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
    /// <param name="onSwitchToTargetForUser">Callback invoqué (sur le UI thread du host) quand
    /// l'orchestrateur veut basculer sur un target en NEED_USER. Le host doit afficher l'onglet
    /// du module et lui donner le focus. Reçoit le target en argument pour identification.</param>
    /// <param name="progress">Optionnel : reçoit des mises à jour de progression pendant la
    /// séquence (target en cours, état, message). Utile pour un overlay UI dans le host.</param>
    /// <param name="ct">Cancellation pour annuler la séquence (ex: l'utilisateur annule la
    /// fermeture du host pendant un Busy long).</param>
    public async Task<HostShutdownOutcome> ExecuteAsync(
        ShutdownOptions opts,
        Func<IWpsShutdownTarget, Task> onSwitchToTargetForUser,
        IProgress<ShutdownProgress>? progress = null,
        CancellationToken ct = default)
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

        // ====== Phase 3 : queue NEED_USER séquentielle ======
        var needUsers = canCloseResults
            .Where(kv => kv.Value is CanCloseResponse.NeedUserR)
            .Select(kv => (Target: kv.Key, Reason: ((CanCloseResponse.NeedUserR)kv.Value).Reason))
            .ToList();
        foreach (var (target, reason) in needUsers)
        {
            ct.ThrowIfCancellationRequested();
            WpsDebugSender.Log(
                $"Phase 3: '{target.Name}' NeedUser ({reason}) — switching tab + focus",
                LogLevel.Info, LogTag);
            progress?.Report(new ShutdownProgress(target, ShutdownPhase.UserInteraction, reason));

            try { await onSwitchToTargetForUser(target).ConfigureAwait(false); }
            catch (Exception ex)
            {
                WpsDebugSender.Log($"Phase 3: onSwitchToTargetForUser('{target.Name}') threw {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Warning, LogTag);
            }

            // Attendre la décision finale (nouveau cycle CAN_CLOSE pour ce target spécifique)
            var followup = await target.RequestCanCloseAsync(opts, ct).ConfigureAwait(false);
            canCloseResults[target] = followup;

            if (followup is CanCloseResponse.RejectedR rej)
            {
                WpsDebugSender.Log(
                    $"Phase 3: '{target.Name}' REJECTED post-NeedUser ({rej.Reason}) — cascade",
                    LogLevel.Info, LogTag);
                progress?.Report(new ShutdownProgress(target, ShutdownPhase.Aborted, $"Refus : {rej.Reason}"));

                var lockedAfter = canCloseResults
                    .Where(kv => kv.Key != target && kv.Value is CanCloseResponse.OkR)
                    .Select(kv => kv.Key)
                    .ToList();
                await Task.WhenAll(lockedAfter.Select(t => t.SendCanCloseAbortedAsync())).ConfigureAwait(false);
                return HostShutdownOutcome.AbortedByModule;
            }
        }

        // ====== Phase 4 : synchronisation des Busy (attente passage en Ok) ======
        // Les Busy doivent passer en Ok via un autre cycle (le module appelle ResolveCanClose
        // de son côté, ou ses BUSY_PROGRESS finissent par cesser et ResolveCanClose(Ok) arrive).
        // On fait un nouveau RequestCanCloseAsync sur chaque Busy pour récupérer la décision
        // mise à jour. Si toujours Busy après notre BusyHeartbeatTimeoutMs, on laissera
        // CompleteShutdownAsync killer.
        var stillBusy = canCloseResults
            .Where(kv => kv.Value is CanCloseResponse.BusyR)
            .Select(kv => kv.Key)
            .ToList();
        if (stillBusy.Count > 0)
        {
            progress?.Report(new ShutdownProgress(null, ShutdownPhase.WaitingBusy, $"{stillBusy.Count} module(s) en cours…"));
            // Note implementation : attente naïve en re-pollant CAN_CLOSE. Plus sophistiqué
            // serait de s'abonner aux events CanCloseOk de la connexion, mais cela demande
            // d'exposer ces events sur IWpsShutdownTarget. Pour le commit 7 minimal, on poll.
            await Task.WhenAll(stillBusy.Select(async t =>
            {
                try
                {
                    var resp = await t.RequestCanCloseAsync(opts, ct).ConfigureAwait(false);
                    lock (canCloseResults) canCloseResults[t] = resp;
                }
                catch { /* timeout ou cancel : on tombera sur Kill au CompleteShutdown */ }
            })).ConfigureAwait(false);
        }

        // ====== Phase 5 : envoi CLOSE en parallèle + attente CLOSING_DONE ======
        progress?.Report(new ShutdownProgress(null, ShutdownPhase.Closing, "Phase finale : CLOSE parallèle"));
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
public sealed record ShutdownProgress(IWpsShutdownTarget? CurrentTarget, ShutdownPhase Phase, string Message);

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
