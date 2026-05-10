namespace Wps.Module;

/// <summary>
/// Boutons proposés à l'utilisateur dans une modale de confirmation déclenchée par
/// <see cref="CanCloseDecision.NeedUser"/>. Mappé directement sur <c>MessageBoxButton</c> WPF
/// côté host (qui affiche la modale), mais redéfini ici pour ne pas dépendre de WPF dans
/// le contracts assembly (le SDK Module/ModuleService utilise ces enums sans linker WPF).
/// </summary>
public enum WpsDialogButtons
{
    /// <summary>Bouton OK uniquement (information non actionnable).</summary>
    Ok,

    /// <summary>OK / Annuler (action réversible).</summary>
    OkCancel,

    /// <summary>Oui / Non (choix binaire).</summary>
    YesNo,

    /// <summary>Oui / Non / Annuler (choix ternaire — typique "voulez-vous sauvegarder ?").</summary>
    YesNoCancel,
}

/// <summary>
/// Résultat retourné par la modale affichée côté host. Mappé sur <c>MessageBoxResult</c> WPF
/// côté host, redéfini ici pour les mêmes raisons que <see cref="WpsDialogButtons"/>.
/// Transporté dans le payload <c>USER_RESPONSE</c> du wire-protocol v1.3.
/// </summary>
public enum WpsDialogResult
{
    /// <summary>Bouton OK cliqué (équivalent "j'accepte de fermer").</summary>
    Ok,

    /// <summary>Annuler cliqué — équivalent à un Rejected côté module.</summary>
    Cancel,

    /// <summary>Oui cliqué — équivalent à un Ok côté module.</summary>
    Yes,

    /// <summary>Non cliqué — équivalent à un Rejected côté module.</summary>
    No,
}

/// <summary>
/// Décision retournée par <see cref="IWpsModule.OnCanCloseRequestedAsync"/> en réponse à
/// un <c>CAN_CLOSE</c> du host. Modèle un type-somme à 4 cas — pattern <c>switch</c>
/// recommandé côté SDK pour traiter chaque variante.
///
/// <para>Construction via les fabriques statiques :</para>
/// <code>
/// // Module libre, peut fermer immédiatement (cas par défaut, DIM de l'interface).
/// return CanCloseDecision.Ok;
///
/// // Module occupé, estimation du temps restant en ms (-1 si indéterminé).
/// return CanCloseDecision.Busy("Désenregistrement AppBar...", 3000);
///
/// // Module veut une confirmation utilisateur — la modale est affichée par le HOST,
/// // pas par le module. L'app fournit juste la question + les boutons. Le SDK reçoit
/// // ensuite USER_RESPONSE et résout automatiquement en Ok ou Rejected.
/// return CanCloseDecision.NeedUser(
///     reason: "Document non sauvegardé",
///     question: "Voulez-vous fermer sans sauvegarder ?",
///     buttons: WpsDialogButtons.YesNoCancel);
///
/// // Refus net immédiat, sans demander à l'utilisateur.
/// return CanCloseDecision.Rejected("Travail en cours, veuillez sauvegarder d'abord");
/// </code>
///
/// <para><b>Note IsUrgent :</b> si le contexte du CAN_CLOSE a <c>IsUrgent = true</c>
/// (= shutdown OS), le SDK module clamp automatiquement <see cref="NeedUserD"/> et
/// <see cref="RejectedD"/> en <see cref="BusyD"/> avec un budget de 2000ms — pas de
/// dialog ni de veto pendant un shutdown système, l'utilisateur a déjà tranché côté
/// OS et Windows tuera le process passé son timeout indépendamment de ce qu'on dit.</para>
///
/// <para><b>Architecture NeedUser depuis v1.3 final :</b> la modale est affichée
/// <i>côté host</i>, pas côté module. Avantages : services console pures compatibles
/// nativement (pas besoin d'Application WPF côté service), pas de focus war
/// cross-process, style cohérent host, sérialisation garantie (une seule modale
/// même si N modules répondent NeedUser). Le module ne fait que déclarer la
/// question + les boutons ; le host affiche la MessageBox et renvoie le résultat
/// via <c>USER_RESPONSE</c> ; le SDK module mappe automatiquement
/// Yes/Ok → <see cref="OkD"/>, No/Cancel → <see cref="RejectedD"/>.</para>
/// </summary>
public abstract record CanCloseDecision
{
    private CanCloseDecision() { }  // hiérarchie fermée : seules les 4 variantes ci-dessous

    /// <summary>Module libre : il peut fermer immédiatement. Variante par défaut de
    /// <see cref="IWpsModule.OnCanCloseRequestedAsync"/>.</summary>
    public sealed record OkD : CanCloseDecision { internal OkD() { } }

    /// <summary>Module occupé : nécessite encore <paramref name="EstimatedMs"/> ms
    /// (ou indéterminé si -1) pour terminer son traitement courant avant de pouvoir
    /// fermer. Le host attend des <c>BUSY_PROGRESS</c> périodiques + un OK final.</summary>
    public sealed record BusyD(string Reason, int EstimatedMs) : CanCloseDecision;

    /// <summary>Module veut une confirmation utilisateur. Le HOST affiche la modale
    /// (<paramref name="Question"/> + <paramref name="Buttons"/>, titre = nom du module
    /// + <paramref name="Reason"/>). Le module reçoit la décision via <c>USER_RESPONSE</c>
    /// — le SDK la mappe en Ok (Yes/Ok) ou Rejected (No/Cancel) automatiquement.
    /// Clamp en Busy si IsUrgent=true.</summary>
    public sealed record NeedUserD(string Reason, string Question, WpsDialogButtons Buttons)
        : CanCloseDecision;

    /// <summary>Utilisateur a refusé la fermeture. Le host annule sa fermeture et
    /// envoie CAN_CLOSE_ABORTED aux autres modules déjà OK pour libérer leur verrou.
    /// Clamp en Busy si IsUrgent=true.</summary>
    public sealed record RejectedD(string Reason) : CanCloseDecision;

    // ====== Fabriques statiques (API publique, ergonomie) ======

    /// <summary>Singleton : <see cref="OkD"/>. Pas d'allocation à chaque appel.</summary>
    public static readonly CanCloseDecision Ok = new OkD();

    /// <summary>Crée un <see cref="BusyD"/> avec une estimation du temps restant.</summary>
    /// <param name="reason">Texte affiché par le host dans l'overlay (ex: "Sauvegarde en cours...").</param>
    /// <param name="estimatedMs">Estimation en ms ou <c>-1</c> si indéterminé.</param>
    public static CanCloseDecision Busy(string reason, int estimatedMs = -1)
        => new BusyD(reason ?? "", estimatedMs);

    /// <summary>Crée un <see cref="NeedUserD"/>. Le HOST affiche la modale —
    /// le module ne fait que déclarer la question et les boutons.</summary>
    /// <param name="reason">Sous-titre / contexte affiché côté host (ex: "Document non
    /// sauvegardé"). Utilisé dans le titre de la modale ou en zone détails.</param>
    /// <param name="question">Question principale affichée à l'utilisateur (ex:
    /// "Voulez-vous fermer sans sauvegarder ?").</param>
    /// <param name="buttons">Boutons proposés. Par défaut <see cref="WpsDialogButtons.YesNoCancel"/>.</param>
    public static CanCloseDecision NeedUser(
        string reason,
        string question,
        WpsDialogButtons buttons = WpsDialogButtons.YesNoCancel)
        => new NeedUserD(reason ?? "", question ?? "", buttons);

    /// <summary>Crée un <see cref="RejectedD"/> avec la raison du refus (affichée côté host
    /// pour informer l'utilisateur que la fermeture du host a été annulée).</summary>
    public static CanCloseDecision Rejected(string reason)
        => new RejectedD(reason ?? "");
}
