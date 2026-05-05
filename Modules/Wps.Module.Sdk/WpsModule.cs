using System.Windows;
using System.Windows.Interop;

namespace Wps.Module;

/// <summary>
/// Point d'entrée du SDK module wipiSoft. Pattern d'utilisation uniforme :
///
/// <code>
/// // App.xaml.cs (TOUS les modules, simples ou complexes)
/// protected override void OnStartup(StartupEventArgs e)
/// {
///     base.OnStartup(e);
///     var window = new MainWindow();
///     WpsModule.Bootstrap(this, window, e.Args);
///     window.Show();
/// }
///
/// // MainWindow.xaml.cs : appeler NotifyReadyAsync quand le module est PRÊT
/// // (notion métier — dépend de chaque module). Exemples :
/// //   - WPF basique : dans Loaded
/// //   - WebView2 : après EnsureCoreWebView2Async
/// //   - CefSharp : dans FrameLoadEnd avec e.Frame.IsMain
/// public partial class MainWindow : Window
/// {
///     public MainWindow()
///     {
///         InitializeComponent();
///         Loaded += async (_, _) => await WpsModule.NotifyReadyAsync();
///     }
/// }
/// </code>
///
/// Le SDK fait HELLO/WELCOME automatiquement au SourceInitialized, mais le READY|hwnd
/// est sous la responsabilité du module — c'est le seul moyen d'avoir le bon timing
/// quand le module a une init asynchrone (browser, charges réseau, etc.).
///
/// Warning automatique si <see cref="NotifyReadyAsync"/> n'est pas appelé dans les 5s
/// suivant le WELCOME → aide les développeurs à diagnostiquer un oubli (sinon le host
/// timeoutera après 30s avec un kill).
///
/// Pour recevoir les hooks lifecycle (close demandé, resize, ...), implémente
/// <see cref="IWpsModule"/> sur la MainWindow et appelle <see cref="Register"/>.
/// </summary>
public static class WpsModule
{
    private const int READY_REMINDER_SECONDS = 5;

    private static WpsModuleConnection? _connection;
    private static IWpsModule? _module;
    private static IntPtr _moduleHwnd;
    private static bool _readySent;
    private static string _logTag = "Wps.Module.Sdk";

    // Signalé quand le handshake HELLO/WELCOME est terminé (pipes prêts, host validé).
    // NotifyReadyAsync attend ce TCS avant d'envoyer le READY → évite la race où Loaded de
    // la MainWindow fire pendant que SourceInitialized async handler est encore en StartAsync.
    private static readonly TaskCompletionSource<bool> _handshakeCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Mode courant : <c>true</c> si lancé par un host (sessionId présent dans args), <c>false</c> en standalone.</summary>
    public static bool IsEmbedded { get; private set; }

    /// <summary>Session ID fourni par le host (vide en standalone).</summary>
    public static string SessionId { get; private set; } = "";

    /// <summary>
    /// Auto-détecte le mode (standalone vs embedded) selon les arguments. En mode embedded,
    /// configure la <paramref name="window"/> pour le slot, ouvre les pipes IPC, fait le
    /// handshake HELLO/WELCOME. En standalone, no-op total. À appeler AVANT <see cref="Window.Show"/>.
    ///
    /// Le module DOIT ensuite appeler <see cref="NotifyReadyAsync"/> quand il est prêt à
    /// être affiché (notion métier propre à chaque module). Sans cet appel, le host
    /// timeoutera après 30s et killera le process — un warning de rappel est loggé à 5s.
    /// </summary>
    public static void Bootstrap(Application app, Window window, string[] args)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        if (window is null) throw new ArgumentNullException(nameof(window));

        var moduleName = window.GetType().Assembly.GetName().Name ?? "Unknown";
        _logTag = $"Wps.Module.Sdk[{moduleName}]";

        SessionId = ParseSession(args);
        IsEmbedded = !string.IsNullOrEmpty(SessionId);
        WpsDebugSender.Log($"Bootstrap: IsEmbedded={IsEmbedded} sessionId='{SessionId}' contractVersion={WpsModuleContract.CurrentVersion}", LogLevel.Info, _logTag);
        if (!IsEmbedded) return;  // standalone → no-op

        // Configure la Window pour le mode embarqué (parking, no chrome, etc.)
        WpsModuleWindowSetup.ConfigureForModuleMode(window);
        WpsDebugSender.Log($"Bootstrap: window configured for embedded mode (parked at -32000)", LogLevel.Trace, _logTag);

        // Démarre l'IPC une fois le HWND créé (= dans SourceInitialized)
        window.SourceInitialized += async (_, _) =>
        {
            try
            {
                _moduleHwnd = new WindowInteropHelper(window).Handle;
                WpsDebugSender.Log($"SourceInitialized: hwnd=0x{_moduleHwnd.ToInt64():X}, opening IPC connection…", LogLevel.Info, _logTag);

                _connection = new WpsModuleConnection(SessionId, app.Dispatcher, _module);
                await _connection.StartAsync();
                WpsDebugSender.Log($"IPC pipes connected (cmd in, notif out)", LogLevel.Info, _logTag);

                // Handshake HELLO ↔ WELCOME (négociation contract version)
                await _connection.SendHelloAsync(WpsModuleContract.CurrentVersion, moduleName);
                WpsDebugSender.Log($"HELLO sent (version={WpsModuleContract.CurrentVersion})", LogLevel.Trace, _logTag);

                await _connection.WaitForWelcomeAsync();
                WpsDebugSender.Log($"WELCOME received from host — awaiting WpsModule.NotifyReadyAsync() call", LogLevel.Info, _logTag);

                // Signale aux NotifyReadyAsync en attente que le handshake est terminé
                _handshakeCompleted.TrySetResult(true);

                _module?.OnHostConnected();

                // Reminder anti-oubli : si le module n'appelle pas NotifyReadyAsync dans les 5s,
                // on log un warning. Le host timeout à 30s et kill.
                StartReadyReminder();
            }
            catch (Exception ex)
            {
                // Pas de catch silent : on log explicitement. Le module reste vivant mais sans IPC ;
                // côté host le LoadAsyncSdk timeoutera après 30s sans Ready et killera le process.
                WpsDebugSender.Log($"IPC startup FAILED: {ex.GetType().Name}: {ex.Message}", LogLevel.Error, _logTag);
            }
        };

        // Cleanup propre à la fermeture du module
        app.Exit += (_, _) =>
        {
            WpsDebugSender.Log($"Application.Exit → disposing IPC connection", LogLevel.Info, _logTag);
            _connection?.Dispose();
        };
    }

    /// <summary>
    /// Envoie le READY|hwnd au host. À appeler par le module quand son contenu métier est prêt
    /// à être affiché (notion propre à chaque module). Idempotent : si déjà appelé, no-op
    /// silencieux. Safe à appeler en standalone : retourne immédiatement.
    /// </summary>
    public static async Task NotifyReadyAsync()
    {
        if (!IsEmbedded) return;             // standalone → no-op
        if (_readySent) return;              // déjà envoyé : idempotent

        // Attendre que le handshake HELLO/WELCOME soit terminé. La MainWindow.Loaded peut fire
        // pendant que SourceInitialized async handler est encore en await sur StartAsync — sans
        // ce wait, NotifyReadyAsync écrirait dans un _writer null → READY perdu silencieusement.
        try
        {
            await _handshakeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            WpsDebugSender.Log($"NotifyReadyAsync: timeout 15s en attente du handshake HELLO/WELCOME — IPC startup probablement échoué", LogLevel.Error, _logTag);
            return;
        }

        if (_connection is null) return;     // bootstrap pas appelé / failed
        if (_moduleHwnd == IntPtr.Zero) return;  // window pas encore HWND
        _readySent = true;

        try
        {
            await _connection.NotifyReadyAsync(_moduleHwnd);
            WpsDebugSender.Log($"READY sent (hwnd=0x{_moduleHwnd.ToInt64():X}) — module fully initialized", LogLevel.Success, _logTag);
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"NotifyReadyAsync FAILED: {ex.GetType().Name}: {ex.Message}", LogLevel.Error, _logTag);
            _readySent = false;  // permet retry
        }
    }

    /// <summary>
    /// Optionnel : enregistre un implémenteur de <see cref="IWpsModule"/> pour recevoir les
    /// hooks lifecycle (OnHostConnected, OnShutdownRequested, OnResizeRequested).
    /// À appeler AVANT <see cref="Bootstrap"/> ou très tôt — typiquement dans le constructeur
    /// de la MainWindow.
    /// </summary>
    public static void Register(IWpsModule module) => _module = module;

    private static void StartReadyReminder()
    {
        _ = Task.Delay(TimeSpan.FromSeconds(READY_REMINDER_SECONDS)).ContinueWith(_ =>
        {
            if (!_readySent && IsEmbedded)
            {
                WpsDebugSender.Log(
                    $"WpsModule.NotifyReadyAsync() pas appelé après {READY_REMINDER_SECONDS}s — " +
                    $"oubli probable dans le code module. Le host timeoutera dans {30 - READY_REMINDER_SECONDS}s et killera le process.",
                    LogLevel.Warning, _logTag);
            }
        });
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
}
