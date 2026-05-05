using System.Globalization;
using System.Windows.Threading;
using Wps.Module.Core;

namespace Wps.Module;

/// <summary>
/// Connexion IPC côté module : utilise <see cref="WpsPipeDuplex"/> pour le transport bas-niveau
/// (pipes nommés conformes à <see cref="WpsModuleContract"/>) et y ajoute la logique métier
/// du contrat wipiSoft : handshake HELLO/WELCOME, dispatch CMD vers <see cref="IWpsModule"/>
/// (CLOSE / RESIZE), reply heartbeat PING → PONG.
///
/// Le heartbeat reply utilise <see cref="Dispatcher.InvokeAsync"/> avec un no-op : si l'UI
/// thread est figé, le dispatch ne complète jamais → pas de PONG envoyé → host détecte le
/// hang. C'est la mécanique cruciale pour distinguer "module crashé" de "module figé".
/// </summary>
internal sealed class WpsModuleConnection : IDisposable
{
    private readonly string _sessionId;
    private readonly Dispatcher _uiDispatcher;
    private readonly IWpsModule? _module;
    private readonly WpsPipeDuplex _duplex;

    // ⚠️ Initialisé dans le constructeur, AVANT que le ReadLoop ou SendHelloAsync ne tournent.
    // Si on l'initialisait dans WaitForWelcomeAsync (appelé après SendHello), il y aurait une
    // race condition : le host répond WELCOME en <20ms, le ReadLoop le reçoit, et
    // _welcomeTcs?.TrySetResult(...) sur un TCS encore null perd le message → timeout 10s.
    // Bug observé sur le 2e module lancé séquentiellement (timing très serré entre slots).
    private readonly TaskCompletionSource<string> _welcomeTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private const string LogTag = "Wps.Module.Sdk";

    public WpsModuleConnection(string sessionId, Dispatcher uiDispatcher, IWpsModule? module)
    {
        _sessionId = sessionId;
        _uiDispatcher = uiDispatcher;
        _module = module;

        // Côté module : reçoit sur "Cmd" (server in), envoie sur "Notif" (client out).
        var cmdPipe = WpsModuleContract.IpcNames.CommandPipe(_sessionId);
        var notifPipe = WpsModuleContract.IpcNames.NotificationPipe(_sessionId);
        _duplex = new WpsPipeDuplex(inboundPipeName: cmdPipe, outboundPipeName: notifPipe, LogTag);
        _duplex.LineReceived += Dispatch;
    }

    /// <summary>Ouvre les pipes via <see cref="WpsPipeDuplex"/>. Démarre la lecture.</summary>
    public Task StartAsync(CancellationToken ct = default) => _duplex.StartAsync(ct);

    /// <summary>Envoie HELLO|version|name|kind. Le host répondra WELCOME ou disconnectera.
    /// Le champ <paramref name="kind"/> est apparu en v1.1 du contrat ; un host plus vieux
    /// l'ignore (parts.Length &gt;= 3 dans son dispatch).</summary>
    public Task SendHelloAsync(string contractVersion, string moduleName,
                               WpsModuleKind kind = WpsModuleKind.Module) =>
        _duplex.SendAsync(FormattableString.Invariant(
            $"{WpsModuleContract.NotifHello}{WpsModuleContract.Separator}{contractVersion}{WpsModuleContract.Separator}{moduleName}{WpsModuleContract.Separator}{kind}"));

    /// <summary>Attend le WELCOME du host (timeout 10s). Lève si pas reçu ou pipe coupé.
    /// Le TCS est créé dans le constructeur (anti-race) ; ici on ajoute juste la trigger timeout.</summary>
    public Task<string> WaitForWelcomeAsync(TimeSpan? timeout = null)
    {
        var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        cts.Token.Register(() => _welcomeTcs.TrySetException(new TimeoutException("WELCOME not received")));
        return _welcomeTcs.Task;
    }

    /// <summary>Notifie READY|hwnd au host. À appeler quand la fenêtre est prête à être embarquée.</summary>
    public Task NotifyReadyAsync(IntPtr hwnd) =>
        _duplex.SendAsync(FormattableString.Invariant(
            $"{WpsModuleContract.NotifReady}{WpsModuleContract.Separator}{hwnd.ToInt64()}"));

    private void Dispatch(string line)
    {
        var parts = line.Split(WpsModuleContract.Separator);
        switch (parts[0])
        {
            case WpsModuleContract.CmdWelcome:
                _welcomeTcs.TrySetResult(parts.Length > 1 ? parts[1] : "");
                break;

            case WpsModuleContract.CmdClose:
                // Hook applicatif ; sinon on tombe en standalone-shutdown via Application.Current
                _uiDispatcher.BeginInvoke(new Action(() =>
                {
                    if (_module is not null) _module.OnShutdownRequested();
                    else System.Windows.Application.Current?.Shutdown();
                }));
                break;

            case WpsModuleContract.CmdResize when parts.Length == 4:
                if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                    && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
                    && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi))
                {
                    _uiDispatcher.BeginInvoke(new Action(() => _module?.OnResizeRequested(w, h, dpi)));
                }
                break;

            case WpsModuleContract.CmdPing:
                _ = HandlePingAsync();
                break;
        }
    }

    private async Task HandlePingAsync()
    {
        try
        {
            // Dispatch un no-op sur le UI thread → si l'UI est figée, ce InvokeAsync ne complétera
            // jamais et on n'enverra pas de PONG. C'est la sonde qui distingue process vivant
            // mais UI bloqué → host signalera module figé.
            await _uiDispatcher.InvokeAsync(() => { });
            await _duplex.SendAsync(WpsModuleContract.NotifPong).ConfigureAwait(false);
        }
        catch { /* dispatcher disposed ou pipe cassé : pas de pong, normal */ }
    }

    public void Dispose() => _duplex.Dispose();
}
