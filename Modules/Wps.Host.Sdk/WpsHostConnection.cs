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
    private DateTime _lastTickUtc;
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

    // ====== v1.3 : événements du shutdown négocié reçus du module ======

    /// <summary>(v1.3) Émis à réception de <c>CAN_CLOSE_OK</c> : le module est libre, peut fermer.</summary>
    public event Action? CanCloseOk;

    /// <summary>(v1.3) Émis à réception de <c>CAN_CLOSE_BUSY|estimatedMs|reason</c>.</summary>
    public event Action<int, string>? CanCloseBusy;

    /// <summary>(v1.3 final) Émis à réception de <c>CAN_CLOSE_NEED_USER|jsonPayload</c>.
    /// Le HOST est responsable d'afficher la modale (cf. <see cref="NeedUserPayload"/>) et
    /// de renvoyer l'id du bouton cliqué via <see cref="SendUserResponseAsync"/>.</summary>
    public event Action<NeedUserPayload>? CanCloseNeedUser;

    /// <summary>(v1.3) Émis à réception de <c>CAN_CLOSE_REJECTED|reason</c>.</summary>
    public event Action<string>? CanCloseRejected;

    /// <summary>(v1.3) Émis à réception de <c>BUSY_PROGRESS|percent|message</c>.</summary>
    public event Action<int, string>? BusyProgressReceived;

    /// <summary>(v1.3) Émis à réception de <c>CLOSING_DONE</c> : cleanup applicatif terminé,
    /// le process va exit immédiatement.</summary>
    public event Action? ClosingDone;

    /// <summary>(v1.3) Émis à réception de <c>SELF_CLOSING|reason</c> : le module se ferme à
    /// son initiative (bouton Quitter, etc.). Permet au host de griser le slot proprement
    /// (état "Closed" plutôt que "Failed").</summary>
    public event Action<string>? SelfClosing;

    /// <summary>(v1.4) Émis à réception de <c>SIGNAL|name|payload</c> du module. Arguments :
    /// <c>(name, payload)</c>. Le <c>name</c> est l'identifiant court du signal (ex:
    /// <c>"server-ready"</c>) ; le <c>payload</c> est un texte libre applicatif (peut être
    /// vide, peut contenir des <c>|</c>). L'app host filtre par <c>name</c> pour router vers
    /// la logique métier appropriée (activer un bouton UI, déclencher un état, etc.).</summary>
    public event Action<string, string>? SignalReceived;

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

    /// <summary>(v1.3) Envoie <c>CAN_CLOSE|isUrgent</c> au module pour engager la phase 1 du
    /// shutdown négocié. Le module répondra avec <c>CAN_CLOSE_OK</c> / <c>BUSY</c> /
    /// <c>NEED_USER</c> / <c>REJECTED</c> via les events correspondants.</summary>
    public Task SendCanCloseAsync(bool isUrgent) =>
        _duplex.SendAsync($"{WpsModuleContract.CmdCanClose}{WpsModuleContract.Separator}{(isUrgent ? "1" : "0")}");

    /// <summary>(v1.3) Envoie <c>CAN_CLOSE_ABORTED</c> au module pour libérer son verrou Locked
    /// (cascade annulée par un autre module qui a renvoyé Rejected). Le module reprend son
    /// fonctionnement normal et l'app reçoit <c>OnCanCloseAborted</c>.</summary>
    public Task SendCanCloseAbortedAsync() =>
        _duplex.SendAsync(WpsModuleContract.CmdCanCloseAborted);

    /// <summary>(v1.3 final) Envoie <c>USER_RESPONSE|buttonId</c> au module en réponse à un
    /// <see cref="CanCloseNeedUser"/> précédemment reçu. L'id est l'une des clés du
    /// dictionnaire <c>Answers</c> du payload, ou un id réservé (<c>yes</c>/<c>ok</c>/
    /// <c>no</c>/<c>cancel</c>) si l'app utilise le mapping standard.</summary>
    public Task SendUserResponseAsync(string buttonId) =>
        _duplex.SendAsync($"{WpsModuleContract.CmdUserResponse}{WpsModuleContract.Separator}{buttonId ?? ""}");

    /// <summary>(v1.3 final) Envoie <c>CAN_CLOSE_COMMITTED</c> au module : signal de
    /// validation globale (toutes les NeedUser ont dit Oui). Côté module SDK, déclenche la
    /// DIM <see cref="IWpsModule.OnCanCloseCommittedAsync"/> où l'app démarre son travail
    /// Busy réel. À envoyer après Phase 3 (NeedUsers résolues) et avant Phase 4 (await
    /// résolution Busy).</summary>
    public Task SendCanCloseCommittedAsync() =>
        _duplex.SendAsync(WpsModuleContract.CmdCanCloseCommitted);

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
        _lastTickUtc = DateTime.UtcNow;
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
        // Heuristique "saut temporel" : delta avec le tick précédent > 3× l'intervalle →
        // on a "dormi" entre les 2 ticks. Couvre veille (S3), hibernation (S4), Modern
        // Standby (S0Low), freeze GC long, breakpoint debugger. Approche auto-détective,
        // pas de dépendance sur SystemEvents.PowerMode (qui rate parfois — Modern Standby
        // ARM notamment). Symétrique au côté module/service.
        var now = DateTime.UtcNow;
        var gapMs = (now - _lastTickUtc).TotalMilliseconds;
        _lastTickUtc = now;
        if (gapMs > PING_INTERVAL_MS * 3)
        {
            WpsDebugSender.Log(
                $"Heartbeat tick gap {gapMs:F0}ms (attendu ~{PING_INTERVAL_MS}ms) → veille/freeze détecté, reset heartbeat",
                LogLevel.Info, LogTag);
            _lastPongUtc = now;
            if (_hangSignaled)
            {
                _hangSignaled = false;
                HungStateChanged?.Invoke(false);
            }
            return;
        }

        try { await _duplex.SendAsync(WpsModuleContract.CmdPing).ConfigureAwait(false); }
        catch { /* IPC dead → on vérifie quand même le timeout ci-dessous */ }

        var elapsed = now - _lastPongUtc;
        if (!_hangSignaled && elapsed.TotalSeconds > PONG_TIMEOUT_SEC)
        {
            _hangSignaled = true;
            WpsDebugSender.Log($"HEARTBEAT: pas de PONG depuis {elapsed.TotalSeconds:F1}s — module figé (UI thread bloqué)", LogLevel.Warning, LogTag);
            HungStateChanged?.Invoke(true);
        }
    }

    /// <summary>Handler du <see cref="WpsPowerWatchdog.Resume"/> : reset <c>_lastPongUtc</c>
    /// comme si on venait de recevoir un PONG frais. Si le slot était signalé hung pendant la
    /// veille (cas où le Suspend a fire mais le watchdog avait déjà basculé avant), on lève
    /// aussi le flag pour éviter qu'un éventuel orchestrateur shutdown ne kill le module au
    /// réveil. Le module aura ~5s (PING_INTERVAL_MS) avant le prochain check, largement assez
    /// pour que les pipes IPC reprennent.</summary>
    private void OnSystemResume()
    {
        _lastPongUtc = DateTime.UtcNow;
        if (_hangSignaled)
        {
            _hangSignaled = false;
            HungStateChanged?.Invoke(false);
        }
        WpsDebugSender.Log("System Resume → heartbeat reset (grace period au réveil)",
            LogLevel.Info, LogTag);
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

        // (v1.3) Pour les messages avec un payload "reason" qui peut contenir des '|', on split
        // à un nombre fixe de parts (la dernière contient tout le reste). Patterns :
        //   CAN_CLOSE_BUSY|estimatedMs|reason          → 3 parts
        //   CAN_CLOSE_NEED_USER|reason                 → 2 parts
        //   CAN_CLOSE_REJECTED|reason                  → 2 parts
        //   BUSY_PROGRESS|percent|message              → 3 parts
        //   SELF_CLOSING|reason                        → 2 parts
        if (line.StartsWith(WpsModuleContract.NotifCanCloseBusy + WpsModuleContract.Separator, StringComparison.Ordinal))
        {
            var p = line.Split(WpsModuleContract.Separator, 3);
            if (p.Length >= 3 && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var estMs))
                CanCloseBusy?.Invoke(estMs, p[2]);
            return;
        }
        if (line.StartsWith(WpsModuleContract.NotifCanCloseNeedUser + WpsModuleContract.Separator, StringComparison.Ordinal))
        {
            // Format v1.3 final : CAN_CLOSE_NEED_USER|jsonPayload (2 parts max — le payload
            // JSON peut contenir des | littéraux, on les préserve via Split(separator, 2)).
            var p = line.Split(WpsModuleContract.Separator, 2);
            if (p.Length >= 2)
            {
                var payload = NeedUserPayload.Deserialize(p[1]);
                if (payload is not null)
                {
                    CanCloseNeedUser?.Invoke(payload);
                }
                else
                {
                    WpsDebugSender.Log(
                        $"NotifCanCloseNeedUser: JSON invalide '{p[1]}' — trame ignorée",
                        LogLevel.Warning, LogTag);
                }
            }
            return;
        }
        if (line.StartsWith(WpsModuleContract.NotifCanCloseRejected + WpsModuleContract.Separator, StringComparison.Ordinal))
        {
            var p = line.Split(WpsModuleContract.Separator, 2);
            if (p.Length >= 2) CanCloseRejected?.Invoke(p[1]);
            return;
        }
        if (line.StartsWith(WpsModuleContract.NotifBusyProgress + WpsModuleContract.Separator, StringComparison.Ordinal))
        {
            var p = line.Split(WpsModuleContract.Separator, 3);
            if (p.Length >= 3 && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
                BusyProgressReceived?.Invoke(percent, p[2]);
            return;
        }
        if (line.StartsWith(WpsModuleContract.NotifSelfClosing + WpsModuleContract.Separator, StringComparison.Ordinal))
        {
            var p = line.Split(WpsModuleContract.Separator, 2);
            if (p.Length >= 2) SelfClosing?.Invoke(p[1]);
            return;
        }
        // (v1.4) SIGNAL|name|payload : name interdit de '|' (2e portion), payload libre (3e
        // portion, peut contenir des '|') → Split(separator, 3) préserve le payload intact.
        if (line.StartsWith(WpsModuleContract.NotifSignal + WpsModuleContract.Separator, StringComparison.Ordinal))
        {
            var p = line.Split(WpsModuleContract.Separator, 3);
            if (p.Length >= 2)
            {
                var name = p[1];
                var payload = p.Length >= 3 ? p[2] : "";
                WpsDebugSender.Log($"received SIGNAL name='{name}' payloadLen={payload.Length}",
                    LogLevel.Info, LogTag);
                SignalReceived?.Invoke(name, payload);
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

            // (v1.3) Messages sans payload variable
            case WpsModuleContract.NotifCanCloseOk:
                CanCloseOk?.Invoke();
                break;

            case WpsModuleContract.NotifClosingDone:
                ClosingDone?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        StopHeartbeat();
        _duplex.Dispose();
    }
}
