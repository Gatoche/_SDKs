namespace Wps.Module;

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
/// // Module veut une confirmation utilisateur. Le HOST affiche la modale —
/// // l'app fournit la question + les boutons (id → label).
/// return CanCloseDecision.NeedUser(
///     reason: "Document non sauvegardé",
///     question: "Voulez-vous fermer ?",
///     answers: new Dictionary&lt;string, string&gt;
///     {
///         ["yes"]       = "Oui",
///         ["yes-after"] = "Oui après traitement",
///         ["no"]        = "Non",
///     },
///     allowClose: false);
///
/// // Refus net immédiat, sans demander à l'utilisateur.
/// return CanCloseDecision.Rejected("Travail en cours, veuillez sauvegarder d'abord");
/// </code>
///
/// <para><b>Note IsUrgent :</b> si le contexte du CAN_CLOSE a <c>IsUrgent = true</c>
/// (= shutdown OS), le SDK module clamp automatiquement <see cref="NeedUserD"/> et
/// <see cref="RejectedD"/> en <see cref="BusyD"/> avec un budget de 2000ms — pas de
/// dialog ni de veto pendant un shutdown système.</para>
///
/// <para><b>Architecture NeedUser depuis v1.3 final :</b> la modale est affichée
/// <i>côté host</i>, pas côté module. Le module ne fait que déclarer la question + le
/// dictionnaire <c>id → label</c> des boutons proposés ; le host affiche la dialog,
/// l'utilisateur clique sur un bouton, le host envoie l'id au module via
/// <c>USER_RESPONSE</c>. Le SDK module mappe automatiquement les ids réservés
/// <c>yes</c>/<c>ok</c> en <see cref="OkD"/> et <c>no</c>/<c>cancel</c> en
/// <see cref="RejectedD"/>. Pour des ids custom (ex: <c>yes-after</c>), l'app implémente
/// la DIM optionnelle <see cref="IWpsModule.OnUserResponseAsync"/> qui retourne la
/// décision finale (typiquement <see cref="BusyD"/> pour un "Oui mais après traitement").</para>
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
    /// (<paramref name="Question"/> + boutons générés depuis <paramref name="Answers"/>) ;
    /// <paramref name="Reason"/> est utilisé en sous-titre / contexte. Si
    /// <paramref name="AllowClose"/> = false, la croix de fermeture est masquée et Esc
    /// neutralisé — l'utilisateur DOIT cliquer un bouton.
    /// <para><c>Answers</c> est un dictionnaire ordonné <c>id → label</c> :</para>
    /// <list type="bullet">
    ///   <item>L'<b>ordre d'insertion</b> est préservé (Dictionary&lt;,&gt; .NET Core 3.0+)
    ///         et utilisé pour l'ordre d'affichage des boutons côté host.</item>
    ///   <item>Le <b>1er bouton</b> est le bouton par défaut (touche Enter).</item>
    ///   <item>L'<b>id</b> est libre et opaque ; le module le récupère via
    ///         <see cref="IWpsModule.OnUserResponseAsync"/> pour décider de la suite.</item>
    ///   <item>Les ids réservés <c>yes</c>, <c>ok</c>, <c>no</c>, <c>cancel</c> bénéficient
    ///         d'un mapping standard automatique (yes/ok → Ok, no/cancel → Rejected) si
    ///         l'app n'override pas via la DIM <see cref="IWpsModule.OnUserResponseAsync"/>.</item>
    /// </list>
    /// Clamp en Busy si IsUrgent=true.</summary>
    public sealed record NeedUserD(
        string Reason,
        string Question,
        IReadOnlyDictionary<string, string> Answers,
        bool AllowClose) : CanCloseDecision;

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
    /// sauvegardé").</param>
    /// <param name="question">Question principale affichée à l'utilisateur.</param>
    /// <param name="answers">Dictionnaire ordonné <c>id → label</c> (l'ordre d'insertion
    /// est utilisé pour l'affichage des boutons). Le 1er est le bouton par défaut.</param>
    /// <param name="allowClose">Si <c>true</c>, la croix de fermeture et Esc sont actifs
    /// (l'utilisateur peut sortir sans choisir un bouton — équivalent à un id <c>cancel</c>
    /// implicite). Par défaut <c>false</c> : l'utilisateur DOIT cliquer un bouton.</param>
    public static CanCloseDecision NeedUser(
        string reason,
        string question,
        IReadOnlyDictionary<string, string> answers,
        bool allowClose = false)
        => new NeedUserD(reason ?? "", question ?? "", answers ?? new Dictionary<string, string>(), allowClose);

    /// <summary>Crée un <see cref="RejectedD"/> avec la raison du refus (affichée côté host
    /// pour informer l'utilisateur que la fermeture du host a été annulée).</summary>
    public static CanCloseDecision Rejected(string reason)
        => new RejectedD(reason ?? "");
}
