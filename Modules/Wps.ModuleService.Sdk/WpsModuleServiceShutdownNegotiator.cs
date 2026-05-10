using System.Windows;
using System.Windows.Threading;
using Wps.Module;
using Wps.Module.Core;

namespace Wps.ModuleService;

/// <summary>
/// Machine à états pour le shutdown négocié v1.3 côté ModuleService. Symétrique à
/// <c>Wps.Module.WpsModuleShutdownNegotiator</c> avec une différence clé : le marshalling du
/// hook applicatif <see cref="IWpsModule.OnCanCloseRequestedAsync"/> est conditionnel.
///
/// <para>Si <see cref="Application.Current"/> existe (cas d'un ModuleService WPF avec settings
/// window), on marshalle sur le UI thread Dispatcher comme le Module classique. Si pas
/// d'Application WPF (cas console pure), on appelle le hook directement sur le ThreadPool —
/// l'app peut faire son cleanup sans contrainte de thread.</para>
///
/// <para>Logique de transitions, clamp urgent, coalescing : identique au Module classique.
/// Cf. <see cref="Wps.Module.WpsModuleShutdownNegotiator"/> pour la doc détaillée des états.</para>
/// </summary>
internal sealed class WpsModuleServiceShutdownNegotiator
{
    /// <summary>États du cycle de fermeture côté ModuleService. Identique au Module classique.</summary>
    internal enum State { Idle, Checking, Busy, NeedUser, Locked, Closing }

    /// <summary>Implémenteur du hook applicatif. Mutable et propagé par
    /// <see cref="WpsModuleService.Register"/> — sans ça l'app DOIT appeler Register AVANT
    /// BootstrapAsync, sinon le négociateur capturait null à sa construction et retournait
    /// Ok par défaut à tous les CAN_CLOSE (le host fermait tout instantanément, sans laisser
    /// l'app répondre Busy/NeedUser/Rejected). Le setter rend l'ordre Register/Bootstrap
    /// indifférent côté app.</summary>
    internal IWpsModule? Module { get; set; }

    private readonly WpsModuleServiceConnection _connection;

    private readonly object _lock = new();
    private State _state = State.Idle;
    private CanCloseContext? _currentCtx;

    private const string LogTag = "Wps.ModuleService.Sdk";

    public State CurrentState
    {
        get { lock (_lock) return _state; }
    }

    public WpsModuleServiceShutdownNegotiator(IWpsModule? module, WpsModuleServiceConnection connection)
    {
        Module = module;
        _connection = connection;
    }

    /// <summary>Entrée principale : CAN_CLOSE reçu. Voir doc équivalente côté Module classique.</summary>
    public async Task<bool> OnCanCloseReceivedAsync(CanCloseContext ctx)
    {
        // Coalescing si cycle déjà en cours (typiquement double signal pipe ↔ SessionEnding)
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

        // Marshalling conditionnel du hook applicatif. ModuleService peut être :
        //   - Pure console : pas d'Application.Current → exécution direct sur ThreadPool
        //   - WPF (settings window) : Application.Current existe → marshall sur UI Dispatcher
        //     pour garder la cohérence avec le reste du SDK qui marshalle déjà SHOW_SETTINGS
        //     (l'app peut accéder à ses contrôles WPF sans cross-thread exception).
        CanCloseDecision result;
        try
        {
            result = await CallHookOnAppropriateContextAsync(ctx).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log(
                $"OnCanCloseRequestedAsync threw {ex.GetType().Name}: {ex.Message} — fallback Ok",
                LogLevel.Warning, LogTag);
            result = CanCloseDecision.Ok;
        }

        // Clamp urgent identique au Module classique
        if (ctx.IsUrgent && (result is CanCloseDecision.NeedUserD or CanCloseDecision.RejectedD))
        {
            WpsDebugSender.Log(
                $"Clamping {result.GetType().Name} → Busy(2000) (IsUrgent=true)",
                LogLevel.Info, LogTag);
            result = CanCloseDecision.Busy("urgent-clamped", 2000);
        }

        await ApplyDecisionAsync(result).ConfigureAwait(false);
        return true;
    }

    /// <summary>Résolution asynchrone (Busy / NeedUser → décision finale). Voir doc Module.</summary>
    public async Task ResolveAsync(CanCloseDecision decision)
    {
        lock (_lock)
        {
            if (_state is not (State.Busy or State.NeedUser))
            {
                WpsDebugSender.Log(
                    $"ResolveAsync ignoré : état={_state} (attendu Busy ou NeedUser)",
                    LogLevel.Warning, LogTag);
                return;
            }
        }

        var ctx = _currentCtx;
        if (ctx is not null && ctx.IsUrgent &&
            (decision is CanCloseDecision.NeedUserD or CanCloseDecision.RejectedD))
        {
            decision = CanCloseDecision.Busy("urgent-clamped", 2000);
        }

        await ApplyDecisionAsync(decision).ConfigureAwait(false);
    }

    /// <summary>(v1.3 final) CAN_CLOSE_COMMITTED reçu — signal que la fermeture est validée
    /// globalement. Déclenche la DIM <see cref="IWpsModule.OnCanCloseCommittedAsync"/> où
    /// l'app démarre son travail Busy réel. Marshall conditionnel UI thread (cohérent avec
    /// les autres hooks ModuleService — WPF ou console pure).</summary>
    public async Task OnCanCloseCommittedReceived()
    {
        var module = Module;
        if (module is null) return;
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                await module.OnCanCloseCommittedAsync().ConfigureAwait(false);
            }
            else
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = dispatcher.BeginInvoke(new Action(async () =>
                {
                    try { await module.OnCanCloseCommittedAsync().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        WpsDebugSender.Log(
                            $"OnCanCloseCommittedAsync threw {ex.GetType().Name}: {ex.Message}",
                            LogLevel.Warning, LogTag);
                    }
                    finally { tcs.TrySetResult(true); }
                }));
                await tcs.Task.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log(
                $"OnCanCloseCommittedReceived marshalling threw {ex.GetType().Name}: {ex.Message}",
                LogLevel.Warning, LogTag);
        }
    }

    /// <summary>(v1.3 final) USER_RESPONSE reçu après que la modale host a été tranchée par
    /// l'utilisateur. Pipeline : appel DIM <see cref="IWpsModule.OnUserResponseAsync"/>
    /// (retour <c>null</c> = fallback standard), sinon mapping standard yes/ok → Ok,
    /// no/cancel → Rejected.</summary>
    public async Task OnUserResponseReceived(string buttonId)
    {
        lock (_lock)
        {
            if (_state != State.NeedUser)
            {
                WpsDebugSender.Log(
                    $"OnUserResponseReceived ignoré : état={_state} (attendu NeedUser, peut-être déjà résolu par l'app)",
                    LogLevel.Trace, LogTag);
                return;
            }
        }

        // 1. Tentative de mapping applicatif via DIM. Null = fallback standard.
        CanCloseDecision? appDecision = null;
        var module = Module;
        if (module is not null)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.CheckAccess())
                {
                    appDecision = await module.OnUserResponseAsync(buttonId).ConfigureAwait(false);
                }
                else
                {
                    var tcs = new TaskCompletionSource<CanCloseDecision?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _ = dispatcher.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            var d = await module.OnUserResponseAsync(buttonId).ConfigureAwait(false);
                            tcs.TrySetResult(d);
                        }
                        catch (Exception ex)
                        {
                            WpsDebugSender.Log(
                                $"OnUserResponseAsync('{buttonId}') threw {ex.GetType().Name}: {ex.Message} — fallback standard mapping",
                                LogLevel.Warning, LogTag);
                            tcs.TrySetResult(null);
                        }
                    }));
                    appDecision = await tcs.Task.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                WpsDebugSender.Log(
                    $"OnUserResponseAsync marshalling threw {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Warning, LogTag);
            }
        }

        // 2. Fallback mapping standard si l'app n'a pas override.
        var decision = appDecision ?? StandardMapping(buttonId);
        WpsDebugSender.Log(
            $"USER_RESPONSE reçu ('{buttonId}') → {decision.GetType().Name} ({(appDecision is not null ? "app override" : "standard mapping")})",
            LogLevel.Info, LogTag);
        await ApplyDecisionAsync(decision).ConfigureAwait(false);
    }

    /// <summary>Mapping standard pour les ids réservés. Les autres tombent sur Ok défensif —
    /// l'app aurait dû override via <see cref="IWpsModule.OnUserResponseAsync"/>.</summary>
    private static CanCloseDecision StandardMapping(string buttonId) => buttonId switch
    {
        "yes" or "ok" => CanCloseDecision.Ok,
        "no" => CanCloseDecision.Rejected("user-no"),
        "cancel" => CanCloseDecision.Rejected("user-cancel"),
        _ => CanCloseDecision.Ok,
    };

    /// <summary>CAN_CLOSE_ABORTED reçu : libère le verrou Locked → Idle, notifie l'app.</summary>
    public void OnCanCloseAborted()
    {
        lock (_lock)
        {
            if (_state != State.Locked) return;
            _state = State.Idle;
            _currentCtx = null;
        }

        // Marshalling identique au hook principal
        var dispatcher = Application.Current?.Dispatcher;
        Action invoke = () =>
        {
            try { Module?.OnCanCloseAborted(); }
            catch (Exception ex)
            {
                WpsDebugSender.Log(
                    $"OnCanCloseAborted handler threw {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Warning, LogTag);
            }
        };
        if (dispatcher is not null && !dispatcher.CheckAccess())
            _ = dispatcher.BeginInvoke(invoke);
        else
            invoke();
    }

    /// <summary>CLOSE reçu : phase finale, OnShutdownRequested + CLOSING_DONE.</summary>
    public async Task OnCloseReceivedAsync()
    {
        lock (_lock)
        {
            if (_state == State.Closing) return;
            _state = State.Closing;
        }

        // Hook applicatif sur le bon contexte
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                try { Module?.OnShutdownRequested(); }
                catch (Exception ex)
                {
                    WpsDebugSender.Log(
                        $"OnShutdownRequested threw {ex.GetType().Name}: {ex.Message}",
                        LogLevel.Warning, LogTag);
                }
                tcs.TrySetResult(true);
            }));
            await tcs.Task.ConfigureAwait(false);
        }
        else
        {
            try { Module?.OnShutdownRequested(); }
            catch (Exception ex)
            {
                WpsDebugSender.Log(
                    $"OnShutdownRequested threw {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Warning, LogTag);
            }
        }

        // Notifier le host que le cleanup est fini
        try
        {
            await _connection.SendClosingDoneAsync().ConfigureAwait(false);
            WpsDebugSender.Log("CLOSING_DONE sent — service ready to exit", LogLevel.Success, LogTag);
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log(
                $"SendClosingDoneAsync threw {ex.GetType().Name}: {ex.Message}",
                LogLevel.Trace, LogTag);
        }
    }

    private async Task<CanCloseDecision> CallHookOnAppropriateContextAsync(CanCloseContext ctx)
    {
        // Lecture locale stable du provider mutable (cf. champ Module). Si l'app a fait
        // Register après Bootstrap, la valeur est désormais celle qu'elle a passée ; sinon
        // c'est null et on retourne Ok par défaut (comportement documenté).
        var module = Module;
        if (module is null) return CanCloseDecision.Ok;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            // Pas d'UI thread WPF (console pure) ou déjà sur le bon thread : appel direct
            return await module.OnCanCloseRequestedAsync(ctx).ConfigureAwait(false);
        }

        // Marshalling sur UI thread WPF via TCS (cohérent avec le ModuleSDK classique)
        var tcs = new TaskCompletionSource<CanCloseDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                var d = await module.OnCanCloseRequestedAsync(ctx).ConfigureAwait(false);
                tcs.TrySetResult(d);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));
        return await tcs.Task.ConfigureAwait(false);
    }

    private Task SendCurrentStateResponseAsync()
    {
        State stateNow;
        lock (_lock) stateNow = _state;
        return stateNow switch
        {
            State.Locked or State.Closing => _connection.SendCanCloseOkAsync(),
            State.Busy or State.Checking or State.NeedUser =>
                _connection.SendCanCloseBusyAsync(-1, "already-in-progress"),
            _ => Task.CompletedTask,
        };
    }

    private async Task ApplyDecisionAsync(CanCloseDecision decision)
    {
        switch (decision)
        {
            case CanCloseDecision.OkD:
                lock (_lock) _state = State.Locked;
                await _connection.SendCanCloseOkAsync().ConfigureAwait(false);
                WpsDebugSender.Log("CAN_CLOSE_OK sent — service locked, awaiting CLOSE", LogLevel.Trace, LogTag);
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
                var payload = new NeedUserPayload
                {
                    Reason = needUser.Reason,
                    Ask = needUser.Question,
                    Answers = new Dictionary<string, string>(needUser.Answers),
                    AllowClose = needUser.AllowClose,
                };
                await _connection.SendCanCloseNeedUserAsync(payload).ConfigureAwait(false);
                WpsDebugSender.Log(
                    $"CAN_CLOSE_NEED_USER sent (reason='{needUser.Reason}', answers=[{string.Join(",", needUser.Answers.Keys)}], allowClose={needUser.AllowClose}) — host will display the modal",
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
