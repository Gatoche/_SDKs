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
/// // Module a un dialog à afficher à l'utilisateur (host bascule l'onglet + focus).
/// return CanCloseDecision.NeedUser("Document non sauvegardé");
///
/// // Utilisateur a explicitement refusé la fermeture.
/// return CanCloseDecision.Rejected("Travail en cours, veuillez sauvegarder d'abord");
/// </code>
///
/// <para><b>Note IsUrgent :</b> si le contexte du CAN_CLOSE a <c>IsUrgent = true</c>
/// (= shutdown OS), le SDK module clamp automatiquement <see cref="NeedUserD"/> et
/// <see cref="RejectedD"/> en <see cref="BusyD"/> avec un budget de 2000ms — pas de
/// dialog ni de veto pendant un shutdown système, l'utilisateur a déjà tranché côté
/// OS et Windows tuera le process passé son timeout indépendamment de ce qu'on dit.</para>
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

    /// <summary>Module veut afficher un dialog à l'utilisateur (genre "voulez-vous
    /// sauvegarder ?"). Le host bascule l'onglet et donne le focus avant que l'app
    /// ne montre son dialog. Quand l'user tranche, l'app rappelle l'orchestrateur via
    /// la décision finale (Ok ou Rejected). Clamp en Busy si IsUrgent=true.</summary>
    public sealed record NeedUserD(string Reason) : CanCloseDecision;

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

    /// <summary>Crée un <see cref="NeedUserD"/> avec la raison affichée pendant le basculement.</summary>
    public static CanCloseDecision NeedUser(string reason)
        => new NeedUserD(reason ?? "");

    /// <summary>Crée un <see cref="RejectedD"/> avec la raison du refus (affichée côté host
    /// pour informer l'utilisateur que la fermeture du host a été annulée).</summary>
    public static CanCloseDecision Rejected(string reason)
        => new RejectedD(reason ?? "");
}
