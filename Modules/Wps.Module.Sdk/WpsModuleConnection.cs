using System.Globalization;
using System.Windows.Threading;
using Wps.Module.Core;

namespace Wps.Module;

/// <summary>
/// Connexion IPC côté module : utilise <see cref="WpsPipeDuplex"/> pour le transport bas-niveau
/// (pipes nommés conformes à <see cref="WpsModuleContract"/>) et y ajoute la logique métier
/// du contrat wipiSoft : handshake HELLO/WELCOME, dispatch CMD vers <see cref="IWpsModule"/>
/// (CLOSE / RESIZE / CAN_CLOSE / CAN_CLOSE_ABORTED), reply heartbeat PING → PONG, envois des
/// notifications Module → Host (READY, CAN_CLOSE_OK/BUSY/NEED_USER/REJECTED, BUSY_PROGRESS,
/// CLOSING_DONE, SELF_CLOSING).
///
/// <para>Le heartbeat reply utilise <see cref="Dispatcher.InvokeAsync"/> avec un no-op : si
/// l'UI thread est figé, le dispatch ne complète jamais → pas de PONG envoyé → host détecte
/// le hang. C'est la mécanique cruciale pour distinguer "module crashé" de "module figé".</para>
///
/// <para><b>v1.3 :</b> la logique de négociation d'arrêt (state machine, clamp urgent, appel
/// au hook applicatif sur UI thread) est déléguée à <see cref="WpsModuleShutdownNegotiator"/>
/// pour garder cette classe focalisée sur le transport et le routing. Le tracking
/// <see cref="LastPingReceivedUtc"/> permet à <see cref="WpsModule"/> d'implémenter le
/// watchdog "host figé" (pas de PING reçu &gt; 30s).</para>
/// </summary>
internal sealed class WpsModuleConnection : IDisposable
{
    private readonly string _sessionId;
    private readonly Dispatcher _uiDispatcher;
    private readonly IWpsModule? _module;
    private readonly WpsPipeDuplex _duplex;

    /// <summary>Négociateur shutdown v1.3, créé après le constructeur (cf. <see cref="AttachNegotiator"/>).
    /// La référence circulaire négociateur ↔ connexion est résolue avec ce setter explicite :
    /// la connexion existe en premier (handshake HELLO/WELCOME), le négociateur est attaché
    /// après pour traiter les CAN_CLOSE qui viendront éventuellement.</summary>
    private WpsModuleShutdownNegotiator? _negotiator;

    // ⚠️ Initialisé dans le constructeur, AVANT que le ReadLoop ou SendHelloAsync ne tournent.
    // Si on l'initialisait dans WaitForWelcomeAsync (appelé après SendHello), il y aurait une
    // race condition : le host répond WELCOME en <20ms, le ReadLoop le reçoit, et
    // _welcomeTcs?.TrySetResult(...) sur un TCS encore null perd le message → timeout 10s.
    // Bug observé sur le 2e module lancé séquentiellement (timing très serré entre slots).
    private readonly TaskCompletionSource<string> _welcomeTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>(v1.3) Timestamp UTC du dernier PING reçu du host. Permet à <see cref="WpsModule"/>
    /// d'implémenter le watchdog "host figé" : si silence &gt; 30s, on considère le host mort
    /// et on déclenche <see cref="IWpsModule.OnHostDisconnected"/>(HeartbeatSilent).</summary>
    public DateTime LastPingReceivedUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Expose le pipe duplex pour permettre à <see cref="WpsModule"/> de s'abonner à
    /// l'event <see cref="WpsPipeDuplex.Closed"/> et déclencher
    /// <see cref="IWpsModule.OnHostDisconnected"/>(PipeClosed) à la coupure.</summary>
    public WpsPipeDuplex Duplex => _duplex;

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

    /// <summary>Attache le négociateur de shutdown (création différée pour résoudre la
    /// dépendance circulaire négociateur → connexion). Appelé par <see cref="WpsModule.Bootstrap"/>
    /// après création de la connexion.</summary>
    public void AttachNegotiator(WpsModuleShutdownNegotiator negotiator) => _negotiator = negotiator;

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

    // ====== v1.3 : envois Module → Host ======

    /// <summary>(v1.3) Envoie CAN_CLOSE_OK : le module est libre de fermer.</summary>
    public Task SendCanCloseOkAsync() =>
        _duplex.SendAsync(WpsModuleContract.NotifCanCloseOk);

    /// <summary>(v1.3) Envoie CAN_CLOSE_BUSY|estimatedMs|reason. Le host attend ensuite des
    /// BUSY_PROGRESS périodiques + un CAN_CLOSE_OK final.</summary>
    public Task SendCanCloseBusyAsync(int estimatedMs, string reason) =>
        _duplex.SendAsync(FormattableString.Invariant(
            $"{WpsModuleContract.NotifCanCloseBusy}{WpsModuleContract.Separator}{estimatedMs}{WpsModuleContract.Separator}{reason ?? ""}"));

    /// <summary>(v1.3 final) Envoie <c>CAN_CLOSE_NEED_USER|jsonPayload</c>. Le HOST affiche
    /// la modale (pas le module) — le module ne fait que déclarer la question + les boutons
    /// via <see cref="NeedUserPayload"/>. Le résultat revient via
    /// <see cref="WpsModuleContract.CmdUserResponse"/> que le négociateur mappe en Ok ou
    /// Rejected pour les ids réservés (yes/ok/no/cancel) — pour les ids custom, l'app
    /// override via <see cref="IWpsModule.OnUserResponseAsync"/>.</summary>
    public Task SendCanCloseNeedUserAsync(NeedUserPayload payload) =>
        _duplex.SendAsync(
            $"{WpsModuleContract.NotifCanCloseNeedUser}{WpsModuleContract.Separator}{payload.Serialize()}");

    /// <summary>(v1.3) Envoie CAN_CLOSE_REJECTED|reason. Le host annule sa fermeture en cascade.</summary>
    public Task SendCanCloseRejectedAsync(string reason) =>
        _duplex.SendAsync($"{WpsModuleContract.NotifCanCloseRejected}{WpsModuleContract.Separator}{reason ?? ""}");

    /// <summary>(v1.3) Envoie BUSY_PROGRESS|percent|message. À envoyer toutes les ~3s pendant
    /// un Busy long pour rester en deça du heartbeat timeout 8s côté host.</summary>
    public Task SendBusyProgressAsync(int percent, string message) =>
        _duplex.SendAsync(FormattableString.Invariant(
            $"{WpsModuleContract.NotifBusyProgress}{WpsModuleContract.Separator}{percent}{WpsModuleContract.Separator}{message ?? ""}"));

    /// <summary>(v1.3) Envoie CLOSING_DONE — phase finale, le process va exit. Le host
    /// considère le module proprement fermé (vs Kill fallback).</summary>
    public Task SendClosingDoneAsync() =>
        _duplex.SendAsync(WpsModuleContract.NotifClosingDone);

    /// <summary>(v1.3) Envoie SELF_CLOSING|reason — le module se ferme à son initiative
    /// (bouton Quitter dans son UI, logique métier). Permet au host de griser le slot
    /// proprement (état "Closed" plutôt que "Failed" du crash non sollicité).</summary>
    public Task SendSelfClosingAsync(string reason) =>
        _duplex.SendAsync($"{WpsModuleContract.NotifSelfClosing}{WpsModuleContract.Separator}{reason ?? ""}");

    // ====== Dispatch des messages reçus du host ======

    private void Dispatch(string line)
    {
        var parts = line.Split(WpsModuleContract.Separator);
        switch (parts[0])
        {
            case WpsModuleContract.CmdWelcome:
                _welcomeTcs.TrySetResult(parts.Length > 1 ? parts[1] : "");
                break;

            case WpsModuleContract.CmdClose:
                // v1.3 : si négociateur attaché, route via la state machine (qui appellera
                // OnShutdownRequested + enverra CLOSING_DONE proprement). Si pas de négociateur
                // (cas dégénéré : Bootstrap a échoué avant AttachNegotiator), fallback legacy
                // direct.
                if (_negotiator is not null)
                    _ = _negotiator.OnCloseReceivedAsync();
                else
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
                LastPingReceivedUtc = DateTime.UtcNow;  // (v1.3) reset watchdog "host figé"
                _ = HandlePingAsync();
                break;

            // ====== v1.3 : nouveaux messages ======

            case WpsModuleContract.CmdCanClose:
                {
                    // Format : CAN_CLOSE|isUrgent (0 ou 1)
                    var isUrgent = parts.Length >= 2 && parts[1] == "1";
                    var ctx = new CanCloseContext { IsUrgent = isUrgent };
                    if (_negotiator is not null)
                        _ = _negotiator.OnCanCloseReceivedAsync(ctx);
                    else
                        // Pas de négociateur : on répond Ok par défaut pour ne pas bloquer.
                        _ = SendCanCloseOkAsync();
                }
                break;

            case WpsModuleContract.CmdCanCloseAborted:
                _negotiator?.OnCanCloseAborted();
                break;

            case WpsModuleContract.CmdUserResponse when parts.Length >= 2:
                // Format v1.3 final : USER_RESPONSE|buttonId (string libre, défini par l'app
                // via le dictionnaire answers du NeedUserPayload). Le négociateur mappe les
                // ids réservés (yes/ok/no/cancel) en standard, et délègue à l'app via la DIM
                // OnUserResponseAsync pour les ids custom.
                _ = _negotiator?.OnUserResponseReceived(parts[1]);
                break;

            case WpsModuleContract.CmdCanCloseCommitted:
                // (v1.3 final) Signal de validation globale : toutes les NeedUser ont dit Oui,
                // l'orchestrateur va attendre les Busy. C'est ICI que le module Busy démarre
                // son travail réel (via la DIM OnCanCloseCommittedAsync).
                _ = _negotiator?.OnCanCloseCommittedReceived();
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
