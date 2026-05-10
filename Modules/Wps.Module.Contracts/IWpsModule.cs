using System.Threading.Tasks;

namespace Wps.Module;

/// <summary>
/// Hooks lifecycle optionnels pour un module wipiSoft. Implémenter cette interface (typiquement
/// sur la <c>MainWindow</c>) puis l'enregistrer via <c>WpsModule.Register(this)</c> permet de
/// recevoir les notifications du host pertinentes pour le code applicatif (close demandé,
/// redimensionnement, négociation de fermeture, déconnexion du host, etc.).
///
/// <para>Tous les hooks ont une implémentation par défaut (DIM, C# 8+) — un module n'override
/// que ce dont il a besoin. Si un module n'enregistre rien, le SDK gère quand même toute la
/// mécanique IPC (heartbeat, ready, shutdown négocié) en silence avec les comportements par
/// défaut (Ok pour CAN_CLOSE, no-op pour les notifications, auto-shutdown sur déconnexion).</para>
///
/// <para><b>Évolution v1.3 :</b> ajout de <see cref="OnCanCloseRequestedAsync"/> (négociation
/// d'arrêt avec le host, peut renvoyer Busy/NeedUser/Rejected), <see cref="OnCanCloseAborted"/>
/// (libération du verrou si la cascade est annulée), <see cref="OnHostDisconnected"/> (host mort
/// ou figé). La sémantique de <see cref="OnShutdownRequested"/> est précisée : c'est désormais
/// la phase finale d'un cycle de fermeture, après que le module ait répondu Ok ou en mode legacy
/// v1.2 (où c'est appelé directement à réception du CLOSE sans phase CAN_CLOSE préalable).</para>
/// </summary>
public interface IWpsModule
{
    /// <summary>Le host est connecté et a accepté la version du contrat (WELCOME reçu).
    /// Les pipes IPC sont ouverts. À ce stade le READY n'est PAS encore envoyé : si tu fais
    /// <c>WpsModule.Bootstrap(autoReady: false)</c>, c'est ici typiquement que tu peux
    /// déclencher ton init asynchrone (browser, etc.) avant d'appeler
    /// <c>WpsModule.NotifyReadyAsync()</c> quand tout est prêt.</summary>
    void OnHostConnected() { }

    /// <summary>(v1.3) Le host demande "peux-tu fermer maintenant ?". L'app retourne une
    /// <see cref="CanCloseDecision"/> :
    /// <list type="bullet">
    ///   <item><see cref="CanCloseDecision.Ok"/> : libre, je m'engage à fermer</item>
    ///   <item><see cref="CanCloseDecision.Busy"/> : occupé, voici une estimation</item>
    ///   <item><see cref="CanCloseDecision.NeedUser"/> : j'ai besoin de l'utilisateur (dialog)</item>
    ///   <item><see cref="CanCloseDecision.Rejected"/> : l'utilisateur refuse la fermeture</item>
    /// </list>
    ///
    /// <para>DIM par défaut = Ok (rétrocompat avec le comportement v1.2 où le module fermait
    /// immédiatement à réception du CLOSE).</para>
    ///
    /// <para>Pour les BUSY longs, après le retour de cette méthode l'app peut envoyer des
    /// <c>BUSY_PROGRESS</c> via <c>WpsModule.ReportBusyProgress(...)</c> pour informer le host
    /// de la progression et reset le timer de heartbeat (sinon Kill après ~8s de silence).</para>
    ///
    /// <para>Pour les NEED_USER, après le retour de cette méthode le host bascule l'onglet sur
    /// le module et donne le focus. L'app affiche son dialog. Quand l'utilisateur tranche,
    /// l'app appelle <c>WpsModule.ResolveCanClose(...)</c> avec sa décision finale.</para>
    ///
    /// <para><b>Mode urgent (shutdown OS) :</b> si <paramref name="ctx"/>.IsUrgent = true, le
    /// SDK module clamp automatiquement NeedUser et Rejected en Busy(2000ms). L'app peut
    /// consulter <c>ctx.IsUrgent</c> pour adapter son cleanup (skip dialog, sauvegarde
    /// minimale) mais elle n'a pas à gérer le clamp elle-même.</para>
    /// </summary>
    ValueTask<CanCloseDecision> OnCanCloseRequestedAsync(CanCloseContext ctx)
        => new(CanCloseDecision.Ok);

    /// <summary>(v1.3) La fermeture engagée a été annulée — un autre module a répondu Rejected
    /// dans la cascade (ou l'utilisateur a fermé une fenêtre de confirmation côté host). Si le
    /// module avait répondu Ok à <see cref="OnCanCloseRequestedAsync"/> et qu'il était en état
    /// "locked" (verrou SDK qui bufferise les signaux applicatifs entrants), ce hook signale la
    /// libération : reprendre les traitements normaux, vider les caches éventuels, etc.
    /// <para>Idempotent : si le module n'était pas en état locked, l'appel est safely ignoré.</para>
    /// </summary>
    void OnCanCloseAborted() { }

    /// <summary>(v1.3) Le host est considéré mort ou figé. Cas détectés :
    /// <list type="bullet">
    ///   <item><see cref="HostDisconnectReason.PipeClosed"/> : le pipe Notif a été coupé
    ///         (host crashé, killé brutalement) — détecté instantanément</item>
    ///   <item><see cref="HostDisconnectReason.HeartbeatSilent"/> : aucun PING reçu du host
    ///         pendant > 30s (host probablement en deadlock UI thread)</item>
    /// </list>
    ///
    /// <para>L'app peut profiter de ce hook pour faire un cleanup d'urgence (sauvegarde
    /// best-effort, log, etc.). <b>Le SDK module fait automatiquement
    /// <c>Application.Current.Shutdown()</c> après le retour de ce hook</b> — pas besoin
    /// que l'app le fasse elle-même. Si l'app a un cleanup async qui doit finir avant le
    /// shutdown, faire un await synchrone dans ce hook (ne PAS fire-and-forget — le shutdown
    /// auto suivra immédiatement).</para>
    ///
    /// <para>DIM vide par défaut : l'app n'a rien à faire de spécial, le SDK gère le shutdown.</para>
    /// </summary>
    void OnHostDisconnected(HostDisconnectReason reason) { }

    /// <summary>Le host demande au module de s'arrêter — phase finale de fermeture.
    ///
    /// <para><b>v1.3 :</b> appelé après que le module ait répondu <see cref="CanCloseDecision.Ok"/>
    /// à <see cref="OnCanCloseRequestedAsync"/> et que le host ait envoyé <c>CLOSE</c>. Le module
    /// fait son cleanup applicatif (flush, dispose des ressources) puis appelle
    /// <c>Window.Close()</c> ou <c>Application.Shutdown()</c>. Le SDK envoie automatiquement
    /// <c>CLOSING_DONE</c> au host avant exit pour signaler une fermeture propre (vs Kill
    /// fallback).</para>
    ///
    /// <para><b>v1.2 (legacy) :</b> appelé directement à réception du <c>CLOSE</c> sans phase
    /// CAN_CLOSE préalable. Comportement identique du point de vue de l'app, le code écrit
    /// pour v1.2 fonctionne tel quel en v1.3.</para>
    ///
    /// <para>L'app ne devrait pas appeler de logique longue (> grace period par défaut 7s)
    /// dans ce hook — sinon Kill fallback. Pour des cleanups longs, déclarer la durée via
    /// <see cref="OnCanCloseRequestedAsync"/> retournant Busy avec un estimatedMs adéquat.</para>
    /// </summary>
    void OnShutdownRequested() { }

    /// <summary>Le host signale les dimensions courantes du slot d'affichage (en DIPs).
    /// Permet au module de pré-rendre à la bonne taille avant d'être visible.
    /// Émis y compris quand le module est parké (-32000) → utile pour les compositors lourds
    /// (CefSharp, WebView2) qui doivent layouter à la bonne taille hors écran.</summary>
    void OnResizeRequested(double dipW, double dipH, double dpi) { }
}

// ================================================================================
// ====== Types compagnons utilisés dans les signatures de IWpsModule ===========
// ================================================================================

/// <summary>(v1.3) Contexte d'un appel <see cref="IWpsModule.OnCanCloseRequestedAsync"/>.
/// Permet au module de connaître l'origine de la demande de fermeture.</summary>
public sealed class CanCloseContext
{
    /// <summary>True si la demande vient d'un shutdown OS (Windows logoff/reboot/poweroff).
    /// Dans ce mode, le SDK module clamp automatiquement les décisions NeedUser et Rejected
    /// en Busy(2000ms) — le timer Windows ~5s tue de toute façon le process passé son
    /// timeout, inutile de bloquer ou d'afficher un dialog que l'utilisateur ne verra pas.
    /// L'app peut consulter ce flag pour adapter son cleanup (sauvegarde minimale, skip
    /// confirmations, etc.).</summary>
    public bool IsUrgent { get; init; }
}

/// <summary>(v1.3) Mise à jour de progression envoyée par le module au host pendant un
/// CAN_CLOSE_BUSY long. À envoyer toutes les ~3s (= moitié du heartbeat timeout 8s) pour
/// prouver que le module est vivant et reset le timer côté host.</summary>
/// <param name="Percent">Pourcentage de progression dans [0, 100], ou <c>-1</c> si indéterminé.</param>
/// <param name="Message">Texte affiché par le host dans son overlay (ex: "Sauvegarde des
/// préférences...", "Désenregistrement AppBar...").</param>
public sealed record BusyProgress(int Percent, string Message);

/// <summary>(v1.3) Raison d'un appel <see cref="IWpsModule.OnHostDisconnected"/>. Permet à
/// l'app d'adapter son comportement selon la nature de la déconnexion (crash brutal vs
/// deadlock UI thread).</summary>
public enum HostDisconnectReason
{
    /// <summary>Le pipe Notif (Module → Host) a été coupé. Cause typique : le process host a
    /// disparu (crash, kill brutal) — l'OS ferme automatiquement les pipes du process mort,
    /// le module est notifié instantanément côté <c>WpsPipeDuplex.Closed</c>.</summary>
    PipeClosed,

    /// <summary>Aucun PING reçu du host pendant > 30s. Le pipe est encore ouvert (le process
    /// host est encore vivant) mais ne répond plus — typiquement un deadlock du UI thread
    /// côté host. Pas réversible en pratique : un host figé > 30s n'est pas un host qui peut
    /// repartir, mieux vaut que le module se ferme.</summary>
    HeartbeatSilent,
}
