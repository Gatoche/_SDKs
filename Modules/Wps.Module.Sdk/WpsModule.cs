using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using wipisoft;

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
/// <para>Le SDK fait HELLO/WELCOME automatiquement au SourceInitialized, mais le READY|hwnd
/// est sous la responsabilité du module — c'est le seul moyen d'avoir le bon timing
/// quand le module a une init asynchrone (browser, charges réseau, etc.).</para>
///
/// <para>Warning automatique si <see cref="NotifyReadyAsync"/> n'est pas appelé dans les 5s
/// suivant le WELCOME → aide les développeurs à diagnostiquer un oubli (sinon le host
/// timeoutera après 30s avec un kill).</para>
///
/// <para>Pour recevoir les hooks lifecycle (close demandé, resize, négociation v1.3, etc.),
/// implémente <see cref="IWpsModule"/> sur la MainWindow et appelle <see cref="Register"/>.</para>
///
/// <para><b>v1.3 :</b> le SDK gère automatiquement la négociation d'arrêt
/// (CAN_CLOSE/CLOSING_DONE), le hook <c>SessionEnding</c> Windows pour le mode shutdown OS,
/// la détection "host figé" (silence PING &gt; 30s), et la coupure du pipe. APIs publiques
/// supplémentaires : <see cref="ReportBusyProgress"/>, <see cref="NotifySelfClosing"/>,
/// <see cref="ResolveCanClose"/>.</para>
/// </summary>
public static class WpsModule
{
    private const int READY_REMINDER_SECONDS = 5;

    /// <summary>(v1.3) Si aucun PING n'a été reçu du host pendant ce délai, on considère le
    /// host figé (UI thread deadlock probable). Le module déclenche
    /// <see cref="IWpsModule.OnHostDisconnected"/>(HeartbeatSilent) puis
    /// <c>Application.Current.Shutdown()</c>.</summary>
    private const int HEARTBEAT_SILENCE_TIMEOUT_SECONDS = 30;

    /// <summary>(v1.3) Période du watchdog qui vérifie le silence PING. 5s = ratio raisonnable
    /// vs le timeout 30s (6 vérifications avant déclenchement).</summary>
    private const int HEARTBEAT_WATCHDOG_PERIOD_SECONDS = 5;

    private static WpsModuleConnection? _connection;
    private static WpsModuleShutdownNegotiator? _negotiator;
    private static IWpsModule? _module;
    private static IntPtr _moduleHwnd;
    private static bool _readySent;
    private static string _logTag = "Wps.Module.Sdk";

    private static DispatcherTimer? _heartbeatWatchdog;
    private static bool _hostDisconnected;  // idempotence : on ne fire qu'une fois

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
    /// handshake HELLO/WELCOME, attache le négociateur de shutdown v1.3, démarre le watchdog
    /// heartbeat et le hook SessionEnding Windows. En standalone, no-op total.
    /// À appeler AVANT <see cref="Window.Show"/>.
    ///
    /// <para>Le module DOIT ensuite appeler <see cref="NotifyReadyAsync"/> quand il est prêt à
    /// être affiché (notion métier propre à chaque module). Sans cet appel, le host
    /// timeoutera après 30s et killera le process — un warning de rappel est loggé à 5s.</para>
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

                // (v1.3) Négociateur de shutdown : créé après la connexion (dépendance circulaire),
                // attaché via setter. Le négociateur encapsule la state machine et la logique
                // d'appel au hook applicatif sur UI thread + clamp urgent.
                _negotiator = new WpsModuleShutdownNegotiator(app.Dispatcher, _module, _connection);
                _connection.AttachNegotiator(_negotiator);

                // (v1.3) Hook coupure pipe : le ReadLoop a terminé (host crashé / killé). Trigger
                // OnHostDisconnected(PipeClosed) et auto-shutdown (option A — DIM vide côté
                // interface, le SDK fait le shutdown automatiquement après le hook).
                _connection.Duplex.Closed += () => HandleHostDisconnected(HostDisconnectReason.PipeClosed);

                await _connection.StartAsync();
                WpsDebugSender.Log($"IPC pipes connected (cmd in, notif out)", LogLevel.Info, _logTag);

                // Handshake HELLO ↔ WELCOME (négociation contract version)
                await _connection.SendHelloAsync(WpsModuleContract.CurrentVersion, moduleName);
                WpsDebugSender.Log($"HELLO sent (version={WpsModuleContract.CurrentVersion})", LogLevel.Trace, _logTag);

                await _connection.WaitForWelcomeAsync();
                WpsDebugSender.Log($"WELCOME received from host — awaiting WpsModule.NotifyReadyAsync() call", LogLevel.Info, _logTag);

                // (v1.3) Démarre le watchdog "silence PING > 30s" maintenant que les PING vont
                // commencer à arriver. Le timer tourne sur le UI thread (Dispatcher) — cohérent
                // avec le reste du SDK qui marshalle déjà tout sur ce thread.
                StartHeartbeatWatchdog(app.Dispatcher);

                // S'abonner à Resume : pendant la veille du PC, aucun PING n'est échangé →
                // sans reset, le watchdog ci-dessus déclencherait immédiatement HeartbeatSilent
                // au réveil et killerait le module. Resume reset le timestamp comme si on
                // venait de recevoir un PING frais → laisse le temps au pipe IPC de reprendre
                // avant le prochain check de silence.
                WpsPowerWatchdog.Resume += OnSystemResume;

                // (v1.3) Hook Windows SessionEnding : déclenche un CAN_CLOSE local urgent quand
                // l'OS demande le shutdown/logoff. Le SDK module gère la coalescing avec un
                // éventuel CAN_CLOSE pipe arrivant en parallèle.
                SystemEvents.SessionEnding += OnSessionEnding;

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
            try { SystemEvents.SessionEnding -= OnSessionEnding; } catch { /* tolérant */ }
            try { WpsPowerWatchdog.Resume -= OnSystemResume; } catch { /* tolérant */ }
            _heartbeatWatchdog?.Stop();
            _heartbeatWatchdog = null;
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
    /// hooks lifecycle (OnHostConnected, OnShutdownRequested, OnResizeRequested,
    /// OnCanCloseRequestedAsync, OnCanCloseAborted, OnHostDisconnected).
    /// <para>L'ordre Register/Bootstrap est indifférent : si Register est appelé après
    /// <see cref="Bootstrap"/>, on propage la référence au négociateur déjà créé. Sans
    /// cette propagation, le négociateur capturait null à sa construction et retournait Ok
    /// par défaut à tous les CAN_CLOSE — le host fermait alors le module instantanément
    /// sans laisser à l'app la possibilité de répondre Busy/NeedUser/Rejected.</para>
    /// </summary>
    public static void Register(IWpsModule module)
    {
        _module = module;
        // Si Bootstrap a déjà couru, le négociateur existe déjà avec _module=null capturé.
        // On lui pousse la nouvelle référence pour que les prochains CAN_CLOSE l'invoquent.
        if (_negotiator is not null) _negotiator.Module = module;
    }

    // ====== v1.3 : APIs publiques pour le shutdown négocié ======

    /// <summary>
    /// (v1.3) Envoie une mise à jour BUSY_PROGRESS au host pendant un Busy long. Reset le
    /// timer de heartbeat côté host (sinon Kill après ~8s de silence).
    /// <para>À appeler à intervalle régulier (~3s) après un retour Busy de
    /// <see cref="IWpsModule.OnCanCloseRequestedAsync"/>. Safe à appeler hors période Busy
    /// (silencieusement ignoré côté SDK — pas d'effet de bord).</para>
    /// </summary>
    /// <param name="progress">Progression (-1 si indéterminé, sinon [0, 100]) + message
    /// affiché par le host dans son overlay.</param>
    public static Task ReportBusyProgress(BusyProgress progress)
    {
        if (!IsEmbedded || _connection is null) return Task.CompletedTask;
        return _connection.SendBusyProgressAsync(progress.Percent, progress.Message);
    }

    /// <summary>
    /// (v1.3) Notifie le host que le module se ferme à son initiative (bouton Quitter dans son
    /// UI, logique métier qui termine, etc.). Permet au host de griser le slot proprement
    /// (état "Closed" plutôt que "Failed" du crash non sollicité).
    /// <para>Pattern typique : appeler ce hook PUIS Window.Close() / Application.Shutdown().
    /// Le host verra Process.Exited peu après et fera son cleanup côté slot.</para>
    /// </summary>
    /// <param name="reason">Texte libre (ex: <c>"user-quit"</c>, <c>"work-done"</c>) — affiché
    /// côté host pour diagnostic.</param>
    public static Task NotifySelfClosing(string reason)
    {
        if (!IsEmbedded || _connection is null) return Task.CompletedTask;
        return _connection.SendSelfClosingAsync(reason);
    }

    /// <summary>
    /// (v1.3) Résolution asynchrone d'un <see cref="IWpsModule.OnCanCloseRequestedAsync"/>
    /// retourné en Busy ou NeedUser. À appeler quand l'app a fini son Busy long ou quand
    /// l'utilisateur a tranché un dialog NeedUser.
    /// <para>Exemple typique :</para>
    /// <code>
    /// // Premier appel : "je suis occupé, ~3s"
    /// public async ValueTask&lt;CanCloseDecision&gt; OnCanCloseRequestedAsync(CanCloseContext ctx)
    /// {
    ///     _ = SaveAndResolve();  // travail fire-and-forget
    ///     return CanCloseDecision.Busy("Sauvegarde en cours...", 3000);
    /// }
    /// private async Task SaveAndResolve()
    /// {
    ///     await _store.SaveAsync();
    ///     await WpsModule.ResolveCanClose(CanCloseDecision.Ok);  // débloque l'attente côté host
    /// }
    /// </code>
    /// </summary>
    public static Task ResolveCanClose(CanCloseDecision decision)
    {
        if (!IsEmbedded || _negotiator is null) return Task.CompletedTask;
        return _negotiator.ResolveAsync(decision);
    }

    // ====== v1.3 : implémentation interne ======

    /// <summary>(v1.3) Démarre le watchdog "silence PING > 30s" sur le UI thread via
    /// DispatcherTimer. Cohérent avec le reste du SDK (tout marshalle sur ce thread).</summary>
    private static void StartHeartbeatWatchdog(Dispatcher dispatcher)
    {
        _heartbeatWatchdog = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(HEARTBEAT_WATCHDOG_PERIOD_SECONDS),
        };
        _heartbeatWatchdog.Tick += (_, _) =>
        {
            if (_connection is null || _hostDisconnected) return;
            // Court-circuit pendant la veille du PC : pas de PING possible, le timestamp
            // ne peut pas avancer. Défensif au cas où le tick fire avant que l'event Resume
            // ne soit propagé (timing serré au réveil).
            if (WpsPowerWatchdog.IsSuspended) return;
            var silenceSecs = (DateTime.UtcNow - _connection.LastPingReceivedUtc).TotalSeconds;
            if (silenceSecs > HEARTBEAT_SILENCE_TIMEOUT_SECONDS)
            {
                WpsDebugSender.Log(
                    $"Heartbeat watchdog: aucun PING reçu depuis {silenceSecs:F0}s (> {HEARTBEAT_SILENCE_TIMEOUT_SECONDS}s) — host figé/mort",
                    LogLevel.Warning, _logTag);
                HandleHostDisconnected(HostDisconnectReason.HeartbeatSilent);
            }
        };
        _heartbeatWatchdog.Start();
    }

    /// <summary>(v1.3) Centralise la gestion de "host disparu" pour les 2 sources de détection
    /// (pipe coupé, silence PING). Idempotent via <see cref="_hostDisconnected"/>. Appelle le
    /// hook applicatif puis fait <c>Application.Current.Shutdown()</c> automatiquement
    /// (option A — l'app peut faire un cleanup synchrone dans le hook avant le shutdown).</summary>
    private static void HandleHostDisconnected(HostDisconnectReason reason)
    {
        if (_hostDisconnected) return;  // idempotent
        _hostDisconnected = true;

        WpsDebugSender.Log($"Host disconnected (reason={reason}) — auto-shutdown after hook",
            LogLevel.Warning, _logTag);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // Pas de WPF Application en cours (cas dégénéré) : direct Environment.Exit.
            try { _module?.OnHostDisconnected(reason); } catch { }
            Environment.Exit(0);
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            try { _module?.OnHostDisconnected(reason); }
            catch (Exception ex)
            {
                WpsDebugSender.Log(
                    $"OnHostDisconnected handler threw {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Warning, _logTag);
            }
            try { Application.Current?.Shutdown(); } catch { /* tolérant */ }
        }));
    }

    /// <summary>Handler du <see cref="WpsPowerWatchdog.Resume"/> : reset le timestamp PING comme
    /// si on venait de recevoir un message frais du host. Sans ce reset, le watchdog
    /// déclencherait HeartbeatSilent immédiatement au réveil du PC (le silence accumulé
    /// pendant la veille dépasse largement les 30s du seuil) et killerait le module alors
    /// que le host est parfaitement vivant — juste momentanément en train de reprendre son
    /// pipe IPC, comme nous.</summary>
    private static void OnSystemResume()
    {
        if (_connection is null) return;
        _connection.ResetHeartbeatTimestamp();
        WpsDebugSender.Log("System Resume → heartbeat watchdog reset (grace period au réveil)",
            LogLevel.Info, _logTag);
    }

    /// <summary>(v1.3) Handler du hook <c>Microsoft.Win32.SystemEvents.SessionEnding</c>.
    /// Déclenche un CAN_CLOSE local en mode urgent (IsUrgent=true). Le négociateur coalesce
    /// avec un éventuel CAN_CLOSE pipe arrivant en parallèle.
    /// <para>Note : <c>SystemEvents.SessionEnding</c> est une notification pure — on ne peut
    /// pas bloquer le shutdown OS depuis ce handler (contrairement à
    /// <c>Application.SessionEnding</c> de WPF qui a un <c>Cancel</c>). On n'essaie de toute
    /// façon pas de bloquer (l'utilisateur a tranché côté OS), on profite juste du budget
    /// temps Windows (~5s par défaut, <c>HungAppTimeout</c>) pour un cleanup best-effort.</para></summary>
    private static void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        if (_negotiator is null) return;
        WpsDebugSender.Log(
            $"SystemEvents.SessionEnding (reason={e.Reason}) → CAN_CLOSE local urgent",
            LogLevel.Info, _logTag);
        var ctx = new CanCloseContext { IsUrgent = true };
        // Fire-and-forget : on ne bloque pas le pump de message Windows. Le négociateur fera
        // son travail (clamp NeedUser/Rejected → Busy, appel hook applicatif, envoi réponse au
        // host). Si le cleanup dépasse le budget Windows ~5s, le process sera tué — limite
        // de l'OS qu'on subit.
        _ = _negotiator.OnCanCloseReceivedAsync(ctx);
    }

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
