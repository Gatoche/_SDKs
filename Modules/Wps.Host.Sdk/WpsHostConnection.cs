using System.Globalization;
using Wps.Module.Core;

namespace Wps.Module.Hosting;

/// <summary>
/// Connexion IPC côté host : utilise <see cref="WpsPipeDuplex"/> pour le transport bas-niveau
/// (pipes nommés conformes à <see cref="WpsModuleContract"/>) et y ajoute la logique métier
/// du contrat wipiSoft : réception du HELLO du module + reply WELCOME, dispatch READY/PONG,
/// pilotage du heartbeat (ping périodique + détection timeout = module figé).
/// </summary>
internal sealed class WpsHostConnection : IDisposable
{
    private readonly string _sessionId;
    private readonly WpsPipeDuplex _duplex;

    // Heartbeat
    private System.Threading.Timer? _heartbeatTimer;
    private DateTime _lastPongUtc;
    private bool _hangSignaled;
    private const int PING_INTERVAL_MS = 5000;
    private const int PONG_TIMEOUT_SEC = 8;

    /// <summary>Émis quand le module a envoyé HELLO et qu'on a validé son contract version.
    /// Argument : <c>(moduleContractVersion, moduleName, kind)</c>. Le <c>kind</c> est apparu
    /// dans le HELLO en v1.1 du contrat — pour un peer en v1.0 (champ absent), on suppose
    /// <see cref="WpsModuleKind.Module"/> pour préserver la rétrocompat.</summary>
    public event Action<string, string, WpsModuleKind>? ModuleHello;

    /// <summary>Émis quand le module signale READY|hwnd. Argument : hwnd à parenter dans le slot.</summary>
    public event Action<long>? ModuleReady;

    /// <summary>Émis sur passage hung↔normal détecté via heartbeat. <c>true</c> = module figé,
    /// <c>false</c> = module redevenu réactif.</summary>
    public event Action<bool>? HungStateChanged;

    /// <summary>(v1.2, ModuleService) Émis à la réception d'un INVOKE_RESULT du peer.
    /// Argument : <c>(requestId, ok, payload)</c> où <c>ok</c>=<c>true</c> si statut OK
    /// (<c>payload</c> = JSON résultat), <c>false</c> si statut ERROR (<c>payload</c> = message
    /// d'erreur). Consommé par <see cref="WpsModuleServiceClient.InvokeAsync"/>.</summary>
    public event Action<string, bool, string>? InvokeResultReceived;

    private const string LogTag = "Wps.Host.Sdk";

    public WpsHostConnection(string sessionId)
    {
        _sessionId = sessionId;

        // Côté host : reçoit sur "Notif" (server in), envoie sur "Cmd" (client out).
        var notifPipe = WpsModuleContract.IpcNames.NotificationPipe(_sessionId);
        var cmdPipe = WpsModuleContract.IpcNames.CommandPipe(_sessionId);
        _duplex = new WpsPipeDuplex(inboundPipeName: notifPipe, outboundPipeName: cmdPipe, LogTag);
        _duplex.LineReceived += Dispatch;
    }

    /// <summary>Ouvre les pipes via <see cref="WpsPipeDuplex"/>. Démarre la lecture.</summary>
    public Task StartAsync(CancellationToken ct = default) => _duplex.StartAsync(ct);

    /// <summary>Envoie WELCOME|hostVersion pour acquitter le HELLO du module.</summary>
    public Task SendWelcomeAsync(string hostContractVersion) =>
        _duplex.SendAsync($"{WpsModuleContract.CmdWelcome}{WpsModuleContract.Separator}{hostContractVersion}");

    public Task SendCloseAsync() => _duplex.SendAsync(WpsModuleContract.CmdClose);

    /// <summary>(v1.2, ModuleService) Envoie INVOKE|requestId|method|jsonParams au peer.
    /// La réponse arrivera asynchrone via <see cref="InvokeResultReceived"/> avec le même
    /// <paramref name="requestId"/> — c'est <see cref="WpsModuleServiceClient.InvokeAsync"/>
    /// qui orchestre la corrélation request/response.</summary>
    public Task SendInvokeAsync(string requestId, string method, string jsonParams) =>
        _duplex.SendAsync(
            $"{WpsModuleContract.CmdInvoke}{WpsModuleContract.Separator}{requestId}{WpsModuleContract.Separator}{method}{WpsModuleContract.Separator}{jsonParams}");

    /// <summary>(v1.2, ModuleService) Demande au service d'afficher sa fenêtre de paramétrage.
    /// Le service ignore silencieusement s'il n'a pas enregistré de settings window factory.</summary>
    public Task SendShowSettingsAsync() => _duplex.SendAsync(WpsModuleContract.CmdShowSettings);

    /// <summary>Notifie le module des nouvelles dimensions du slot (en DIPs).</summary>
    public Task SendResizeAsync(double dipW, double dipH, double dpi) =>
        _duplex.SendAsync(FormattableString.Invariant(
            $"{WpsModuleContract.CmdResize}{WpsModuleContract.Separator}{dipW:F2}{WpsModuleContract.Separator}{dipH:F2}{WpsModuleContract.Separator}{dpi:F2}"));

    /// <summary>Démarre le heartbeat : PING toutes les 5s, signale hung si pas de PONG > 8s.</summary>
    public void StartHeartbeat()
    {
        if (_heartbeatTimer is not null) return;
        _lastPongUtc = DateTime.UtcNow;
        _hangSignaled = false;
        _heartbeatTimer = new System.Threading.Timer(OnHeartbeatTick, null, PING_INTERVAL_MS, PING_INTERVAL_MS);
    }

    public void StopHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    private async void OnHeartbeatTick(object? _)
    {
        try { await _duplex.SendAsync(WpsModuleContract.CmdPing).ConfigureAwait(false); }
        catch { /* IPC dead → on vérifie quand même le timeout ci-dessous */ }

        var elapsed = DateTime.UtcNow - _lastPongUtc;
        if (!_hangSignaled && elapsed.TotalSeconds > PONG_TIMEOUT_SEC)
        {
            _hangSignaled = true;
            WpsDebugSender.Log($"HEARTBEAT: pas de PONG depuis {elapsed.TotalSeconds:F1}s — module figé (UI thread bloqué)", LogLevel.Warning, LogTag);
            HungStateChanged?.Invoke(true);
        }
    }

    private void Dispatch(string line)
    {
        // ⚠️ Pour INVOKE_RESULT : le payload JSON peut contenir des '|' → on doit limiter le
        // split aux 4 premières parties (cmd, requestId, status, payload-recombiné).
        if (line.StartsWith(WpsModuleContract.NotifInvokeResult + WpsModuleContract.Separator, StringComparison.Ordinal))
        {
            var ir = line.Split(WpsModuleContract.Separator, 4);
            if (ir.Length >= 4)
            {
                bool ok = string.Equals(ir[2], WpsModuleContract.InvokeStatusOk, StringComparison.Ordinal);
                InvokeResultReceived?.Invoke(ir[1], ok, ir[3]);
            }
            return;
        }

        var parts = line.Split(WpsModuleContract.Separator);
        switch (parts[0])
        {
            case WpsModuleContract.NotifHello when parts.Length >= 3:
                {
                    // 4e champ "kind" introduit en v1.1 du contrat — pour un peer v1.0 (3 champs),
                    // on suppose Module (mode classique) pour préserver la rétrocompat.
                    var kind = WpsModuleKind.Module;
                    if (parts.Length >= 4 && Enum.TryParse<WpsModuleKind>(parts[3], ignoreCase: true, out var parsed))
                        kind = parsed;
                    WpsDebugSender.Log($"received HELLO from name='{parts[2]}' contractVersion={parts[1]} kind={kind}", LogLevel.Info, LogTag);
                    ModuleHello?.Invoke(parts[1], parts[2], kind);
                }
                break;

            case WpsModuleContract.NotifReady when parts.Length == 2:
                if (long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hwnd))
                {
                    WpsDebugSender.Log($"received READY hwnd=0x{hwnd:X}", LogLevel.Success, LogTag);
                    ModuleReady?.Invoke(hwnd);
                }
                break;

            case WpsModuleContract.NotifPong:
                _lastPongUtc = DateTime.UtcNow;
                if (_hangSignaled)
                {
                    _hangSignaled = false;
                    WpsDebugSender.Log($"PONG reçu, module récupéré (was hung)", LogLevel.Info, LogTag);
                    HungStateChanged?.Invoke(false);
                }
                break;
        }
    }

    public void Dispose()
    {
        StopHeartbeat();
        _duplex.Dispose();
    }
}
