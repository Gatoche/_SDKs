namespace Wps.Module.Hosting;

/// <summary>
/// Options de fermeture passées à <see cref="WpsModuleSlot.ShutdownAsync(ShutdownOptions, System.Threading.CancellationToken)"/>
/// et aux méthodes phasées (<c>RequestCanCloseAsync</c> / <c>CompleteShutdownAsync</c>). Tous
/// les timeouts sont en millisecondes ; les valeurs par défaut couvrent les cas réels en
/// production (flush SMB, ABM_REMOVE, commit BDD) avec marge pour les configs en pression
/// mémoire qui peuvent swapper.
///
/// <para>Pour un shutdown OS rapide (WM_ENDSESSION reçu côté host), utiliser
/// <see cref="Urgent"/> qui clamp les timeouts au budget Windows ~5s.</para>
/// </summary>
public sealed class ShutdownOptions
{
    /// <summary>Si <c>true</c>, le host envoie <c>CAN_CLOSE|1</c> (vs <c>0</c> en mode normal).
    /// Côté module, le SDK clamp NeedUser/Rejected en Busy(2000ms) — pas de dialog ni de
    /// veto pendant un shutdown OS. Default <c>false</c>.</summary>
    public bool IsUrgent { get; init; } = false;

    /// <summary>Phase 1 : délai d'attente pour la réponse au <c>CAN_CLOSE</c> (Ok/Busy/NeedUser/
    /// Rejected). Au-delà, on renvoie <see cref="CanCloseResponse.Timeout"/> et l'orchestrateur
    /// décide (typiquement Kill direct vu que le module est probablement figé). Default 3000ms.</summary>
    public int CanCloseTimeoutMs { get; init; } = 3000;

    /// <summary>Pendant un Busy : silence > ce délai entre 2 BUSY_PROGRESS = présomption de
    /// deadlock → Kill direct. Default 8000ms (= 2× la période recommandée d'envoi BUSY_PROGRESS
    /// côté module, 3s, + marge swap).</summary>
    public int BusyHeartbeatTimeoutMs { get; init; } = 8000;

    /// <summary>Phase finale : délai d'attente du <c>CLOSING_DONE</c> après envoi du
    /// <c>CLOSE</c>. Au-delà, fallback Kill (si <see cref="KillFallback"/>). Default 7000ms.</summary>
    public int CleanupGracePeriodMs { get; init; } = 7000;

    /// <summary>Si <c>true</c>, fallback <c>Process.Kill(true)</c> en cas de timeout/figeage.
    /// Désactivable pour les tests (false = on attend l'exit indéfiniment). Default <c>true</c>.</summary>
    public bool KillFallback { get; init; } = true;

    /// <summary>Defaults sensibles pour la majorité des cas d'arrêt (toggle daemon, fermeture
    /// host, Stop pageslot).</summary>
    public static readonly ShutdownOptions Default = new();

    /// <summary>Mode shutdown OS : timeouts clampés au budget Windows ~5s (HungAppTimeout par
    /// défaut 5000ms, on s'auto-borne à 4s pour 1s de marge avant TerminateProcess).</summary>
    public static readonly ShutdownOptions Urgent = new()
    {
        IsUrgent = true,
        CanCloseTimeoutMs = 500,
        BusyHeartbeatTimeoutMs = 2000,
        CleanupGracePeriodMs = 3500,
    };
}

/// <summary>
/// Réponse à un <c>CAN_CLOSE</c> retournée par <see cref="WpsModuleSlot.RequestCanCloseAsync"/>
/// ou <see cref="WpsModuleServiceClient.RequestCanCloseAsync"/>. Type-somme à 5 variantes pour
/// que l'orchestrateur puisse pattern-matcher la réponse et décider la suite (CLOSE, queue
/// NEED_USER, abort cascade, Kill direct).
/// </summary>
public abstract record CanCloseResponse
{
    private CanCloseResponse() { }  // hiérarchie fermée

    /// <summary>Module libre : peut fermer. L'orchestrateur passera ensuite à
    /// <c>CompleteShutdownAsync</c> pour envoyer CLOSE.</summary>
    public sealed record OkR : CanCloseResponse { internal OkR() { } }

    /// <summary>Module occupé : nécessite encore <paramref name="EstimatedMs"/> ms.
    /// L'orchestrateur attend les BUSY_PROGRESS périodiques (event <c>BusyProgressChanged</c>)
    /// + un nouveau CAN_CLOSE_OK final. Si silence &gt; <c>BusyHeartbeatTimeoutMs</c>, Kill.</summary>
    public sealed record BusyR(int EstimatedMs, string Reason) : CanCloseResponse;

    /// <summary>Module veut afficher un dialog à l'utilisateur. L'orchestrateur bascule l'onglet
    /// sur ce module (callback fourni au constructeur) et attend la décision finale via un
    /// nouveau cycle CanCloseOk / CanCloseRejected.</summary>
    public sealed record NeedUserR(string Reason) : CanCloseResponse;

    /// <summary>Utilisateur a refusé la fermeture. L'orchestrateur annule sa fermeture en
    /// cascade (SendCanCloseAbortedAsync sur tous les modules déjà OK).</summary>
    public sealed record RejectedR(string Reason) : CanCloseResponse;

    /// <summary>Pas de réponse dans le délai imparti (<c>CanCloseTimeoutMs</c>). Module
    /// probablement figé — l'orchestrateur passe au Kill direct.</summary>
    public sealed record TimeoutR : CanCloseResponse { internal TimeoutR() { } }

    public static readonly CanCloseResponse Ok = new OkR();
    public static readonly CanCloseResponse Timeout = new TimeoutR();
    public static CanCloseResponse Busy(int estimatedMs, string reason)
        => new BusyR(estimatedMs, reason ?? "");
    public static CanCloseResponse NeedUser(string reason)
        => new NeedUserR(reason ?? "");
    public static CanCloseResponse Rejected(string reason)
        => new RejectedR(reason ?? "");
}

/// <summary>
/// Résultat final de <see cref="WpsModuleSlot.ShutdownAsync(ShutdownOptions, System.Threading.CancellationToken)"/>
/// ou <see cref="WpsModuleSlot.CompleteShutdownAsync"/>. Permet au caller (orchestrateur ou call
/// site simple) de savoir comment la fermeture s'est terminée.
/// </summary>
public enum ShutdownResult
{
    /// <summary>Séquence négociée terminée proprement (CLOSING_DONE reçu, process exited).</summary>
    Completed,

    /// <summary>Module a renvoyé Rejected → fermeture annulée. L'orchestrateur a propagé
    /// <c>SendCanCloseAbortedAsync</c> aux modules déjà OK.</summary>
    Aborted,

    /// <summary>Fallback Kill engagé (timeout, deadlock, ou pas de support v1.3).</summary>
    Killed,

    /// <summary>Process était déjà mort avant l'appel.</summary>
    AlreadyExited,

    /// <summary>Slot/Client déjà disposed — appel idempotent.</summary>
    NoOp,
}

/// <summary>
/// Mise à jour de progression rapportée pendant un Busy long. Identique au type côté module
/// (<c>Wps.Module.BusyProgress</c>) — copié dans le namespace Hosting pour ne pas forcer les
/// callers Host à référencer Wps.Module pour ce type pur de transport.
/// </summary>
public sealed record HostBusyProgress(int Percent, string Message);

/// <summary>
/// Surface commune permettant à un orchestrateur (<see cref="ShutdownOrchestrator"/>) de piloter
/// indistinctement un <see cref="WpsModuleSlot"/> (Module embedded UI) ou un
/// <see cref="WpsModuleServiceClient"/> (ModuleService headless). Implémentée par les deux.
///
/// <para>Pas exposée publiquement aux callers du host (qui manipulent les types concrets) — c'est
/// uniquement pour l'orchestrateur qui veut traiter N targets de types mixtes.</para>
/// </summary>
public interface IWpsShutdownTarget
{
    /// <summary>Nom du module/service annoncé au HELLO. Pour log et UI overlay.</summary>
    string Name { get; }

    /// <summary>True si le module a annoncé un contrat &gt;= 1.3 → flow négocié. False sinon →
    /// fallback v1.2 (CLOSE direct + grace + Kill).</summary>
    bool SupportsNegotiatedShutdown { get; }

    /// <summary>Phase 1 : envoie CAN_CLOSE et attend la réponse (Ok/Busy/NeedUser/Rejected/Timeout).</summary>
    Task<CanCloseResponse> RequestCanCloseAsync(ShutdownOptions opts, System.Threading.CancellationToken ct);

    /// <summary>(v1.3) Attend la résolution d'un Busy en cours côté module : écoute les events
    /// <c>CanCloseOk</c> (résolution en Ok), <c>CanCloseNeedUser</c>/<c>CanCloseRejected</c>
    /// (changement de décision), et <c>BusyProgressReceived</c> (reset du watchdog silence).
    /// Retourne la nouvelle <see cref="CanCloseResponse"/> dès que le module sort de l'état
    /// Busy, ou <see cref="CanCloseResponse.Timeout"/> si silence &gt;
    /// <see cref="ShutdownOptions.BusyHeartbeatTimeoutMs"/> entre 2 BUSY_PROGRESS (deadlock
    /// présumé).
    /// <para>Contrairement à <see cref="RequestCanCloseAsync"/>, n'envoie PAS de nouveau
    /// CAN_CLOSE — on écoute juste les notifications du module qui a déjà répondu Busy en
    /// phase 1.</para></summary>
    Task<CanCloseResponse> WaitForBusyResolutionAsync(ShutdownOptions opts, System.Threading.CancellationToken ct);

    /// <summary>(v1.3) Attend la résolution d'un NeedUser en cours côté module : l'utilisateur
    /// est en train d'interagir avec le dialog applicatif (Yes/No/Cancel ou similaire). Le
    /// module n'envoie aucun signal pendant ce temps — il attend simplement que l'humain
    /// tranche. <b>Aucun watchdog silence</b> côté host : l'utilisateur peut prendre tout son
    /// temps (réfléchir, déplacer la souris, basculer vers une autre app, etc.).
    /// <para>Retourne dès que le module appelle <c>WpsModule.ResolveCanClose</c> avec sa
    /// décision finale, qui arrive ici via les events <c>CanCloseOk</c> / <c>CanCloseRejected</c>
    /// / <c>CanCloseBusy</c> (dialog tranché en "encore un peu de boulot" — rare). Annulation
    /// possible uniquement via le <see cref="System.Threading.CancellationToken"/>.</para>
    /// <para>Cette méthode existe SÉPARÉMENT de <see cref="WaitForBusyResolutionAsync"/> car
    /// le watchdog silence-Busy est inadapté pour NeedUser : un dialog peut rester ouvert plusieurs
    /// dizaines de secondes voire minutes. Le SDK ne doit pas forcer un Kill silencieux pendant
    /// qu'un humain réfléchit.</para></summary>
    Task<CanCloseResponse> WaitForNeedUserResolutionAsync(ShutdownOptions opts, System.Threading.CancellationToken ct);

    /// <summary>Annulation cascade : libère le verrou côté module (qui repassera Idle après
    /// avoir reçu CAN_CLOSE_ABORTED).</summary>
    Task SendCanCloseAbortedAsync();

    /// <summary>Phase finale : envoie CLOSE, attend CLOSING_DONE, fallback Kill si timeout.</summary>
    Task<ShutdownResult> CompleteShutdownAsync(ShutdownOptions opts, System.Threading.CancellationToken ct);

    /// <summary>Émis pendant un Busy : permet à l'orchestrateur d'afficher la progression.</summary>
    event Action<HostBusyProgress>? BusyProgressChanged;

    /// <summary>Émis quand le module signale NeedUser (si pas déjà reçu via RequestCanCloseAsync).
    /// Utile pour le pattern où le module passe Busy puis bascule en NeedUser.</summary>
    event Action<string>? NeedUserSignaled;

    /// <summary>Émis si le process meurt pendant la séquence (pipe coupé, Process.Exited).
    /// L'orchestrateur peut court-circuiter le wait du CLOSING_DONE.</summary>
    event Action? Disconnected;
}
