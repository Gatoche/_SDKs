using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using wipisoft;
using Wps.Module;
using Wps.Module.Core;

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
/// <para>Détection de mode (standalone vs embedded) via le flag <c>--wps-session</c> dans args.
/// En standalone : <see cref="BootstrapAsync"/> retourne immédiatement, les Register* sont
/// no-op, <see cref="RunAsync"/> bloque jusqu'à Ctrl+C — utile pour tester le service en
/// isolation (avec un harness qui injecte des appels directs).</para>
///
/// <para><b>v1.3 :</b> support du shutdown négocié (CAN_CLOSE/CLOSING_DONE), hook
/// <c>SessionEnding</c> Windows pour le mode shutdown OS, watchdog "host figé" (silence PING
/// &gt; 30s), détection coupure pipe. APIs publiques supplémentaires :
/// <see cref="Register"/> (hook lifecycle <see cref="IWpsModule"/>),
/// <see cref="ReportBusyProgress"/>, <see cref="NotifySelfClosing"/>,
/// <see cref="ResolveCanClose"/>.</para>
/// </summary>
public static class WpsModuleService
{
    private const int HEARTBEAT_SILENCE_TIMEOUT_SECONDS = 30;
    private const int HEARTBEAT_WATCHDOG_PERIOD_SECONDS = 5;

    private static WpsModuleServiceConnection? _connection;
    private static WpsModuleServiceShutdownNegotiator? _negotiator;
    private static IWpsModule? _module;
    private static string _logTag = "Wps.ModuleService.Sdk";
    private static readonly TaskCompletionSource<bool> _shutdownTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static System.Threading.Timer? _heartbeatWatchdog;
    private static bool _hostDisconnected;  // idempotence

    /// <summary>Mode courant : <c>true</c> si lancé par un host (sessionId présent dans args).</summary>
    public static bool IsEmbedded { get; private set; }

    /// <summary>Session ID fourni par le host (vide en standalone).</summary>
    public static string SessionId { get; private set; } = "";

    /// <summary>
    /// Initialise le SDK : parse les args, ouvre les pipes IPC, fait le handshake HELLO/WELCOME
    /// avec <see cref="WpsModuleKind.ModuleService"/>, attache le négociateur de shutdown v1.3,
    /// démarre le watchdog heartbeat et le hook SessionEnding Windows. À appeler en début de
    /// Main avant d'enregistrer les handlers Invoke.
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

            // (v1.3) Négociateur de shutdown : créé après la connexion (dépendance circulaire).
            _negotiator = new WpsModuleServiceShutdownNegotiator(_module, _connection);
            _connection.AttachNegotiator(_negotiator);

            // Events legacy ShutdownRequested / PipeClosed conservés pour rétrocompat (services
            // pré-v1.3 comme TracePML qui s'y abonnent directement). En v1.3 le négociateur les
            // double avec le hook applicatif IWpsModule.OnShutdownRequested.
            _connection.ShutdownRequested += () =>
            {
                WpsDebugSender.Log($"CLOSE reçu du host → fin de RunAsync", LogLevel.Info, _logTag);
                _shutdownTcs.TrySetResult(true);
            };
            _connection.PipeClosed += () =>
            {
                WpsDebugSender.Log($"pipe coupé par le host (EOF) → fin de RunAsync", LogLevel.Info, _logTag);
                // (v1.3) Trigger OnHostDisconnected(PipeClosed) avant de débloquer RunAsync. L'app
                // peut faire un cleanup d'urgence dans le hook.
                HandleHostDisconnected(HostDisconnectReason.PipeClosed);
                _shutdownTcs.TrySetResult(true);
            };

            await _connection.StartAsync();
            WpsDebugSender.Log($"IPC pipes connected (cmd in, notif out)", LogLevel.Info, _logTag);

            await _connection.SendHelloAsync(WpsModuleContract.CurrentVersion, serviceName);
            WpsDebugSender.Log($"HELLO sent (version={WpsModuleContract.CurrentVersion}, kind=ModuleService)", LogLevel.Trace, _logTag);

            await _connection.WaitForWelcomeAsync();
            WpsDebugSender.Log($"WELCOME received from host — service ready to register handlers", LogLevel.Info, _logTag);

            // (v1.3) Démarre le watchdog "silence PING > 30s". Utilise System.Threading.Timer
            // (pas DispatcherTimer comme côté Module classique) car le ModuleService peut être
            // une console pure sans Application WPF — le timer ThreadPool fonctionne dans tous
            // les cas.
            StartHeartbeatWatchdog();

            // S'abonner à Resume : pendant la veille du PC, aucun PING n'est échangé.
            // Sans reset, le watchdog déclencherait immédiatement HeartbeatSilent au réveil
            // et terminerait le service alors que le host est vivant. Cf. WpsPowerWatchdog.
            WpsPowerWatchdog.Resume += OnSystemResume;

            // (v1.3) Hook Windows SessionEnding : déclenche un CAN_CLOSE local urgent au
            // shutdown OS. Fonctionne au niveau process (message-only window WPF — créée
            // automatiquement dès qu'on touche à Application/Dispatcher), aussi efficace pour
            // une console pure car SystemEvents installe son propre pump message si nécessaire.
            SystemEvents.SessionEnding += OnSessionEnding;
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"BootstrapAsync FAILED: {ex.GetType().Name}: {ex.Message}", LogLevel.Error, _logTag);
            throw;
        }
    }

    /// <summary>(v1.3) Optionnel : enregistre un implémenteur de <see cref="IWpsModule"/> pour
    /// recevoir les hooks lifecycle (<c>OnCanCloseRequestedAsync</c>, <c>OnCanCloseAborted</c>,
    /// <c>OnShutdownRequested</c>, <c>OnHostDisconnected</c>).
    /// <para>L'ordre Register/Bootstrap est indifférent : si Register est appelé après
    /// <see cref="BootstrapAsync"/>, on propage la référence au négociateur déjà créé. Sans
    /// cette propagation, le négociateur capturait null à sa construction et retournait Ok
    /// par défaut à tous les CAN_CLOSE — le host fermait alors le service instantanément
    /// sans laisser à l'app la possibilité de répondre Busy/NeedUser/Rejected.</para></summary>
    public static void Register(IWpsModule module)
    {
        _module = module;
        // Si Bootstrap a déjà couru, le négociateur existe déjà avec _module=null capturé.
        // On lui pousse la nouvelle référence pour que les prochains CAN_CLOSE l'invoquent.
        if (_negotiator is not null) _negotiator.Module = module;
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
                _ = dispatcher.BeginInvoke(show);
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
        try { SystemEvents.SessionEnding -= OnSessionEnding; } catch { /* tolérant */ }
        try { WpsPowerWatchdog.Resume -= OnSystemResume; } catch { /* tolérant */ }
        _heartbeatWatchdog?.Dispose();
        _heartbeatWatchdog = null;
        _connection?.Dispose();
        WpsDebugSender.Log($"RunAsync exit — service shutting down", LogLevel.Info, _logTag);
    }

    // ====== v1.3 : APIs publiques pour le shutdown négocié ======

    /// <summary>(v1.3) Envoie une mise à jour BUSY_PROGRESS au host pendant un Busy long. À
    /// appeler à intervalle régulier (~3s) après un retour Busy de
    /// <see cref="IWpsModule.OnCanCloseRequestedAsync"/>. Safe à appeler hors période Busy
    /// (silencieusement ignoré côté SDK).</summary>
    public static Task ReportBusyProgress(BusyProgress progress)
    {
        if (!IsEmbedded || _connection is null) return Task.CompletedTask;
        return _connection.SendBusyProgressAsync(progress.Percent, progress.Message);
    }

    /// <summary>(v1.3) Notifie le host que le service se ferme à son initiative (bouton Quitter
    /// dans la settings window, logique métier qui termine, etc.). Permet au host de griser le
    /// slot proprement (état "Closed" plutôt que "Failed").</summary>
    public static Task NotifySelfClosing(string reason)
    {
        if (!IsEmbedded || _connection is null) return Task.CompletedTask;
        return _connection.SendSelfClosingAsync(reason);
    }

    /// <summary>(v1.3) Résolution asynchrone d'un <see cref="IWpsModule.OnCanCloseRequestedAsync"/>
    /// retourné en Busy ou NeedUser. À appeler quand l'app a fini son Busy long ou quand
    /// l'utilisateur a tranché un dialog NeedUser.</summary>
    public static Task ResolveCanClose(CanCloseDecision decision)
    {
        if (!IsEmbedded || _negotiator is null) return Task.CompletedTask;
        return _negotiator.ResolveAsync(decision);
    }

    // ====== v1.3 : implémentation interne ======

    /// <summary>(v1.3) Démarre le watchdog "silence PING > 30s" sur le ThreadPool via
    /// System.Threading.Timer. Pas de dépendance Application WPF (un ModuleService peut être
    /// console pure). À l'expiration du timeout, déclenche
    /// <see cref="HandleHostDisconnected"/>(HeartbeatSilent).</summary>
    private static void StartHeartbeatWatchdog()
    {
        var period = TimeSpan.FromSeconds(HEARTBEAT_WATCHDOG_PERIOD_SECONDS);
        _heartbeatWatchdog = new System.Threading.Timer(_ =>
        {
            if (_connection is null || _hostDisconnected) return;
            // Court-circuit pendant la veille : pas de PING possible, le timestamp ne peut
            // pas avancer. Défensif au cas où le tick fire avant que l'event Resume soit
            // propagé (timing serré au réveil).
            if (WpsPowerWatchdog.IsSuspended) return;
            var silenceSecs = (DateTime.UtcNow - _connection.LastPingReceivedUtc).TotalSeconds;
            if (silenceSecs > HEARTBEAT_SILENCE_TIMEOUT_SECONDS)
            {
                WpsDebugSender.Log(
                    $"Heartbeat watchdog: aucun PING reçu depuis {silenceSecs:F0}s (> {HEARTBEAT_SILENCE_TIMEOUT_SECONDS}s) — host figé/mort",
                    LogLevel.Warning, _logTag);
                HandleHostDisconnected(HostDisconnectReason.HeartbeatSilent);
            }
        }, null, period, period);
    }

    /// <summary>(v1.3) Centralise la gestion de "host disparu" pour les 2 sources (pipe coupé,
    /// silence PING). Idempotent. Appelle le hook applicatif puis débloque le RunAsync — l'app
    /// sortira de sa boucle proprement (vs Application.Current.Shutdown côté Module classique
    /// qui force un shutdown WPF immédiat).</summary>
    private static void HandleHostDisconnected(HostDisconnectReason reason)
    {
        if (_hostDisconnected) return;  // idempotent
        _hostDisconnected = true;

        WpsDebugSender.Log($"Host disconnected (reason={reason}) — exiting RunAsync after hook",
            LogLevel.Warning, _logTag);

        // Marshalling conditionnel : si Application.Current existe (settings window WPF),
        // appel sur Dispatcher ; sinon direct.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        Action invoke = () =>
        {
            try { _module?.OnHostDisconnected(reason); }
            catch (Exception ex)
            {
                WpsDebugSender.Log(
                    $"OnHostDisconnected handler threw {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Warning, _logTag);
            }
        };
        if (dispatcher is not null && !dispatcher.CheckAccess())
            _ = dispatcher.BeginInvoke(invoke);
        else
            invoke();

        // Débloque RunAsync (idempotent grâce au TCS)
        _shutdownTcs.TrySetResult(true);
    }

    /// <summary>Handler du <see cref="WpsPowerWatchdog.Resume"/> : reset le timestamp PING comme
    /// si on venait d'en recevoir un frais. Sans ce reset, le watchdog déclencherait
    /// HeartbeatSilent immédiatement au réveil (le silence accumulé pendant la veille
    /// dépasse 30s) et terminerait le service alors que le host est vivant.</summary>
    private static void OnSystemResume()
    {
        if (_connection is null) return;
        _connection.ResetHeartbeatTimestamp();
        WpsDebugSender.Log("System Resume → heartbeat watchdog reset (grace period au réveil)",
            LogLevel.Info, _logTag);
    }

    /// <summary>(v1.3) Handler du hook <c>SystemEvents.SessionEnding</c> côté ModuleService.
    /// Identique au Module classique : fire-and-forget un CAN_CLOSE local urgent au négociateur.</summary>
    private static void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        if (_negotiator is null) return;
        WpsDebugSender.Log(
            $"SystemEvents.SessionEnding (reason={e.Reason}) → CAN_CLOSE local urgent",
            LogLevel.Info, _logTag);
        var ctx = new CanCloseContext { IsUrgent = true };
        _ = _negotiator.OnCanCloseReceivedAsync(ctx);
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

    /// <summary>Force la <paramref name="window"/> au premier plan + clic synthétique
    /// pour stopper l'alerte taskbar. À appeler après <see cref="Window.Show"/>. Utile
    /// aussi pour ramener une fenêtre déjà ouverte (cas d'un 2e SHOW_SETTINGS).
    ///
    /// <para>Implémentation : déléguée à <see cref="wipisoft.WpsForegroundBringer.BringToForegroundAndClick"/>
    /// (helper partagé via <c>_libs/</c> avec les apps wipiSoft standalone).</para>
    /// </summary>
    public static void BringToForegroundAndClick(Window window)
        => wipisoft.WpsForegroundBringer.BringToForegroundAndClick(window);
}
