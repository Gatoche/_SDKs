namespace Wps.Module;

/// <summary>
/// Type de l'app wipiSoft connectée au host. Annoncé dans le 4e champ du HELLO (depuis v1.1
/// du contrat). Si absent (peer en v1.0 non rebuild), le host suppose <see cref="Module"/>.
/// </summary>
public enum WpsModuleKind
{
    /// <summary>Application avec UI principale (fenêtre WPF) embarquée par le host dans un slot
    /// (parking HWND, WS_EX_TOOLWINDOW). Mode classique du SDK <c>Wps.Module.Sdk</c>.</summary>
    Module,

    /// <summary>Application headless (souvent console) qui rend une fonctionnalité au host sans
    /// UI principale. Peut exposer une fenêtre de paramétrage ou ouvrir une fenêtre modale
    /// éphémère parentée au host. Mode du futur SDK <c>Wps.ModuleService.Sdk</c>.</summary>
    ModuleService,
}

/// <summary>
/// Convention de communication module ↔ host wipiSoft. Constantes immuables qui définissent
/// la version du contrat, les conventions d'arguments ligne de commande, les noms de pipes
/// nommés, et le vocabulaire wire-protocol des messages texte échangés sur les pipes.
///
/// Toute évolution incompatible doit bumper <see cref="CurrentVersion"/> :
/// - Major bump : breaking change → host rejette les modules d'un major différent
/// - Minor bump : ajout additif → compat ascendante préservée (host accepte modules de même major
///   et minor inférieur ou égal)
///
/// Historique :
/// - <b>1.0</b> : handshake HELLO/WELCOME, READY/hwnd, RESIZE, CLOSE, PING/PONG
/// - <b>1.1</b> : 4e champ <c>kind</c> dans HELLO (Module | ModuleService) — additif, défaut Module si absent
/// - <b>1.2</b> : INVOKE / INVOKE_RESULT (Host ↔ ModuleService request/response), SHOW_SETTINGS
///   (Host → ModuleService demande l'ouverture de la fenêtre de paramétrage) — additif
/// - <b>1.3</b> : shutdown négocié — CAN_CLOSE (avec flag <c>isUrgent</c> pour shutdown OS) +
///   réponses CAN_CLOSE_OK / CAN_CLOSE_BUSY / CAN_CLOSE_NEED_USER / CAN_CLOSE_REJECTED,
///   CAN_CLOSE_ABORTED (libère le verrou si la fermeture est annulée par cascade), CLOSING_DONE
///   (fin de cleanup côté module avant exit), BUSY_PROGRESS (mise à jour pendant un BUSY long),
///   SELF_CLOSING (module se ferme à son initiative). Permet la négociation d'arrêt avec veto
///   utilisateur, intervention applicative en NEED_USER, et grace timeouts confortables — additif
/// </summary>
public static class WpsModuleContract
{
    /// <summary>Version du contrat module ↔ host (semver "major.minor"). Annoncée au handshake HELLO.</summary>
    public const string CurrentVersion = "1.3";

    /// <summary>Argument ligne de commande qui passe le sessionId au module : <c>--wps-session XXX</c>.</summary>
    public const string SessionArgFlag = "--wps-session";

    /// <summary>Séparateur de champs dans les messages texte sur les pipes.</summary>
    public const char Separator = '|';

    /// <summary>Conventions de noms des ressources nommées (pipes, shared memory) par session.</summary>
    public static class IpcNames
    {
        /// <summary>Pipe Host → Module (commands). Host est client, Module est serveur.</summary>
        public static string CommandPipe(string sessionId) => $"Wps.Module.Cmd.{sessionId}";

        /// <summary>Pipe Module → Host (notifications). Module est client, Host est serveur.</summary>
        public static string NotificationPipe(string sessionId) => $"Wps.Module.Notif.{sessionId}";

        /// <summary>Shared memory pour frame buffer (snapshots cross-process). Optionnel.</summary>
        public static string FrameBuffer(string sessionId) => $"Wps.Module.Frame.{sessionId}";
    }

    // ====== Vocabulaire wire-protocol Host → Module ======

    /// <summary>Réponse au HELLO du module. Format : <c>WELCOME|hostContractVersion</c>.
    /// Le module peut décider de couper la connexion s'il ne supporte pas la version annoncée.</summary>
    public const string CmdWelcome = "WELCOME";

    /// <summary>Demande au module de se fermer proprement (équivalent Window.Close).</summary>
    public const string CmdClose = "CLOSE";

    /// <summary>Notifie le module des dimensions courantes du slot d'affichage.
    /// Format : <c>RESIZE|dipWidth|dipHeight|dpi</c> (invariant culture, F2).</summary>
    public const string CmdResize = "RESIZE";

    /// <summary>Heartbeat ping vers le module. Le module doit répondre PONG après dispatch UI no-op.
    /// Pas de réponse > timeout → host signale module figé (UI thread bloqué).</summary>
    public const string CmdPing = "PING";

    /// <summary>(v1.2, ModuleService) Host invoque une méthode métier exposée par le service.
    /// Format : <c>INVOKE|requestId|method|jsonParams</c>. Le service doit répondre avec
    /// <see cref="NotifInvokeResult"/> en réutilisant le même <c>requestId</c>.
    /// <para>Le <c>jsonParams</c> peut contenir des séparateurs <c>|</c> — c'est la dernière
    /// portion de la trame, on la reconstruit via <c>string.Join</c> côté parser.</para></summary>
    public const string CmdInvoke = "INVOKE";

    /// <summary>(v1.2, ModuleService) Demande au service d'afficher sa fenêtre de paramétrage.
    /// Format : <c>SHOW_SETTINGS</c> (sans payload). Si le service n'a pas enregistré de
    /// settings window (factory non fournie au Bootstrap), il ignore.</summary>
    public const string CmdShowSettings = "SHOW_SETTINGS";

    // ====== Vocabulaire wire-protocol Module → Host ======

    /// <summary>Premier message envoyé par le module à la connexion. Annonce sa version contrat
    /// et son nom. Format : <c>HELLO|moduleContractVersion|moduleName</c>.
    /// Le host valide la compatibilité et répond WELCOME ou disconnect.</summary>
    public const string NotifHello = "HELLO";

    /// <summary>Notifie le host que la fenêtre principale du module est prête à être embarquée.
    /// Format : <c>READY|hwndAsLong</c>. Le hwnd est celui à parenter dans le slot du host.</summary>
    public const string NotifReady = "READY";

    /// <summary>Réponse au PING. Émis après dispatch UI no-op (preuve que l'UI thread répond).</summary>
    public const string NotifPong = "PONG";

    /// <summary>(v1.2, ModuleService) Réponse à un <see cref="CmdInvoke"/>. Format :
    /// <c>INVOKE_RESULT|requestId|status|payload</c> où :
    /// <list type="bullet">
    ///   <item><c>status</c> = <see cref="InvokeStatusOk"/> (succès, payload = JSON résultat)
    ///         ou <see cref="InvokeStatusError"/> (échec, payload = message d'erreur)</item>
    ///   <item><c>payload</c> peut contenir des <c>|</c> — c'est la dernière portion de la trame</item>
    /// </list></summary>
    public const string NotifInvokeResult = "INVOKE_RESULT";

    /// <summary>Statut OK pour <see cref="NotifInvokeResult"/> (payload = JSON sérialisé du résultat).</summary>
    public const string InvokeStatusOk = "OK";

    /// <summary>Statut ERROR pour <see cref="NotifInvokeResult"/> (payload = message d'erreur).</summary>
    public const string InvokeStatusError = "ERROR";

    // ================================================================================
    // ====== v1.3 : Shutdown négocié ================================================
    // ================================================================================
    //
    // Le host n'envoie plus directement CLOSE. Il commence par CAN_CLOSE et attend la
    // décision du module (Ok / Busy / NeedUser / Rejected). Selon la réponse, l'arrêt
    // est annulé (REJECTED), différé (BUSY/NEED_USER), ou confirmé par CLOSE. Le module
    // termine en envoyant CLOSING_DONE avant exit. Pour le détail des transitions, voir
    // les machines à états dans Wps.Module.Sdk/HOWTO.md (commit 12).
    //
    // Aucun corrélation ID dans ces messages : le pattern est mono-shot par cycle, le
    // pipe FIFO single-stream garantit l'ordre, et les double-signaux Windows-shutdown
    // ↔ pipe-CAN_CLOSE sont coalescés côté SDK module via un flag d'état interne.
    //
    // Tous les messages v1.3 sont additifs — un module v1.2 ignore CAN_CLOSE qu'il
    // reçoit (il ne sait pas le parser), et un host v1.3 détecte la version du module
    // dans HELLO pour router vers le flow legacy si v <= 1.2 (CLOSE direct + grace +
    // Kill fallback, mais avec grace par défaut bumpé à 7000ms — cf. commit 1).

    // ====== Vocabulaire v1.3 Host → Module ======

    /// <summary>(v1.3) Phase 1 du shutdown négocié : "peux-tu fermer maintenant ?".
    /// Format : <c>CAN_CLOSE|isUrgent</c> où <c>isUrgent</c> = <c>0</c> ou <c>1</c>.
    /// <para>En mode urgent (= shutdown OS, WM_ENDSESSION reçu côté host), le module
    /// est tenu de répondre OK ou BUSY uniquement — le SDK module clamp automatiquement
    /// les décisions <see cref="WpsModule.CanCloseDecision"/>.NeedUser / .Rejected en Busy
    /// dans ce mode. Justification : pendant un shutdown OS, afficher un dialog ou bloquer
    /// la fermeture du host est inopérant (Windows ignore les refus côté apps standalone
    /// sauf en cas explicite, et le timer Windows ~5s tue le process indépendamment).</para>
    /// <para>Réponse attendue dans <see cref="NotifCanCloseOk"/> / <see cref="NotifCanCloseBusy"/>
    /// / <see cref="NotifCanCloseNeedUser"/> / <see cref="NotifCanCloseRejected"/> avant
    /// timeout (3000ms par défaut, cf. <c>ShutdownOptions.CanCloseTimeoutMs</c>).</para></summary>
    public const string CmdCanClose = "CAN_CLOSE";

    /// <summary>(v1.3) Annulation cascade : un autre module a renvoyé REJECTED, le host annule
    /// sa fermeture. Le module qui avait répondu OK et est en état "locked" (engagé à fermer)
    /// libère son verrou et reprend son fonctionnement normal.
    /// Format : <c>CAN_CLOSE_ABORTED</c> (sans payload).
    /// <para>Le SDK module appelle <see cref="IWpsModule.OnCanCloseAborted"/> à réception. Si
    /// le module n'était pas en état locked, le message est ignoré (idempotent).</para></summary>
    public const string CmdCanCloseAborted = "CAN_CLOSE_ABORTED";

    // ====== Vocabulaire v1.3 Module → Host ======

    /// <summary>(v1.3) Réponse OK au CAN_CLOSE : le module est libre, il peut fermer.
    /// À partir de maintenant et jusqu'à réception du CLOSE (ou CAN_CLOSE_ABORTED), le
    /// module est en état "locked" — le SDK bloque les signaux applicatifs qui pourraient
    /// le rendre Busy (timer, message réseau, etc.) pour préserver l'engagement.
    /// Format : <c>CAN_CLOSE_OK</c> (sans payload).</summary>
    public const string NotifCanCloseOk = "CAN_CLOSE_OK";

    /// <summary>(v1.3) Réponse BUSY au CAN_CLOSE : le module est occupé. Le host attend
    /// l'envoi périodique de <see cref="NotifBusyProgress"/> + le passage final en
    /// <see cref="NotifCanCloseOk"/>. Si le module ne renvoie rien pendant
    /// <c>BusyHeartbeatTimeoutMs</c> (8000ms par défaut), le host considère le module
    /// figé et passe au Kill direct.
    /// Format : <c>CAN_CLOSE_BUSY|estimatedMs|reason</c> où <c>estimatedMs</c> est un
    /// entier (estimation du temps restant en ms, ou <c>-1</c> si indéterminé), et
    /// <c>reason</c> est un texte court affichable (peut contenir des <c>|</c> — c'est
    /// la dernière portion de trame, reconstruite via string.Join côté parser).</summary>
    public const string NotifCanCloseBusy = "CAN_CLOSE_BUSY";

    /// <summary>(v1.3) Réponse NEED_USER au CAN_CLOSE : le module a besoin d'une interaction
    /// utilisateur (dialog "voulez-vous sauvegarder ?", confirmation, etc.). Le host bascule
    /// l'onglet sur ce module + lui donne le focus clavier (<c>SetFocus</c> sur le HWND
    /// embarqué, pas <c>SetForegroundWindow</c> qui est restreint cross-process). Le module
    /// gère son dialog ; quand l'user a tranché, il renvoie <see cref="NotifCanCloseOk"/>
    /// ou <see cref="NotifCanCloseRejected"/>.
    /// Format : <c>CAN_CLOSE_NEED_USER|reason</c> où <c>reason</c> est affiché par le host
    /// dans son overlay pendant le basculement (peut contenir des <c>|</c>).
    /// <para>Si <c>isUrgent=1</c> dans le CAN_CLOSE entrant, le SDK module clamp NeedUser
    /// en Busy automatiquement — pas de dialog pendant un shutdown OS.</para></summary>
    public const string NotifCanCloseNeedUser = "CAN_CLOSE_NEED_USER";

    /// <summary>(v1.3) Réponse REJECTED au CAN_CLOSE : l'utilisateur a explicitement refusé
    /// la fermeture (cliqué "Annuler" sur un dialog du module). Le host doit annuler sa
    /// propre fermeture en cascade : pour les autres modules ayant déjà répondu OK et
    /// engagés en lock, il envoie <see cref="CmdCanCloseAborted"/> pour libérer leur verrou.
    /// Pour les modules en BUSY, il termine la séquence courante (laisse finir le cleanup
    /// éventuellement, mais ne déclenche pas le CLOSE final).
    /// Format : <c>CAN_CLOSE_REJECTED|reason</c> (peut contenir des <c>|</c>).
    /// <para>Si <c>isUrgent=1</c>, clamp en Busy par le SDK module.</para></summary>
    public const string NotifCanCloseRejected = "CAN_CLOSE_REJECTED";

    /// <summary>(v1.3) Mise à jour pendant un BUSY long. Le module DEVRAIT envoyer un
    /// BUSY_PROGRESS toutes les ~3s (= moitié du <c>BusyHeartbeatTimeoutMs</c> 8s) pour
    /// prouver qu'il est toujours vivant et qu'il avance. Le host affiche le message dans
    /// son overlay et reset le timer de heartbeat.
    /// Format : <c>BUSY_PROGRESS|percent|message</c> où <c>percent</c> ∈ <c>[0, 100]</c>
    /// (entier) ou <c>-1</c> si indéterminé, et <c>message</c> est le texte affiché (peut
    /// contenir des <c>|</c>).</summary>
    public const string NotifBusyProgress = "BUSY_PROGRESS";

    /// <summary>(v1.3) Phase finale : le module a fini son cleanup applicatif et le process
    /// va exit immédiatement après. Le host peut considérer ce module comme proprement fermé
    /// (vs Kill fallback en cas de timeout ou crash).
    /// Format : <c>CLOSING_DONE</c> (sans payload).
    /// <para>Si le SDK ne reçoit pas CLOSING_DONE avant <c>CleanupGracePeriodMs</c> (7000ms
    /// par défaut), le host force le Kill — mais c'est censé être l'exception, pas la règle.</para></summary>
    public const string NotifClosingDone = "CLOSING_DONE";

    /// <summary>(v1.3) Le module se ferme à son initiative (bouton Quitter dans son UI,
    /// logique métier qui termine, etc.). Permet au host de griser le slot proprement (état
    /// "Closed" plutôt que "Failed" qui est réservé au crash non sollicité).
    /// Format : <c>SELF_CLOSING|reason</c> où <c>reason</c> est un texte libre (ex:
    /// <c>"user-quit"</c>, <c>"work-done"</c>) — peut contenir des <c>|</c>.
    /// <para>Le module peut envoyer SELF_CLOSING puis enchaîner directement Window.Close /
    /// Application.Shutdown. Le host verra <see cref="System.Diagnostics.Process.Exited"/>
    /// peu après et fera le cleanup côté slot.</para></summary>
    public const string NotifSelfClosing = "SELF_CLOSING";
}
