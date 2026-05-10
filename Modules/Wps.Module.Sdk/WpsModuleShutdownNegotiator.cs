using System.Globalization;
using System.Windows.Threading;
using Wps.Module.Core;

namespace Wps.Module;

/// <summary>
/// Machine à états interne pour le shutdown négocié v1.3 côté module. Encapsule la logique
/// de transition entre les états (Idle → Checking → {Ok|Busy|NeedUser|Rejected} → Locked
/// → Closing) et l'appel au hook applicatif <see cref="IWpsModule.OnCanCloseRequestedAsync"/>
/// avec marshalling sur le UI thread + clamping urgent.
///
/// <para>Le négociateur est piloté par les évènements du <see cref="WpsModuleConnection"/>
/// (CAN_CLOSE, CAN_CLOSE_ABORTED, CLOSE reçus du host) et par les API publiques de
/// <see cref="WpsModule"/> (<c>ResolveCanClose</c> appelé par l'app après un Busy long ou
/// un dialog NeedUser tranché).</para>
///
/// <para>Coalescing : si un cycle de fermeture est déjà en cours (état != Idle) et qu'un
/// nouveau CAN_CLOSE arrive (typiquement double signal pipe ↔ SessionEnding Windows),
/// le négociateur répond immédiatement avec l'état courant sans relancer le hook applicatif.
/// Évite les doubles cleanups et les races.</para>
/// </summary>
internal sealed class WpsModuleShutdownNegotiator
{
    /// <summary>États du cycle de fermeture côté module.</summary>
    internal enum State
    {
        /// <summary>Aucun cycle de fermeture en cours.</summary>
        Idle,

        /// <summary>CAN_CLOSE reçu, hook applicatif en cours d'évaluation
        /// (appel à <see cref="IWpsModule.OnCanCloseRequestedAsync"/>).</summary>
        Checking,

        /// <summary>Réponse Busy envoyée au host. Le module attend que l'app appelle
        /// <see cref="WpsModule.ResolveCanClose"/> pour passer à Ok ou poursuivre Busy.</summary>
        Busy,

        /// <summary>Réponse NeedUser envoyée. Le host bascule l'onglet, l'app gère son dialog,
        /// puis appelle <see cref="WpsModule.ResolveCanClose"/> avec la décision finale.</summary>
        NeedUser,

        /// <summary>Réponse Ok envoyée. Le module est engagé à fermer ; il attend le CLOSE
        /// du host (ou un CAN_CLOSE_ABORTED si la cascade est annulée).</summary>
        Locked,

        /// <summary>CLOSE reçu : cleanup applicatif en cours. Après <see cref="IWpsModule.OnShutdownRequested"/>,
        /// le négociateur envoie CLOSING_DONE puis le process exit.</summary>
        Closing,
    }

    private readonly Dispatcher _uiDispatcher;

    /// <summary>Implémenteur du hook applicatif. Mutable et propagé par
    /// <see cref="WpsModule.Register"/> — sans ça l'app DOIT appeler Register AVANT
    /// <see cref="WpsModule.Bootstrap"/>, sinon le négociateur capturait null à sa construction
    /// et retournait Ok par défaut à tous les CAN_CLOSE (le host fermait tout instantanément
    /// sans laisser l'app répondre Busy/NeedUser/Rejected). Le setter rend l'ordre
    /// Register/Bootstrap indifférent côté app.</summary>
    internal IWpsModule? Module { get; set; }

    private readonly WpsModuleConnection _connection;

    private readonly object _lock = new();
    private State _state = State.Idle;
    private CanCloseContext? _currentCtx;

    private const string LogTag = "Wps.Module.Sdk";

    /// <summary>État courant — exposé en lecture pour diagnostic et coalescing externe
    /// (ex: SessionEnding handler vérifie si un cycle est déjà en cours).</summary>
    public State CurrentState
    {
        get { lock (_lock) return _state; }
    }

    public WpsModuleShutdownNegotiator(Dispatcher uiDispatcher, IWpsModule? module, WpsModuleConnection connection)
    {
        _uiDispatcher = uiDispatcher;
        Module = module;
        _connection = connection;
    }

    /// <summary>
    /// Entrée principale : un CAN_CLOSE a été reçu (du host via pipe, ou auto-déclenché
    /// localement par le SessionEnding Windows). Appelle le hook applicatif sur le UI thread,
    /// applique le clamp urgent si <paramref name="ctx"/>.IsUrgent, envoie la réponse au host
    /// et transitionne l'état interne.
    /// </summary>
    /// <returns>True si le CAN_CLOSE a été pris en charge ; false s'il a été coalescé (un
    /// cycle est déjà en cours, l'état courant a été retourné au host sans relancer le hook).</returns>
    public async Task<bool> OnCanCloseReceivedAsync(CanCloseContext ctx)
    {
        // Coalescing : si on est déjà en cycle, on ne ré-évalue pas — on renvoie l'état courant
        // au host. Cas typique : SessionEnding local a déjà déclenché la séquence, et le
        // CAN_CLOSE pipe arrive juste après.
        lock (_lock)
        {
            if (_state != State.Idle)
            {
                WpsDebugSender.Log(
                    $"OnCanCloseReceivedAsync: cycle déjà en cours (state={_state}) — coalesce",
                    LogLevel.Trace, LogTag);
                _ = SendCurrentStateResponseAsync();
                return false;
            }
            _state = State.Checking;
            _currentCtx = ctx;
        }

        // Appel du hook applicatif sur le UI thread. Pattern TaskCompletionSource pour
        // faire transiter une ValueTask<CanCloseDecision> à travers le Dispatcher.
        // (`_ =` : on ignore explicitement le DispatcherOperation retourné — la synchronisation
        // se fait via le TCS, pas via le résultat de BeginInvoke.)
        // Lecture locale stable du provider mutable (cf. champ Module). Si l'app a fait
        // Register après Bootstrap, la valeur est désormais celle qu'elle a passée ; sinon
        // c'est null et on retourne Ok par défaut (comportement documenté).
        var module = Module;
        var tcs = new TaskCompletionSource<CanCloseDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _uiDispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                var decision = module is not null
                    ? await module.OnCanCloseRequestedAsync(ctx).ConfigureAwait(false)
                    : CanCloseDecision.Ok;
                tcs.TrySetResult(decision);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));

        CanCloseDecision result;
        try
        {
            result = await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log(
                $"OnCanCloseRequestedAsync threw {ex.GetType().Name}: {ex.Message} — fallback Ok",
                LogLevel.Warning, LogTag);
            result = CanCloseDecision.Ok;
        }

        // Clamp urgent : NeedUser/Rejected → Busy(2000) si IsUrgent. L'app n'a pas besoin de
        // gérer ce cas elle-même.
        if (ctx.IsUrgent && (result is CanCloseDecision.NeedUserD or CanCloseDecision.RejectedD))
        {
            WpsDebugSender.Log(
                $"Clamping {result.GetType().Name} → Busy(2000) (IsUrgent=true, shutdown OS en cours)",
                LogLevel.Info, LogTag);
            result = CanCloseDecision.Busy("urgent-clamped", 2000);
        }

        await ApplyDecisionAsync(result).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Appelé par <see cref="WpsModule.ResolveCanClose"/> quand l'app a une nouvelle décision
    /// à transmettre (typiquement après un Busy long résolu en Ok, ou un NeedUser tranché par
    /// l'utilisateur en Ok ou Rejected).
    /// </summary>
    public async Task ResolveAsync(CanCloseDecision decision)
    {
        lock (_lock)
        {
            if (_state is not (State.Busy or State.NeedUser))
            {
                WpsDebugSender.Log(
                    $"ResolveAsync ignoré : état courant={_state} (attendu Busy ou NeedUser)",
                    LogLevel.Warning, LogTag);
                return;
            }
        }

        // Re-clamp urgent au cas où l'app reviendrait avec un NeedUser/Rejected post-clamp.
        var ctx = _currentCtx;
        if (ctx is not null && ctx.IsUrgent &&
            (decision is CanCloseDecision.NeedUserD or CanCloseDecision.RejectedD))
        {
            decision = CanCloseDecision.Busy("urgent-clamped", 2000);
        }

        await ApplyDecisionAsync(decision).ConfigureAwait(false);
    }

    /// <summary>
    /// Appelé quand le host envoie CAN_CLOSE_ABORTED (cascade annulée par un autre module qui
    /// a renvoyé Rejected). Libère le verrou interne (état Locked → Idle) et notifie le hook
    /// applicatif <see cref="IWpsModule.OnCanCloseAborted"/>. Idempotent si l'état n'est pas
    /// Locked.
    /// </summary>
    public void OnCanCloseAborted()
    {
        lock (_lock)
        {
            if (_state != State.Locked)
            {
                WpsDebugSender.Log(
                    $"OnCanCloseAborted ignoré : état courant={_state} (attendu Locked)",
                    LogLevel.Trace, LogTag);
                return;
            }
            _state = State.Idle;
            _currentCtx = null;
        }
        _uiDispatcher.BeginInvoke(new Action(() =>
        {
            try { Module?.OnCanCloseAborted(); }
            catch (Exception ex)
            {
                WpsDebugSender.Log(
                    $"OnCanCloseAborted handler threw {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Warning, LogTag);
            }
        }));
    }

    /// <summary>
    /// Appelé quand le host envoie CLOSE — phase finale de la fermeture. Transitionne en
    /// Closing, appelle <see cref="IWpsModule.OnShutdownRequested"/> sur le UI thread, puis
    /// envoie CLOSING_DONE au host.
    ///
    /// <para>Sémantique v1.3 : CLOSE n'arrive normalement qu'après un cycle CAN_CLOSE → Ok.
    /// État attendu = Locked. Mais en mode legacy v1.2 (host n'envoie pas de CAN_CLOSE), on
    /// peut recevoir CLOSE directement depuis Idle — on accepte ce chemin pour préserver la
    /// rétrocompat.</para>
    /// </summary>
    public async Task OnCloseReceivedAsync()
    {
        lock (_lock)
        {
            if (_state == State.Closing)
            {
                WpsDebugSender.Log("OnCloseReceivedAsync: déjà en Closing — ignoré", LogLevel.Trace, LogTag);
                return;
            }
            _state = State.Closing;
        }

        // Hook applicatif (cleanup) sur le UI thread.
        // (`_ =` : ignore explicite du DispatcherOperation, synchronisation via TCS.)
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _uiDispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var module = Module;
                if (module is not null) module.OnShutdownRequested();
                else System.Windows.Application.Current?.Shutdown();
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                WpsDebugSender.Log(
                    $"OnShutdownRequested handler threw {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Warning, LogTag);
                tcs.TrySetResult(true);  // on continue quand même vers CLOSING_DONE
            }
        }));

        await tcs.Task.ConfigureAwait(false);

        // Notifier le host que le cleanup est fini (avant que le process exit). En cas de pipe
        // déjà coupé, SendAsync est silencieux (le host saura via Process.Exited).
        try
        {
            await _connection.SendClosingDoneAsync().ConfigureAwait(false);
            WpsDebugSender.Log("CLOSING_DONE sent — module ready to exit", LogLevel.Success, LogTag);
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log(
                $"SendClosingDoneAsync threw {ex.GetType().Name}: {ex.Message}",
                LogLevel.Trace, LogTag);
        }
    }

    /// <summary>
    /// Envoie au host une réponse correspondant à l'état courant. Utilisé par le coalescing
    /// quand un nouveau CAN_CLOSE arrive alors qu'un cycle est déjà en cours.
    /// </summary>
    private Task SendCurrentStateResponseAsync()
    {
        State stateNow;
        lock (_lock) stateNow = _state;
        return stateNow switch
        {
            State.Locked or State.Closing => _connection.SendCanCloseOkAsync(),
            State.Busy or State.Checking or State.NeedUser =>
                _connection.SendCanCloseBusyAsync(-1, "already-in-progress"),
            _ => Task.CompletedTask,  // Idle ne devrait pas arriver ici (coalesce checké en amont)
        };
    }

    /// <summary>
    /// Applique une décision : envoie le message correspondant au host, transitionne l'état.
    /// Centralise la logique d'envoi pour ne pas la dupliquer entre OnCanCloseReceivedAsync
    /// et ResolveAsync.
    /// </summary>
    private async Task ApplyDecisionAsync(CanCloseDecision decision)
    {
        switch (decision)
        {
            case CanCloseDecision.OkD:
                lock (_lock) _state = State.Locked;
                await _connection.SendCanCloseOkAsync().ConfigureAwait(false);
                WpsDebugSender.Log("CAN_CLOSE_OK sent — module locked, awaiting CLOSE", LogLevel.Trace, LogTag);
                break;

            case CanCloseDecision.BusyD busy:
                lock (_lock) _state = State.Busy;
                await _connection.SendCanCloseBusyAsync(busy.EstimatedMs, busy.Reason).ConfigureAwait(false);
                WpsDebugSender.Log(
                    $"CAN_CLOSE_BUSY sent (estMs={busy.EstimatedMs}, reason='{busy.Reason}')",
                    LogLevel.Trace, LogTag);
                break;

            case CanCloseDecision.NeedUserD needUser:
                lock (_lock) _state = State.NeedUser;
                await _connection.SendCanCloseNeedUserAsync(needUser.Reason).ConfigureAwait(false);
                WpsDebugSender.Log(
                    $"CAN_CLOSE_NEED_USER sent (reason='{needUser.Reason}')",
                    LogLevel.Trace, LogTag);
                break;

            case CanCloseDecision.RejectedD rejected:
                lock (_lock) { _state = State.Idle; _currentCtx = null; }
                await _connection.SendCanCloseRejectedAsync(rejected.Reason).ConfigureAwait(false);
                WpsDebugSender.Log(
                    $"CAN_CLOSE_REJECTED sent (reason='{rejected.Reason}') — cycle ended",
                    LogLevel.Info, LogTag);
                break;
        }
    }
}
