using Wps.Module.Core;

namespace Wps.Module.Hosting;

/// <summary>
/// Orchestre la fermeture coordonnée de N modules embedded (mix Module / ModuleService) selon
/// l'algorithme "option A2" décidé en design :
///
/// <para>1. <b>Parallel-collect</b> : envoi de <c>CAN_CLOSE</c> à TOUS les targets en parallèle,
///    collecte des réponses dans une fenêtre de timeout (<see cref="ShutdownOptions.CanCloseTimeoutMs"/>)</para>
///
/// <para>2. <b>Cancellation cascade</b> : si au moins un target a répondu <c>Rejected</c>,
///    annulation de la fermeture du host. Les targets ayant répondu <c>Ok</c> et engagés en
///    "lock" reçoivent un <c>CAN_CLOSE_ABORTED</c> pour libérer leur verrou.
///    Outcome = <see cref="HostShutdownOutcome.AbortedByModule"/>.</para>
///
/// <para>3. <b>Queue NEED_USER</b> : pour chaque target ayant répondu <c>NeedUser</c>,
///    bascule séquentielle de l'onglet/focus (via le callback <c>onSwitchTo</c> fourni par le
///    host) et attente de la décision finale (Ok ou Rejected via un nouveau cycle d'events).
///    Évite que N dialogs apparaissent en parallèle si plusieurs modules en ont besoin.</para>
///
/// <para>4. <b>Synchronisation BUSY</b> : attente que les targets en Busy passent en Ok
///    (events <c>CanCloseOk</c> + watchdog <see cref="ShutdownOptions.BusyHeartbeatTimeoutMs"/>).
///    Les targets qui timeoutent en Busy seront killés au CompleteShutdown.</para>
///
/// <para>5. <b>Phase finale parallèle</b> : envoi de <c>CLOSE</c> à tous les targets prêts,
///    attente de <c>CLOSING_DONE</c> en parallèle, fallback Kill pour ceux qui dépassent
///    <see cref="ShutdownOptions.CleanupGracePeriodMs"/>.</para>
///
/// <para><b>Mode urgent (shutdown OS)</b> : <see cref="ShutdownOptions.IsUrgent"/> = true →
/// les NEED_USER et REJECTED sont clampés en Busy côté SDK module (invariant SDK). La phase 3
/// est donc vide en pratique. Les targets non-réactifs sont rapidement killés (timeouts serrés).</para>
///
/// <para><b>Targets legacy v1.2</b> : si un target a <see cref="IWpsShutdownTarget.SupportsNegotiatedShutdown"/>
/// = false, l'orchestrateur ne lui envoie PAS de CAN_CLOSE — il utilise <see cref="WpsModuleSlot.ShutdownAsync(int)"/>
/// (legacy) en parallèle de la phase finale. Ces targets ne peuvent pas REJECTED ni NEED_USER —
/// ils sont fermés en best-effort avec le grace bumpé à <see cref="ShutdownOptions.CleanupGracePeriodMs"/>.</para>
/// </summary>
public sealed class ShutdownOrchestrator
{
    private readonly IReadOnlyList<IWpsShutdownTarget> _targets;
    private const string LogTag = "Wps.Host.Sdk.Orchestrator";

    /// <summary>Crée un orchestrateur pour les targets fournis. La liste est figée à la
    /// construction — ajouter/retirer un target en cours d'orchestration n'est pas supporté
    /// (cas d'usage rare et complexe à raisonner ; à reconsidérer si besoin).</summary>
    public ShutdownOrchestrator(IReadOnlyList<IWpsShutdownTarget> targets)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    /// <summary>
    /// Exécute la séquence Option A2 sur tous les targets. Retourne l'outcome global :
    /// <see cref="HostShutdownOutcome.Completed"/> si tout s'est bien fermé, <see cref="HostShutdownOutcome.AbortedByModule"/>
    /// si un module a refusé la cascade (le host doit annuler sa fermeture), <see cref="HostShutdownOutcome.AbortedByUser"/>
    /// si <paramref name="ct"/> a été annulé.
    /// </summary>
    /// <param name="opts">Options de fermeture (timeouts, mode urgent, etc.).</param>
    /// <param name="onShowUserDialog">Callback invoqué (sur le UI thread du host) pour afficher
    /// la modale de confirmation côté HOST quand un target répond NEED_USER. Reçoit
    /// <c>(target, payload)</c> où le payload contient <c>reason</c>, <c>ask</c>,
    /// <c>answers</c> dict (id → label) et <c>allowClose</c>. Doit retourner l'id (string) du
    /// bouton cliqué par l'utilisateur. L'orchestrateur transmet cet id au module via
    /// <c>USER_RESPONSE</c> ; le SDK module appelle la DIM <c>OnUserResponseAsync</c> (override
    /// applicatif) puis tombe sur le mapping standard pour les ids réservés (yes/ok → Ok,
    /// no/cancel → Rejected). Le host est libre de faire un switch onglet vers le target en
    /// plus de l'affichage de la modale, pour le contexte visuel.</param>
    /// <param name="progress">Optionnel : reçoit des mises à jour de progression pendant la
    /// séquence (target en cours, état, message). Utile pour un overlay UI dans le host.</param>
    /// <param name="ct">Cancellation pour annuler la séquence (ex: l'utilisateur annule la
    /// fermeture du host pendant un Busy long).</param>
    /// <param name="onBeforeTargetClose">Callback optionnel invoqué (sur le UI thread du host)
    /// juste avant que <c>CompleteShutdownAsync</c> ne soit appelé sur un target — i.e. juste
    /// avant que le CLOSE final ne parte. Permet au host de capturer un dernier snapshot du
    /// module et de parker son HWND, pour que l'utilisateur voie l'image figée du module
    /// pendant le cleanup et le tear-down (au lieu d'une page noire quand le HWND est détruit).
    /// Si null, pas de capture — comportement default.</param>
    public async Task<HostShutdownOutcome> ExecuteAsync(
        ShutdownOptions opts,
        Func<IWpsShutdownTarget, NeedUserPayload, Task<string>> onShowUserDialog,
        IProgress<ShutdownProgress>? progress = null,
        CancellationToken ct = default,
        Func<IWpsShutdownTarget, Task>? onBeforeTargetClose = null)
    {
        if (_targets.Count == 0) return HostShutdownOutcome.Completed;

        WpsDebugSender.Log(
            $"ExecuteAsync: {_targets.Count} target(s), urgent={opts.IsUrgent}",
            LogLevel.Info, LogTag);
        progress?.Report(new ShutdownProgress(null, ShutdownPhase.Starting, $"{_targets.Count} target(s)"));

        // Sépare les targets supportant v1.3 vs legacy
        var v13Targets = _targets.Where(t => t.SupportsNegotiatedShutdown).ToList();
        var legacyTargets = _targets.Where(t => !t.SupportsNegotiatedShutdown).ToList();

        if (legacyTargets.Count > 0)
        {
            WpsDebugSender.Log(
                $"ExecuteAsync: {legacyTargets.Count} target(s) legacy v1.2 — closure parallèle via ShutdownAsync(int)",
                LogLevel.Trace, LogTag);
        }

        // ====== Phase 1 : parallel-collect des CAN_CLOSE pour les targets v1.3 ======
        progress?.Report(new ShutdownProgress(null, ShutdownPhase.RequestingCanClose, "Phase 1 : CAN_CLOSE parallel"));
        var canCloseResults = new Dictionary<IWpsShutdownTarget, CanCloseResponse>();
        var phase1Tasks = v13Targets.Select(async t =>
        {
            try
            {
                var resp = await t.RequestCanCloseAsync(opts, ct).ConfigureAwait(false);
                lock (canCloseResults) canCloseResults[t] = resp;
                WpsDebugSender.Log($"Phase 1: '{t.Name}' → {resp.GetType().Name}", LogLevel.Trace, LogTag);
            }
            catch (Exception ex)
            {
                WpsDebugSender.Log($"Phase 1: '{t.Name}' threw {ex.GetType().Name}: {ex.Message} — Timeout",
                    LogLevel.Warning, LogTag);
                lock (canCloseResults) canCloseResults[t] = CanCloseResponse.Timeout;
            }
        }).ToList();
        try { await Task.WhenAll(phase1Tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { return HostShutdownOutcome.AbortedByUser; }

        // ====== Phase 2 : détection cancellation cascade ======
        var rejected = canCloseResults.FirstOrDefault(kv => kv.Value is CanCloseResponse.RejectedR);
        if (rejected.Key is not null)
        {
            var reason = ((CanCloseResponse.RejectedR)rejected.Value).Reason;
            WpsDebugSender.Log(
                $"Phase 2: '{rejected.Key.Name}' REJECTED ({reason}) — cancellation cascade",
                LogLevel.Info, LogTag);
            progress?.Report(new ShutdownProgress(rejected.Key, ShutdownPhase.Aborted, $"Refus : {reason}"));

            // Libère les verrous des targets ayant répondu Ok
            var lockedTargets = canCloseResults
                .Where(kv => kv.Value is CanCloseResponse.OkR)
                .Select(kv => kv.Key)
                .ToList();
            await Task.WhenAll(lockedTargets.Select(t =>
            {
                WpsDebugSender.Log($"Phase 2: SendCanCloseAbortedAsync to '{t.Name}'", LogLevel.Trace, LogTag);
                return t.SendCanCloseAbortedAsync();
            })).ConfigureAwait(false);

            return HostShutdownOutcome.AbortedByModule;
        }

        // ====== Phase 3 : queue NEED_USER séquentielle (modale affichée HOST-side) ======
        // Le host affiche la modale custom (boutons dynamiques depuis answers fourni par le
        // module). onShowUserDialog → id du bouton cliqué → USER_RESPONSE → DIM
        // OnUserResponseAsync (override applicatif pour ids custom comme "yes-after") puis
        // fallback mapping standard (yes/ok → Ok, no/cancel → Rejected). Résolution effective
        // via WaitForNeedUserResolutionAsync. Si yes-after → Busy → rejoint la liste des
        // Busy à committer juste après Phase 3.
        //
        // ⚠️ Architecture critique : les modules Busy NE démarrent PAS leur travail dès leur
        // réponse à CAN_CLOSE. Ils attendent un signal CAN_CLOSE_COMMITTED envoyé après que
        // toutes les NeedUser aient dit Oui. Sans ça, un Non tardif sur une modale annulerait
        // la fermeture mais les Busy auraient déjà commencé leur cleanup → état applicatif
        // dégradé sans retour possible.
        var needUsers = canCloseResults
            .Where(kv => kv.Value is CanCloseResponse.NeedUserR)
            .Select(kv => (Target: kv.Key, NeedUser: (CanCloseResponse.NeedUserR)kv.Value))
            .ToList();
        foreach (var (target, nu) in needUsers)
        {
            ct.ThrowIfCancellationRequested();
            var payload = nu.Payload;
            WpsDebugSender.Log(
                $"Phase 3: '{target.Name}' NeedUser (reason='{payload.Reason}', answers=[{string.Join(",", payload.Answers.Keys)}], allowClose={payload.AllowClose}) — host displays modal",
                LogLevel.Info, LogTag);
            progress?.Report(new ShutdownProgress(target, ShutdownPhase.UserInteraction, payload.Reason));

            // 1. Affichage de la modale côté host (callback → id du bouton cliqué)
            string buttonId;
            try
            {
                buttonId = await onShowUserDialog(target, payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WpsDebugSender.Log(
                    $"Phase 3: onShowUserDialog('{target.Name}') threw {ex.GetType().Name}: {ex.Message} — fallback id 'cancel' (Rejected via mapping standard)",
                    LogLevel.Warning, LogTag);
                buttonId = "cancel";
            }
            WpsDebugSender.Log(
                $"Phase 3: '{target.Name}' user clicked '{buttonId}' → SendUserResponseAsync",
                LogLevel.Trace, LogTag);

            // 2. Envoi USER_RESPONSE au module
            try
            {
                await target.SendUserResponseAsync(buttonId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WpsDebugSender.Log(
                    $"Phase 3: SendUserResponseAsync('{target.Name}') threw {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Warning, LogTag);
            }

            // 3. Attendre la résolution effective (Ok / Busy / Rejected).
            var followup = await target.WaitForNeedUserResolutionAsync(opts, ct).ConfigureAwait(false);
            lock (canCloseResults) canCloseResults[target] = followup;

            if (followup is CanCloseResponse.RejectedR rej)
            {
                WpsDebugSender.Log(
                    $"Phase 3: '{target.Name}' REJECTED post-USER_RESPONSE ({rej.Reason}) — cascade",
                    LogLevel.Info, LogTag);
                progress?.Report(new ShutdownProgress(target, ShutdownPhase.Aborted, $"Refus : {rej.Reason}"));

                var lockedAfter = canCloseResults
                    .Where(kv => kv.Key != target && kv.Value is CanCloseResponse.OkR)
                    .Select(kv => kv.Key)
                    .ToList();
                await Task.WhenAll(lockedAfter.Select(t => t.SendCanCloseAbortedAsync())).ConfigureAwait(false);
                return HostShutdownOutcome.AbortedByModule;
            }
            // Si followup BusyR (yes-after par ex.), le target rejoint naturellement la liste
            // des Busy ci-dessous via canCloseResults — il recevra son CAN_CLOSE_COMMITTED.
        }

        // ====== Phase 3.5 : envoi CAN_CLOSE_COMMITTED à tous les Busy ======
        // Validation globale : toutes les NeedUser ont dit Oui, aucun Rejected en cascade.
        // C'est ICI seulement que les Busy peuvent commencer leur travail réel. Le SDK module
        // appelle la DIM OnCanCloseCommittedAsync à réception, l'app y lance son travail
        // asynchrone et finira par ResolveCanClose(Ok).
        var busyTargets = canCloseResults
            .Where(kv => kv.Value is CanCloseResponse.BusyR)
            .Select(kv => kv.Key)
            .ToList();
        if (busyTargets.Count > 0)
        {
            WpsDebugSender.Log(
                $"Phase 3.5: SendCanCloseCommittedAsync to {busyTargets.Count} Busy target(s) — démarrage travail applicatif",
                LogLevel.Info, LogTag);
            await Task.WhenAll(busyTargets.Select(async t =>
            {
                try { await t.SendCanCloseCommittedAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    WpsDebugSender.Log(
                        $"Phase 3.5: SendCanCloseCommittedAsync('{t.Name}') threw {ex.GetType().Name}: {ex.Message}",
                        LogLevel.Warning, LogTag);
                }
            })).ConfigureAwait(false);
        }

        // ====== Phase 4 : synchronisation des Busy (attente passage en Ok) ======
        // Maintenant que les Busy ont reçu COMMITTED et démarrent leur travail, on attend
        // leur résolution. WaitForBusyResolutionAsync s'abonne aux events CanCloseOk /
        // CanCloseRejected / CanCloseNeedUser / BusyProgressReceived (heartbeat).
        if (busyTargets.Count > 0)
        {
            // Report avec PendingTargets : le host peut cycler entre les noms ("Fermeture
            // de X en cours…" toutes les ~1s) plutôt qu'un texte générique.
            var pendingBusy = new List<IWpsShutdownTarget>(busyTargets);
            void ReportBusyPending()
            {
                IReadOnlyList<IWpsShutdownTarget> snapshot;
                lock (pendingBusy) snapshot = new List<IWpsShutdownTarget>(pendingBusy);
                progress?.Report(new ShutdownProgress(
                    null, ShutdownPhase.WaitingBusy,
                    $"{snapshot.Count} module(s) en cours…",
                    snapshot));
            }
            ReportBusyPending();

            await Task.WhenAll(busyTargets.Select(async t =>
            {
                try
                {
                    var resolved = await t.WaitForBusyResolutionAsync(opts, ct).ConfigureAwait(false);
                    lock (canCloseResults) canCloseResults[t] = resolved;
                    WpsDebugSender.Log($"Phase 4: '{t.Name}' Busy résolu → {resolved.GetType().Name}",
                        LogLevel.Trace, LogTag);
                }
                catch (Exception ex)
                {
                    WpsDebugSender.Log($"Phase 4: '{t.Name}' WaitForBusyResolutionAsync threw {ex.GetType().Name}: {ex.Message}",
                        LogLevel.Warning, LogTag);
                }
                finally
                {
                    lock (pendingBusy) pendingBusy.Remove(t);
                    ReportBusyPending();
                }
            })).ConfigureAwait(false);

            // Détection Rejected / NeedUser tardifs (cas rares Busy → Rejected ou Busy →
            // NeedUser). NeedUser tardif traité comme Rejected — la queue Phase 3 est finie.
            var lateRejected = canCloseResults.FirstOrDefault(kv =>
                kv.Value is CanCloseResponse.RejectedR or CanCloseResponse.NeedUserR);
            if (lateRejected.Key is not null)
            {
                var reason = lateRejected.Value switch
                {
                    CanCloseResponse.RejectedR r => r.Reason,
                    CanCloseResponse.NeedUserR n => $"NeedUser tardif (post-Busy) : {n.Payload.Reason}",
                    _ => "?",
                };
                WpsDebugSender.Log(
                    $"Phase 4: '{lateRejected.Key.Name}' a tranché en {lateRejected.Value.GetType().Name} ({reason}) → cascade",
                    LogLevel.Info, LogTag);
                progress?.Report(new ShutdownProgress(lateRejected.Key, ShutdownPhase.Aborted, $"Refus tardif : {reason}"));

                var lockedNow = canCloseResults
                    .Where(kv => kv.Key != lateRejected.Key && kv.Value is CanCloseResponse.OkR)
                    .Select(kv => kv.Key)
                    .ToList();
                await Task.WhenAll(lockedNow.Select(t => t.SendCanCloseAbortedAsync())).ConfigureAwait(false);
                return HostShutdownOutcome.AbortedByModule;
            }
        }

        // ====== Phase 5 : envoi CLOSE en parallèle + attente CLOSING_DONE ======
        // (v1.3 final) Report avec PendingTargets : le host peut cycler sur les noms pendant
        // que les modules ferment ("Fermeture de Demo7.Negotiate en cours…"). Mise à jour à
        // chaque target qui termine sa fermeture.
        var allClosingTargets = v13Targets.Concat(legacyTargets).ToList();
        var pendingClosing = new List<IWpsShutdownTarget>(allClosingTargets);
        void ReportClosingPending()
        {
            IReadOnlyList<IWpsShutdownTarget> snapshot;
            lock (pendingClosing) snapshot = new List<IWpsShutdownTarget>(pendingClosing);
            progress?.Report(new ShutdownProgress(
                null, ShutdownPhase.Closing,
                $"Phase finale : {snapshot.Count} target(s) à fermer",
                snapshot));
        }
        ReportClosingPending();

        // Hook UI : juste avant le CLOSE final, on laisse le host capturer un snapshot du
        // module et parker son HWND. L'utilisateur verra l'image figée du module pendant le
        // cleanup + tear-down, pas une page noire quand le HWND est détruit. Invoqué pour
        // tous les targets (v1.3 + legacy) qui vont effectivement être fermés.
        if (onBeforeTargetClose is not null)
        {
            foreach (var t in allClosingTargets)
            {
                try { await onBeforeTargetClose(t).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    WpsDebugSender.Log($"onBeforeTargetClose('{t.Name}') threw {ex.GetType().Name}: {ex.Message}",
                        LogLevel.Warning, LogTag);
                }
            }
        }

        var closeTasks = v13Targets.Select(async t =>
        {
            try
            {
                var result = await t.CompleteShutdownAsync(opts, ct).ConfigureAwait(false);
                WpsDebugSender.Log($"Phase 5: '{t.Name}' → {result}", LogLevel.Trace, LogTag);
            }
            catch (Exception ex)
            {
                WpsDebugSender.Log($"Phase 5: '{t.Name}' CompleteShutdownAsync threw {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Warning, LogTag);
            }
            finally
            {
                lock (pendingClosing) pendingClosing.Remove(t);
                ReportClosingPending();
            }
        }).ToList();

        // ====== Phase 5bis : targets legacy v1.2 traités en parallèle (pas de phase 1-4 pour eux) ======
        // On utilise leur ShutdownAsync(opts) qui route vers le legacy ShutdownAsync(int) interne.
        // Ils sont fermés indépendamment des résultats des targets v1.3.
        var legacyCloseTasks = legacyTargets.Select(async t =>
        {
            try
            {
                if (t is WpsModuleSlot slot)
                    await slot.ShutdownAsync(opts, ct).ConfigureAwait(false);
                else if (t is WpsModuleServiceClient client)
                    await client.ShutdownAsync(opts, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WpsDebugSender.Log($"Phase 5 (legacy): '{t.Name}' threw {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Warning, LogTag);
            }
            finally
            {
                lock (pendingClosing) pendingClosing.Remove(t);
                ReportClosingPending();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(closeTasks.Concat(legacyCloseTasks)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return HostShutdownOutcome.AbortedByUser; }

        progress?.Report(new ShutdownProgress(null, ShutdownPhase.Completed, "Tous les modules fermés"));
        WpsDebugSender.Log("ExecuteAsync: completed", LogLevel.Success, LogTag);
        return HostShutdownOutcome.Completed;
    }
}

/// <summary>Résultat global d'un <see cref="ShutdownOrchestrator.ExecuteAsync"/>.</summary>
public enum HostShutdownOutcome
{
    /// <summary>Tous les targets sont fermés (proprement ou via Kill fallback). Le host peut
    /// terminer son shutdown (<c>Application.Current.Shutdown()</c>).</summary>
    Completed,

    /// <summary>Au moins un target a répondu Rejected. Le host doit annuler sa propre fermeture
    /// (<c>e.Cancel = true</c> dans <c>OnClosing</c>). Les targets ayant répondu Ok ont reçu
    /// CAN_CLOSE_ABORTED pour libérer leurs verrous.</summary>
    AbortedByModule,

    /// <summary>L'utilisateur a annulé la séquence (<see cref="CancellationToken"/>) — par
    /// exemple il a cliqué "Continuer" sur un dialog du host pendant un Busy long.</summary>
    AbortedByUser,
}

/// <summary>Snapshot de progression rapporté par <see cref="ShutdownOrchestrator.ExecuteAsync"/>
/// au cours de la séquence. Permet à un overlay UI du host de refléter l'état courant
/// ("Phase 1...", "Module X demande l'utilisateur...", etc.).</summary>
/// <param name="CurrentTarget">Target en cours de traitement, ou null si phase globale.</param>
/// <param name="Phase">Phase courante de la séquence.</param>
/// <param name="Message">Texte affichable côté UI.</param>
/// <param name="CurrentTarget">Target en cours de traitement, ou null si phase globale.</param>
/// <param name="Phase">Phase courante de la séquence.</param>
/// <param name="Message">Texte affichable côté UI (utilisé en fallback si <paramref name="PendingTargets"/>
/// est vide).</param>
/// <param name="PendingTargets">(v1.3 final) Pour les phases <see cref="ShutdownPhase.WaitingBusy"/>
/// et <see cref="ShutdownPhase.Closing"/> : liste des targets encore en attente de résolution.
/// Permet au host d'afficher leur nom en cycle (toutes les ~1s par exemple) plutôt qu'un
/// message générique "N module(s) en cours…". Mise à jour à chaque target qui sort de la
/// liste (résolu / fermé). Vide ou null pour les autres phases.</param>
public sealed record ShutdownProgress(
    IWpsShutdownTarget? CurrentTarget,
    ShutdownPhase Phase,
    string Message,
    IReadOnlyList<IWpsShutdownTarget>? PendingTargets = null);

/// <summary>Phase courante de l'orchestrateur, pour la UI host.</summary>
public enum ShutdownPhase
{
    Starting,
    RequestingCanClose,
    UserInteraction,
    WaitingBusy,
    Closing,
    Completed,
    Aborted,
}
