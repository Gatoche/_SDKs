using System.Globalization;
using System.Text.Json;
using Wps.Module;
using Wps.Module.Core;

namespace Wps.ModuleService;

/// <summary>
/// Connexion IPC côté ModuleService. Symétrique à <c>WpsModuleConnection</c> (côté Module classique)
/// mais headless : pas de Dispatcher WPF obligatoire, pas de gestion RESIZE/READY|hwnd. Annonce
/// le Kind <see cref="WpsModuleKind.ModuleService"/> dans le HELLO et dispatche les nouveaux
/// messages <c>INVOKE</c> / <c>SHOW_SETTINGS</c> du contrat 1.2 + le shutdown négocié v1.3
/// (<c>CAN_CLOSE</c> / <c>CAN_CLOSE_ABORTED</c>) via <see cref="WpsModuleServiceShutdownNegotiator"/>.
///
/// <para>Les handlers Invoke sont enregistrés via <see cref="RegisterInvokeHandler"/> (string
/// method name → async handler). Pour chaque INVOKE reçu, on cherche le handler correspondant,
/// on désérialise le payload JSON en <c>JsonElement</c>, on appelle le handler, on sérialise le
/// résultat et on répond INVOKE_RESULT|requestId|OK|jsonResult. En cas d'exception ou de handler
/// manquant, on répond INVOKE_RESULT|requestId|ERROR|message.</para>
///
/// <para><b>v1.3 :</b> tracking <see cref="LastPingReceivedUtc"/> pour le watchdog "host figé"
/// implémenté côté <see cref="WpsModuleService"/>. Send methods pour les nouvelles notifications
/// (CAN_CLOSE_OK/BUSY/NEED_USER/REJECTED, BUSY_PROGRESS, CLOSING_DONE, SELF_CLOSING).</para>
/// </summary>
internal sealed class WpsModuleServiceConnection : IDisposable
{
    private readonly string _sessionId;
    private readonly WpsPipeDuplex _duplex;

    // Handler signature : (jsonParams) → Task<jsonResult>. Le SDK fait l'aiguillage du JSON,
    // le caller utilise des wrappers typés (cf. WpsModuleService.RegisterInvokeHandler<T,U>).
    private readonly Dictionary<string, Func<JsonElement, Task<string>>> _invokeHandlers
        = new(StringComparer.OrdinalIgnoreCase);

    // Handler optionnel pour SHOW_SETTINGS (factory de fenêtre WPF — peut être null si le service
    // n'expose pas de paramétrage).
    private Action? _showSettingsHandler;

    /// <summary>(v1.3) Négociateur shutdown attaché après création (cf. <see cref="AttachNegotiator"/>).</summary>
    private WpsModuleServiceShutdownNegotiator? _negotiator;

    private readonly TaskCompletionSource<string> _welcomeTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>(v1.3) Timestamp UTC du dernier PING reçu — pour watchdog "host figé".</summary>
    public DateTime LastPingReceivedUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Reset le timestamp watchdog comme si on venait de recevoir un PING frais.
    /// Utilisé au retour de veille Windows (cf. <see cref="wipisoft.WpsPowerWatchdog"/>) pour
    /// éviter que le watchdog ne déclenche HeartbeatSilent immédiatement au réveil — pendant
    /// le sommeil aucun PING n'a pu être échangé, le silence accumulé n'est pas un signal
    /// "host mort".</summary>
    public void ResetHeartbeatTimestamp() => LastPingReceivedUtc = DateTime.UtcNow;

    /// <summary>Expose le pipe duplex pour permettre à <see cref="WpsModuleService"/> de
    /// s'abonner à <see cref="WpsPipeDuplex.Closed"/> et déclencher
    /// <see cref="IWpsModule.OnHostDisconnected"/>(PipeClosed).</summary>
    public WpsPipeDuplex Duplex => _duplex;

    /// <summary>Émis quand le pipe est coupé par le Host (EOF). Le caller (typiquement le main
    /// du service console) peut s'en servir pour sortir de RunAsync. Préservé pour rétrocompat
    /// avec les services existants (TracePML, etc.) — en v1.3 préférer
    /// <see cref="IWpsModule.OnHostDisconnected"/>.</summary>
    public event Action? PipeClosed;

    /// <summary>Émis quand le Host envoie CLOSE (shutdown propre). Le caller doit terminer son
    /// travail en cours et sortir. Préservé pour rétrocompat (TracePML utilise cet event
    /// directement). En v1.3, le négociateur intercepte CLOSE en premier pour faire passer le
    /// hook applicatif <see cref="IWpsModule.OnShutdownRequested"/> + envoyer CLOSING_DONE — ce
    /// vieil event est levé en complément pour les services qui n'utilisent pas l'interface.</summary>
    public event Action? ShutdownRequested;

    private const string LogTag = "Wps.ModuleService.Sdk";

    public WpsModuleServiceConnection(string sessionId)
    {
        _sessionId = sessionId;

        // Côté ModuleService : reçoit sur "Cmd" (server in), envoie sur "Notif" (client out).
        // Mêmes conventions de nommage que le Module classique — le contrat reste unifié.
        var cmdPipe = WpsModuleContract.IpcNames.CommandPipe(_sessionId);
        var notifPipe = WpsModuleContract.IpcNames.NotificationPipe(_sessionId);
        _duplex = new WpsPipeDuplex(inboundPipeName: cmdPipe, outboundPipeName: notifPipe, LogTag);
        _duplex.LineReceived += Dispatch;
        _duplex.Closed += () => PipeClosed?.Invoke();
    }

    /// <summary>Attache le négociateur de shutdown (création différée pour résoudre la
    /// dépendance circulaire). Appelé par <see cref="WpsModuleService.BootstrapAsync"/>.</summary>
    public void AttachNegotiator(WpsModuleServiceShutdownNegotiator negotiator) => _negotiator = negotiator;

    public Task StartAsync(CancellationToken ct = default) => _duplex.StartAsync(ct);

    /// <summary>Envoie HELLO|version|name|ModuleService.</summary>
    public Task SendHelloAsync(string contractVersion, string serviceName) =>
        _duplex.SendAsync(FormattableString.Invariant(
            $"{WpsModuleContract.NotifHello}{WpsModuleContract.Separator}{contractVersion}{WpsModuleContract.Separator}{serviceName}{WpsModuleContract.Separator}{WpsModuleKind.ModuleService}"));

    public Task<string> WaitForWelcomeAsync(TimeSpan? timeout = null)
    {
        var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        cts.Token.Register(() => _welcomeTcs.TrySetException(new TimeoutException("WELCOME not received")));
        return _welcomeTcs.Task;
    }

    /// <summary>Envoie READY|0 pour signaler "service prêt à recevoir des Invoke" (pas de
    /// HWND à embed côté ModuleService — le 0 est explicite).</summary>
    public Task NotifyReadyAsync() =>
        _duplex.SendAsync($"{WpsModuleContract.NotifReady}{WpsModuleContract.Separator}0");

    public void RegisterInvokeHandler(string method, Func<JsonElement, Task<string>> handler)
    {
        if (string.IsNullOrEmpty(method)) throw new ArgumentException("method required", nameof(method));
        _invokeHandlers[method] = handler;
    }

    public void SetShowSettingsHandler(Action handler) => _showSettingsHandler = handler;

    // ====== v1.3 : envois Module → Host ======

    public Task SendCanCloseOkAsync() =>
        _duplex.SendAsync(WpsModuleContract.NotifCanCloseOk);

    public Task SendCanCloseBusyAsync(int estimatedMs, string reason) =>
        _duplex.SendAsync(FormattableString.Invariant(
            $"{WpsModuleContract.NotifCanCloseBusy}{WpsModuleContract.Separator}{estimatedMs}{WpsModuleContract.Separator}{reason ?? ""}"));

    /// <summary>(v1.3 final) Envoie <c>CAN_CLOSE_NEED_USER|jsonPayload</c>. Le HOST affiche
    /// la modale (pas le service). Cf. <see cref="NeedUserPayload"/> pour le format.</summary>
    public Task SendCanCloseNeedUserAsync(NeedUserPayload payload) =>
        _duplex.SendAsync(
            $"{WpsModuleContract.NotifCanCloseNeedUser}{WpsModuleContract.Separator}{payload.Serialize()}");

    public Task SendCanCloseRejectedAsync(string reason) =>
        _duplex.SendAsync($"{WpsModuleContract.NotifCanCloseRejected}{WpsModuleContract.Separator}{reason ?? ""}");

    public Task SendBusyProgressAsync(int percent, string message) =>
        _duplex.SendAsync(FormattableString.Invariant(
            $"{WpsModuleContract.NotifBusyProgress}{WpsModuleContract.Separator}{percent}{WpsModuleContract.Separator}{message ?? ""}"));

    public Task SendClosingDoneAsync() =>
        _duplex.SendAsync(WpsModuleContract.NotifClosingDone);

    public Task SendSelfClosingAsync(string reason) =>
        _duplex.SendAsync($"{WpsModuleContract.NotifSelfClosing}{WpsModuleContract.Separator}{reason ?? ""}");

    // ====== Dispatch des messages reçus du host ======

    private void Dispatch(string line)
    {
        // ⚠️ Pour CmdInvoke : le payload JSON peut contenir des '|' → on doit limiter le split
        // aux 4 premières parties (cmd, requestId, method, jsonPayload-recombiné).
        // Pour les autres commandes, un Split classique suffit.
        if (line.StartsWith(WpsModuleContract.CmdInvoke + WpsModuleContract.Separator, StringComparison.Ordinal))
        {
            HandleInvoke(line);
            return;
        }

        var parts = line.Split(WpsModuleContract.Separator);
        switch (parts[0])
        {
            case WpsModuleContract.CmdWelcome:
                _welcomeTcs.TrySetResult(parts.Length > 1 ? parts[1] : "");
                break;

            case WpsModuleContract.CmdClose:
                // v1.3 : si négociateur attaché, route via la state machine (qui appellera
                // OnShutdownRequested + enverra CLOSING_DONE proprement). Lever AUSSI l'event
                // legacy ShutdownRequested pour les services qui ne sont pas portés v1.3 (ex:
                // TracePML qui s'abonne directement à cet event dans son App.xaml.cs).
                if (_negotiator is not null)
                    _ = _negotiator.OnCloseReceivedAsync();
                ShutdownRequested?.Invoke();
                break;

            case WpsModuleContract.CmdPing:
                LastPingReceivedUtc = DateTime.UtcNow;  // (v1.3) reset watchdog "host figé"
                // Pas de UI thread à sonder côté ModuleService console : on répond PONG direct
                // depuis le ReadLoop. Si le service a un workload bloquant qui occupe le pipe
                // d'écoute, le PONG ne sortira pas → c'est un signal "non réactif" légitime.
                _ = _duplex.SendAsync(WpsModuleContract.NotifPong);
                break;

            case WpsModuleContract.CmdShowSettings:
                try { _showSettingsHandler?.Invoke(); }
                catch (Exception ex)
                {
                    WpsDebugSender.Log($"ShowSettings handler threw {ex.GetType().Name}: {ex.Message}",
                        LogLevel.Warning, LogTag);
                }
                break;

            // ====== v1.3 : nouveaux messages ======

            case WpsModuleContract.CmdCanClose:
                {
                    var isUrgent = parts.Length >= 2 && parts[1] == "1";
                    var ctx = new CanCloseContext { IsUrgent = isUrgent };
                    if (_negotiator is not null)
                        _ = _negotiator.OnCanCloseReceivedAsync(ctx);
                    else
                        // Pas de négociateur : on répond Ok pour ne pas bloquer
                        _ = SendCanCloseOkAsync();
                }
                break;

            case WpsModuleContract.CmdCanCloseAborted:
                _negotiator?.OnCanCloseAborted();
                break;

            case WpsModuleContract.CmdUserResponse when parts.Length >= 2:
                // Format v1.3 final : USER_RESPONSE|buttonId (string libre).
                _ = _negotiator?.OnUserResponseReceived(parts[1]);
                break;

            case WpsModuleContract.CmdCanCloseCommitted:
                // (v1.3 final) Validation globale → démarrer le travail Busy via la DIM.
                _ = _negotiator?.OnCanCloseCommittedReceived();
                break;
        }
    }

    private async void HandleInvoke(string line)
    {
        // Format : INVOKE|requestId|method|jsonParams (le jsonParams peut contenir des '|')
        // string.Split avec count=4 limite à 4 parts : la 4e contient tout le reste.
        var parts = line.Split(WpsModuleContract.Separator, 4);
        if (parts.Length < 4)
        {
            WpsDebugSender.Log($"INVOKE malformed (parts={parts.Length}): {line}", LogLevel.Warning, LogTag);
            return;
        }

        var requestId = parts[1];
        var method = parts[2];
        var jsonParams = parts[3];

        if (!_invokeHandlers.TryGetValue(method, out var handler))
        {
            await SendInvokeResultAsync(requestId, ok: false, payload: $"unknown method '{method}'");
            return;
        }

        JsonElement paramsJson;
        try
        {
            using var doc = JsonDocument.Parse(jsonParams);
            paramsJson = doc.RootElement.Clone();
        }
        catch (JsonException jex)
        {
            await SendInvokeResultAsync(requestId, ok: false, payload: $"invalid JSON params: {jex.Message}");
            return;
        }

        try
        {
            var resultJson = await handler(paramsJson).ConfigureAwait(false);
            await SendInvokeResultAsync(requestId, ok: true, payload: resultJson);
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"INVOKE '{method}' handler threw {ex.GetType().Name}: {ex.Message}",
                LogLevel.Error, LogTag);
            await SendInvokeResultAsync(requestId, ok: false, payload: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private Task SendInvokeResultAsync(string requestId, bool ok, string payload)
    {
        var status = ok ? WpsModuleContract.InvokeStatusOk : WpsModuleContract.InvokeStatusError;
        return _duplex.SendAsync(
            $"{WpsModuleContract.NotifInvokeResult}{WpsModuleContract.Separator}{requestId}{WpsModuleContract.Separator}{status}{WpsModuleContract.Separator}{payload}");
    }

    public void Dispose() => _duplex.Dispose();
}
