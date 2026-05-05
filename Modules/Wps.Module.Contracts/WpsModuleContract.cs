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
/// </summary>
public static class WpsModuleContract
{
    /// <summary>Version du contrat module ↔ host (semver "major.minor"). Annoncée au handshake HELLO.</summary>
    public const string CurrentVersion = "1.2";

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
}
