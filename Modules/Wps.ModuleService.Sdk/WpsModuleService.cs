using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Wps.Module;

namespace Wps.ModuleService;

/// <summary>
/// Point d'entrée du SDK ModuleService wipiSoft. Pattern d'utilisation typique (console app) :
///
/// <code>
/// // Program.cs
/// public static async Task Main(string[] args)
/// {
///     await WpsModuleService.BootstrapAsync(args);
///
///     WpsModuleService.RegisterInvokeHandler&lt;EchoParams, EchoResult&gt;("Echo", async p =>
///     {
///         await Task.CompletedTask;
///         return new EchoResult { Reply = $"hello, {p.Name}" };
///     });
///
///     await WpsModuleService.NotifyReadyAsync();
///     await WpsModuleService.RunAsync();  // bloque jusqu'au shutdown demandé par le Host
/// }
/// </code>
///
/// Détection de mode (standalone vs embedded) via le flag <c>--wps-session</c> dans args.
/// En standalone : <see cref="BootstrapAsync"/> retourne immédiatement, les Register* sont
/// no-op, <see cref="RunAsync"/> bloque jusqu'à Ctrl+C — utile pour tester le service
/// en isolation (avec un harness qui injecte des appels directs).
/// </summary>
public static class WpsModuleService
{
    private static WpsModuleServiceConnection? _connection;
    private static string _logTag = "Wps.ModuleService.Sdk";
    private static readonly TaskCompletionSource<bool> _shutdownTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Mode courant : <c>true</c> si lancé par un host (sessionId présent dans args).</summary>
    public static bool IsEmbedded { get; private set; }

    /// <summary>Session ID fourni par le host (vide en standalone).</summary>
    public static string SessionId { get; private set; } = "";

    /// <summary>
    /// Initialise le SDK : parse les args, ouvre les pipes IPC, fait le handshake HELLO/WELCOME
    /// avec <see cref="WpsModuleKind.ModuleService"/>. À appeler en début de Main avant d'enregistrer
    /// les handlers Invoke.
    /// </summary>
    public static async Task BootstrapAsync(string[] args)
    {
        var serviceName = Assembly.GetEntryAssembly()?.GetName().Name ?? "Unknown";
        _logTag = $"Wps.ModuleService.Sdk[{serviceName}]";

        SessionId = ParseSession(args);
        IsEmbedded = !string.IsNullOrEmpty(SessionId);
        WpsDebugSender.Log(
            $"BootstrapAsync: IsEmbedded={IsEmbedded} sessionId='{SessionId}' contractVersion={WpsModuleContract.CurrentVersion}",
            LogLevel.Info, _logTag);

        if (!IsEmbedded) return;  // standalone → no-op, RunAsync bloque jusqu'à Ctrl+C

        try
        {
            _connection = new WpsModuleServiceConnection(SessionId);
            _connection.ShutdownRequested += () =>
            {
                WpsDebugSender.Log($"CLOSE reçu du host → fin de RunAsync", LogLevel.Info, _logTag);
                _shutdownTcs.TrySetResult(true);
            };
            _connection.PipeClosed += () =>
            {
                WpsDebugSender.Log($"pipe coupé par le host (EOF) → fin de RunAsync", LogLevel.Info, _logTag);
                _shutdownTcs.TrySetResult(true);
            };

            await _connection.StartAsync();
            WpsDebugSender.Log($"IPC pipes connected (cmd in, notif out)", LogLevel.Info, _logTag);

            await _connection.SendHelloAsync(WpsModuleContract.CurrentVersion, serviceName);
            WpsDebugSender.Log($"HELLO sent (version={WpsModuleContract.CurrentVersion}, kind=ModuleService)", LogLevel.Trace, _logTag);

            await _connection.WaitForWelcomeAsync();
            WpsDebugSender.Log($"WELCOME received from host — service ready to register handlers", LogLevel.Info, _logTag);
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"BootstrapAsync FAILED: {ex.GetType().Name}: {ex.Message}", LogLevel.Error, _logTag);
            throw;
        }
    }

    /// <summary>
    /// Enregistre un handler typé pour une méthode invocable. Le SDK gère sérialisation /
    /// désérialisation JSON (System.Text.Json) entre le wire-protocol et les types <typeparamref name="TParams"/>
    /// / <typeparamref name="TResult"/>.
    /// <para>L'appel <see cref="System.Text.Json.JsonSerializer.Deserialize"/> tolère un peer qui
    /// envoie des champs additionnels — la rétrocompat ascendante du payload reste donc à la
    /// charge des types métier (exemple : utiliser des nullable / valeurs par défaut).</para>
    /// </summary>
    public static void RegisterInvokeHandler<TParams, TResult>(string method, Func<TParams, Task<TResult>> handler)
        where TParams : class
    {
        if (_connection is null) return;  // standalone → silent no-op
        _connection.RegisterInvokeHandler(method, async json =>
        {
            var paramsTyped = json.Deserialize<TParams>()
                              ?? throw new InvalidOperationException($"Invoke '{method}' params deserialized to null");
            var result = await handler(paramsTyped).ConfigureAwait(false);
            return JsonSerializer.Serialize(result);
        });
    }

    /// <summary>Enregistre une factory de fenêtre WPF de paramétrage. Le SDK ouvre cette fenêtre
    /// en réponse à un <see cref="WpsModuleContract.CmdShowSettings"/> reçu du Host. Si le service
    /// est une console pure sans paramètres, ne pas appeler cette méthode.
    /// <para>Le SDK marshalle automatiquement l'appel sur le UI thread WPF
    /// (<see cref="System.Windows.Application.Current"/>.Dispatcher) — la factory peut donc
    /// retourner directement une instance créée sur le UI thread sans risque de cross-thread.</para></summary>
    public static void RegisterSettingsWindow(Func<System.Windows.Window> factory)
    {
        if (_connection is null) return;
        _connection.SetShowSettingsHandler(() =>
        {
            // Le handler est invoqué depuis le ReadLoop IPC (ThreadPool). On marshalle sur le
            // UI thread WPF si Application.Current est dispo (cas standard d'un ModuleService
            // avec UI WPF), sinon fallback inline (console pure ou cas inhabituel).
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            Action show = () =>
            {
                try
                {
                    var window = factory();
                    window.Show();
                    BringToForegroundAndClick(window);
                }
                catch (Exception ex)
                {
                    WpsDebugSender.Log($"Settings window factory threw {ex.GetType().Name}: {ex.Message}",
                        LogLevel.Warning, _logTag);
                }
            };
            if (dispatcher is not null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(show);
            else
                show();
        });
    }

    /// <summary>Envoie READY|0 au Host (signale "service prêt à recevoir des Invoke").
    /// À appeler après l'enregistrement de tous les handlers et l'init asynchrone éventuelle.
    /// Idempotent et safe en standalone (no-op).</summary>
    public static async Task NotifyReadyAsync()
    {
        if (_connection is null) return;
        try
        {
            await _connection.NotifyReadyAsync();
            WpsDebugSender.Log($"READY sent — service fully initialized", LogLevel.Success, _logTag);
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"NotifyReadyAsync FAILED: {ex.GetType().Name}: {ex.Message}", LogLevel.Error, _logTag);
        }
    }

    /// <summary>Boucle d'attente. Bloque jusqu'à ce que le Host demande CLOSE ou que le pipe soit
    /// coupé (cas du Host qui meurt). En standalone (pas embedded), bloque jusqu'à Ctrl+C
    /// (Console.CancelKeyPress) si une console est attachée — sinon (cas <c>OutputType=WinExe</c>,
    /// recommandé pour un ModuleService strictement silencieux), il faut tuer le process via
    /// le Task Manager pour le terminer. Le hook Console.CancelKeyPress reste posé sans danger
    /// en WinExe (no-op silencieux, jamais déclenché).</summary>
    public static async Task RunAsync()
    {
        if (!IsEmbedded)
        {
            WpsDebugSender.Log($"RunAsync (standalone) — waiting for Ctrl+C ou kill", LogLevel.Info, _logTag);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; _shutdownTcs.TrySetResult(true); };
        }
        await _shutdownTcs.Task;
        _connection?.Dispose();
        WpsDebugSender.Log($"RunAsync exit — service shutting down", LogLevel.Info, _logTag);
    }

    private static string ParseSession(string[] args)
    {
        if (args is null) return "";
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], WpsModuleContract.SessionArgFlag, StringComparison.Ordinal))
                return args[i + 1] ?? "";
        }
        return "";
    }

    // ====== BringToForegroundAndClick : amène la fenêtre au premier plan + clic synth ======
    //
    // Quand le service reçoit SHOW_SETTINGS du host, l'host est encore le foreground actif et
    // Windows refuse d'arracher le focus à un process tiers (sécurité Win10/11). Résultat :
    // window.Show() + Activate() ne suffit pas — la fenêtre apparaît mais reste derrière, et
    // l'icône taskbar clignote pour signaler "tu as une nouvelle fenêtre".
    //
    // Solution standard Win32 : AttachThreadInput sur le thread du foreground actif (cela
    // donne à notre thread la même "input queue" → SetForegroundWindow accepté). Puis un clic
    // synthétique via PostMessage(WM_LBUTTONDOWN/UP) : l'OS considère qu'un input a été reçu
    // dans la fenêtre → arrête le clignotement taskbar.

    /// <summary>Force la <paramref name="window"/> au premier plan via AttachThreadInput, puis
    /// envoie un clic synthétique (WM_LBUTTONDOWN/UP) au HWND pour stopper l'alerte taskbar.
    /// À appeler après <see cref="Window.Show"/>. Utile aussi pour ramener une fenêtre déjà
    /// ouverte (cas d'un 2e SHOW_SETTINGS).</summary>
    public static void BringToForegroundAndClick(Window window)
    {
        if (window is null) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        var fg = GetForegroundWindow();
        uint thisThread = GetCurrentThreadId();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        bool attached = false;
        if (thisThread != fgThread && fgThread != 0)
            attached = AttachThreadInput(fgThread, thisThread, true);

        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            window.Activate();

            // Clic synthétique via PostMessage : pas besoin de bouger le curseur, l'OS
            // considère qu'un input a été reçu → stoppe l'alerte taskbar.
            const int WM_LBUTTONDOWN = 0x0201;
            const int WM_LBUTTONUP = 0x0202;
            PostMessage(hwnd, WM_LBUTTONDOWN, IntPtr.Zero, IntPtr.Zero);
            PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            if (attached) AttachThreadInput(fgThread, thisThread, false);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
}
