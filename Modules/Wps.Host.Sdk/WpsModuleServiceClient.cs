using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Wps.Module.Core;

namespace Wps.Module.Hosting;

/// <summary>
/// Représente la liaison à un ModuleService (app headless qui rend une fonctionnalité au host
/// sans UI principale embed). Symétrique à <see cref="WpsModuleSlot"/> mais sans concerns
/// d'embed UI / parking HWND.
///
/// <para>Pattern d'utilisation :</para>
/// <code>
/// var client = new WpsModuleServiceClient();
/// client.Ready += () => /* prêt à invoquer */;
/// client.ProcessExited += () => /* service mort */;
/// await client.LaunchAsync(@"C:\_wipiSoft\Apps\Modules\Demo5.Echo\Demo5.Echo.exe");
/// var result = await client.InvokeAsync&lt;EchoParams, EchoResult&gt;("Echo", new EchoParams { Name = "Gatoche" });
/// // ... plus tard ...
/// await client.ShutdownAsync();   // ou client.Dispose() en contexte sync
/// </code>
/// </summary>
public sealed class WpsModuleServiceClient : IDisposable, IWpsShutdownTarget
{
    private WpsHostConnection? _connection;
    private Process? _process;
    private string _sessionId = "";
    private bool _ready;
    /// <summary>Idempotence du <see cref="ShutdownAsync"/> : true après le 1er appel,
    /// les suivants sont no-op. Symétrique à <c>WpsModuleSlot._disposed</c>.</summary>
    private bool _disposed;

    // Map requestId → TCS pour corréler INVOKE_RESULT à l'appel InvokeAsync.
    // ConcurrentDictionary car InvokeResultReceived peut fire depuis le ReadLoop pendant
    // qu'un autre thread fait un nouvel InvokeAsync.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(bool ok, string payload)>> _pendingInvokes
        = new();

    /// <summary>Path de l'exécutable du ModuleService (set au moment du Launch).</summary>
    public string? ServicePath { get; private set; }

    /// <summary>Process du service (peut être null avant Launch ou après Kill).</summary>
    public Process? Process => _process;

    /// <summary>Version du contrat annoncée par le service au handshake.</summary>
    public string ServiceContractVersion { get; private set; } = "";

    /// <summary>Nom du service annoncé au handshake.</summary>
    public string ServiceName { get; private set; } = "";

    /// <summary>True quand le service a annoncé READY (prêt à recevoir des Invoke).</summary>
    public bool IsReady => _ready;

    /// <summary>Émis quand le service a envoyé READY (prêt à recevoir des Invoke). Pour un
    /// ModuleService, le hwnd associé est 0 (pas de fenêtre à embed) — on ignore cette valeur.</summary>
    public event Action? Ready;

    /// <summary>Émis sur passage hung↔normal (heartbeat). Identique à <see cref="WpsModuleSlot"/>.</summary>
    public event Action<bool>? HungStateChanged;

    /// <summary>Émis quand le process du service se termine (crash ou shutdown propre).</summary>
    public event Action? ProcessExited;

    /// <summary>Émis si le service annonce une version de contrat incompatible.</summary>
    public event Action<string>? IncompatibleContract;

    /// <summary>Timeout par défaut pour <see cref="InvokeAsync"/> si non précisé.</summary>
    public TimeSpan DefaultInvokeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // ====== v1.3 : surface IWpsShutdownTarget + events de progression ======

    /// <inheritdoc/>
    string IWpsShutdownTarget.Name => string.IsNullOrEmpty(ServiceName) ? "(unknown)" : ServiceName;

    /// <inheritdoc/>
    public bool SupportsNegotiatedShutdown => ParseContractAtLeast(ServiceContractVersion, 1, 3);

    /// <summary>(v1.3) Émis pendant un Busy long quand le service envoie un BUSY_PROGRESS.</summary>
    public event Action<HostBusyProgress>? BusyProgressChanged;

    /// <summary>(v1.3) Émis si le service signale NEED_USER en cours de séquence (rare côté
    /// ModuleService — plutôt typique d'une settings window ouverte ad hoc).</summary>
    public event Action<string>? NeedUserSignaled;

    /// <summary>(v1.3) Émis quand le service se ferme à son initiative (SELF_CLOSING reçu).</summary>
    public event Action<string>? SelfClosing;

    /// <summary>(v1.3) Alias plus sémantique de <see cref="ProcessExited"/> pour les callers
    /// de <see cref="IWpsShutdownTarget"/>.</summary>
    event Action? IWpsShutdownTarget.Disconnected
    {
        add { ProcessExited += value; }
        remove { ProcessExited -= value; }
    }

    private const string LogTag = "Wps.Host.Sdk";

    /// <summary>
    /// Lance le ModuleService : génère un sessionId, démarre la connexion IPC côté host,
    /// lance le process via WMI avec <c>--wps-session</c>, attend le HELLO, valide la contract
    /// version + le Kind=ModuleService, envoie WELCOME, attend READY, démarre le heartbeat.
    /// </summary>
    public async Task LaunchAsync(string exePath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            throw new FileNotFoundException("ModuleService exe not found", exePath);

        // Vérification d'intégrité du déploiement avant tout lancement : manifest présent,
        // master guid OK, tous les fichiers listés présents avec le bon hash. En cas
        // d'échec, on lève WpsDeployInvalidException — le caller (UI pageslot) catche et
        // affiche l'erreur en zone dédiée, sans laisser apparaître un dialogue runtime
        // cryptique pour un fichier manquant suite à un push interrompu, etc.
        var deployDir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
        var appName = Path.GetFileNameWithoutExtension(exePath);
        var verify = WpsDeployVerifier.Verify(deployDir, appName);
        if (!verify.IsValid)
        {
            WpsDebugSender.Log(
                $"LaunchAsync: deploy invalid for '{appName}' → {verify.DisplayMessage}",
                LogLevel.Error, LogTag);
            throw new WpsDeployInvalidException(verify);
        }

        ServicePath = exePath;
        _sessionId = Guid.NewGuid().ToString("N");
        var sidShort = _sessionId[..8];
        WpsDebugSender.Log($"LaunchAsync [{sidShort}]: exe='{Path.GetFileName(exePath)}' (Service)", LogLevel.Info, LogTag);

        _connection = new WpsHostConnection(_sessionId);
        _connection.ModuleHello += OnServiceHello;
        _connection.ModuleReady += _ =>
        {
            // Pour un ModuleService, le hwnd reçu est 0 (pas de fenêtre à embed) — on ignore
            // cette valeur. On reste sur la même machinerie ModuleReady pour ne pas dupliquer.
            _ready = true;
            _connection.StartHeartbeat();
            WpsDebugSender.Log($"LaunchAsync [{sidShort}]: service ready, heartbeat started", LogLevel.Info, LogTag);
            Ready?.Invoke();
        };
        _connection.HungStateChanged += hung => HungStateChanged?.Invoke(hung);
        _connection.InvokeResultReceived += OnInvokeResultReceived;

        // (v1.3) Republie les events de la connexion vers les events publics du client.
        _connection.BusyProgressReceived += (percent, msg) =>
            BusyProgressChanged?.Invoke(new HostBusyProgress(percent, msg));
        _connection.CanCloseNeedUser += reason =>
            NeedUserSignaled?.Invoke(reason);
        _connection.SelfClosing += reason =>
            SelfClosing?.Invoke(reason);

        var startTask = _connection.StartAsync(ct);

        // Lance le process en parallèle de l'attente connexion (WMI ~150ms → Task.Run)
        var workingDir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
        var args = $"{WpsModuleContract.SessionArgFlag} {_sessionId}";
        var wmiStart = DateTime.UtcNow;
        _process = await Task.Run(() => WpsDetachedProcessLauncher.Launch(exePath, args, workingDir), ct);
        var wmiElapsed = (DateTime.UtcNow - wmiStart).TotalMilliseconds;
        WpsDebugSender.Log($"LaunchAsync [{sidShort}]: WMI launch done in {wmiElapsed:F0}ms, pid={_process?.Id ?? -1}", LogLevel.Info, LogTag);

        if (_process is not null)
        {
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                WpsDebugSender.Log($"LaunchAsync [{sidShort}]: process exited (pid={_process.Id})", LogLevel.Trace, LogTag);
                // Échec de tous les Invoke en attente — sinon ils timeoutent inutilement.
                FailAllPendingInvokes("service process exited");
                ProcessExited?.Invoke();
            };
        }

        await startTask.ConfigureAwait(false);
        WpsDebugSender.Log($"LaunchAsync [{sidShort}]: pipes connected, waiting handshake…", LogLevel.Trace, LogTag);
    }

    private async void OnServiceHello(string serviceVersion, string serviceName, WpsModuleKind kind)
    {
        ServiceContractVersion = serviceVersion;
        ServiceName = serviceName;
        var sidShort = _sessionId[..Math.Min(8, _sessionId.Length)];

        // Validation Kind : on attend explicitement ModuleService côté ce client.
        if (kind != WpsModuleKind.ModuleService)
        {
            var msg = $"Service v{serviceVersion} a annoncé Kind={kind} (attendu ModuleService) — refus";
            WpsDebugSender.Log($"OnServiceHello [{sidShort}]: REJECT {msg}", LogLevel.Error, LogTag);
            IncompatibleContract?.Invoke(msg);
            _connection?.Dispose();
            return;
        }

        // Validation semver lax (cf. WpsContractVersion)
        if (!WpsContractVersion.IsCompatible(serviceVersion, WpsModuleContract.CurrentVersion))
        {
            var msg = $"Service v{serviceVersion} incompatible avec host v{WpsModuleContract.CurrentVersion}";
            WpsDebugSender.Log($"OnServiceHello [{sidShort}]: REJECT {msg}", LogLevel.Error, LogTag);
            IncompatibleContract?.Invoke(msg);
            _connection?.Dispose();
            return;
        }

        try
        {
            await _connection!.SendWelcomeAsync(WpsModuleContract.CurrentVersion).ConfigureAwait(false);
            WpsDebugSender.Log($"OnServiceHello [{sidShort}]: contract v{serviceVersion} kind=ModuleService OK, WELCOME sent", LogLevel.Trace, LogTag);
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"OnServiceHello [{sidShort}]: SendWelcome failed ({ex.Message})", LogLevel.Warning, LogTag);
        }
    }

    /// <summary>
    /// Invoque une méthode exposée par le service. Sérialise <paramref name="parameters"/> en JSON,
    /// envoie INVOKE|requestId|method|jsonParams au service, attend INVOKE_RESULT (timeout
    /// configurable) et désérialise le payload retourné.
    /// <para>Lève <see cref="WpsServiceInvokeException"/> si le service retourne ERROR, ou
    /// <see cref="TimeoutException"/> si pas de réponse dans le délai imparti, ou
    /// <see cref="InvalidOperationException"/> si le service n'est pas Ready.</para>
    /// </summary>
    public async Task<TResult> InvokeAsync<TParams, TResult>(string method, TParams parameters,
                                                              TimeSpan? timeout = null,
                                                              CancellationToken ct = default)
        where TParams : class
    {
        if (!_ready) throw new InvalidOperationException("Service not ready (no READY received yet)");
        if (_connection is null) throw new InvalidOperationException("Service connection disposed");

        var requestId = Guid.NewGuid().ToString("N")[..16];
        var jsonParams = JsonSerializer.Serialize(parameters);

        var tcs = new TaskCompletionSource<(bool ok, string payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingInvokes.TryAdd(requestId, tcs))
            throw new InvalidOperationException($"requestId collision: {requestId}");

        try
        {
            await _connection.SendInvokeAsync(requestId, method, jsonParams).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultInvokeTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            linked.Token.Register(() => tcs.TrySetException(new TimeoutException(
                $"Invoke '{method}' (requestId={requestId}) timed out after {(timeout ?? DefaultInvokeTimeout).TotalSeconds}s")));

            var (ok, payload) = await tcs.Task.ConfigureAwait(false);
            if (!ok)
                throw new WpsServiceInvokeException(method, payload);

            var result = JsonSerializer.Deserialize<TResult>(payload);
            if (result is null)
                throw new WpsServiceInvokeException(method, $"deserialized result was null (payload='{payload}')");
            return result;
        }
        finally
        {
            _pendingInvokes.TryRemove(requestId, out _);
        }
    }

    private void OnInvokeResultReceived(string requestId, bool ok, string payload)
    {
        if (_pendingInvokes.TryGetValue(requestId, out var tcs))
            tcs.TrySetResult((ok, payload));
        else
            WpsDebugSender.Log($"INVOKE_RESULT requestId={requestId} sans appelant en attente — ignoré", LogLevel.Warning, LogTag);
    }

    private void FailAllPendingInvokes(string reason)
    {
        foreach (var kv in _pendingInvokes)
            kv.Value.TrySetException(new WpsServiceInvokeException("(any)", reason));
        _pendingInvokes.Clear();
    }

    /// <summary>Demande au service d'afficher sa fenêtre de paramétrage (s'il en a une).</summary>
    public Task ShowSettingsAsync() => _connection?.SendShowSettingsAsync() ?? Task.CompletedTask;

    /// <summary>Demande la fermeture propre via CLOSE direct (API bas-niveau, sans wait ni Kill).
    /// Préférer <see cref="ShutdownAsync(ShutdownOptions, CancellationToken)"/> qui orchestre tout.</summary>
    public Task RequestCloseAsync() => _connection?.SendCloseAsync() ?? Task.CompletedTask;

    // ====== v1.3 : API canonique étendue (cf. WpsModuleSlot pour la doc détaillée) ======

    /// <inheritdoc cref="WpsModuleSlot.RequestCanCloseAsync"/>
    public async Task<CanCloseResponse> RequestCanCloseAsync(ShutdownOptions opts, CancellationToken ct = default)
    {
        if (_disposed || _connection is null) return CanCloseResponse.Ok;
        if (!SupportsNegotiatedShutdown)
            throw new InvalidOperationException(
                $"Service '{ServiceName}' v{ServiceContractVersion} ne supporte pas le shutdown négocié — " +
                $"utiliser ShutdownAsync(opts) qui route automatiquement vers le legacy v1.2.");

        var tcs = new TaskCompletionSource<CanCloseResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action onOk = () => tcs.TrySetResult(CanCloseResponse.Ok);
        Action<int, string> onBusy = (estMs, reason) => tcs.TrySetResult(CanCloseResponse.Busy(estMs, reason));
        Action<string> onNeedUser = reason => tcs.TrySetResult(CanCloseResponse.NeedUser(reason));
        Action<string> onRejected = reason => tcs.TrySetResult(CanCloseResponse.Rejected(reason));

        _connection.CanCloseOk += onOk;
        _connection.CanCloseBusy += onBusy;
        _connection.CanCloseNeedUser += onNeedUser;
        _connection.CanCloseRejected += onRejected;

        try
        {
            await _connection.SendCanCloseAsync(opts.IsUrgent).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(opts.CanCloseTimeoutMs);
            using var _ = timeoutCts.Token.Register(() => tcs.TrySetResult(CanCloseResponse.Timeout));

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _connection.CanCloseOk -= onOk;
            _connection.CanCloseBusy -= onBusy;
            _connection.CanCloseNeedUser -= onNeedUser;
            _connection.CanCloseRejected -= onRejected;
        }
    }

    /// <inheritdoc cref="WpsModuleSlot.SendCanCloseAbortedAsync"/>
    public Task SendCanCloseAbortedAsync()
    {
        if (_disposed || _connection is null) return Task.CompletedTask;
        return _connection.SendCanCloseAbortedAsync();
    }

    /// <inheritdoc cref="WpsModuleSlot.CompleteShutdownAsync"/>
    public async Task<ShutdownResult> CompleteShutdownAsync(ShutdownOptions opts, CancellationToken ct = default)
    {
        if (_disposed) return ShutdownResult.NoOp;
        if (_process is null || _process.HasExited)
        {
            FailAllPendingInvokes("service already exited");
            DisposeUnmanaged();
            return ShutdownResult.AlreadyExited;
        }

        var sidShort = string.IsNullOrEmpty(_sessionId) ? "?" : _sessionId[..Math.Min(8, _sessionId.Length)];

        var closingDoneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action onClosingDone = () => closingDoneTcs.TrySetResult(true);
        if (_connection is not null) _connection.ClosingDone += onClosingDone;

        try
        {
            WpsDebugSender.Log($"CompleteShutdownAsync [{sidShort}]: sending CLOSE (grace={opts.CleanupGracePeriodMs}ms)",
                LogLevel.Info, LogTag);
            await RequestCloseAsync().ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(opts.CleanupGracePeriodMs);

            var processExitTask = _process.WaitForExitAsync(cts.Token);
            var first = await Task.WhenAny(closingDoneTcs.Task, processExitTask).ConfigureAwait(false);

            if (first == closingDoneTcs.Task && !_process.HasExited)
            {
                using var shortCts = new CancellationTokenSource(2000);
                try { await _process.WaitForExitAsync(shortCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            if (_process.HasExited)
            {
                WpsDebugSender.Log($"CompleteShutdownAsync [{sidShort}]: process exited gracefully",
                    LogLevel.Success, LogTag);
                _disposed = true;
                FailAllPendingInvokes("service shut down");
                DisposeUnmanaged();
                return ShutdownResult.Completed;
            }

            if (opts.KillFallback)
            {
                WpsDebugSender.Log($"CompleteShutdownAsync [{sidShort}]: grace expired ({opts.CleanupGracePeriodMs}ms) → Kill(true)",
                    LogLevel.Warning, LogTag);
                try { _process.Kill(true); }
                catch (Exception ex)
                {
                    WpsDebugSender.Log($"CompleteShutdownAsync [{sidShort}]: Kill threw {ex.GetType().Name}: {ex.Message}",
                        LogLevel.Trace, LogTag);
                }
                _disposed = true;
                FailAllPendingInvokes("service killed (timeout)");
                DisposeUnmanaged();
                return ShutdownResult.Killed;
            }

            _disposed = true;
            FailAllPendingInvokes("service shut down (no kill fallback)");
            DisposeUnmanaged();
            return ShutdownResult.Completed;
        }
        finally
        {
            if (_connection is not null) _connection.ClosingDone -= onClosingDone;
        }
    }

    /// <inheritdoc cref="WpsModuleSlot.ShutdownAsync(ShutdownOptions, CancellationToken)"/>
    public async Task<ShutdownResult> ShutdownAsync(ShutdownOptions opts, CancellationToken ct = default)
    {
        if (_disposed) return ShutdownResult.NoOp;
        if (_process is null || _process.HasExited)
        {
            FailAllPendingInvokes("service already exited");
            DisposeUnmanaged();
            _disposed = true;
            return ShutdownResult.AlreadyExited;
        }

        if (!SupportsNegotiatedShutdown)
        {
            // Legacy v1.2
            await ShutdownAsync(opts.CleanupGracePeriodMs).ConfigureAwait(false);
            return _process is null || _process.HasExited ? ShutdownResult.Completed : ShutdownResult.Killed;
        }

        var canCloseResult = await RequestCanCloseAsync(opts, ct).ConfigureAwait(false);
        switch (canCloseResult)
        {
            case CanCloseResponse.RejectedR rejected:
                WpsDebugSender.Log($"ShutdownAsync: service REJECTED ({rejected.Reason}) — annulation",
                    LogLevel.Info, LogTag);
                return ShutdownResult.Aborted;

            case CanCloseResponse.NeedUserR needUser:
                WpsDebugSender.Log($"ShutdownAsync: NeedUser ({needUser.Reason}) reçu hors orchestrateur → Kill",
                    LogLevel.Warning, LogTag);
                if (opts.KillFallback) try { _process.Kill(true); } catch { }
                _disposed = true;
                FailAllPendingInvokes("service killed (need_user out of orchestrator)");
                DisposeUnmanaged();
                return ShutdownResult.Killed;

            case CanCloseResponse.BusyR busy:
                await WaitForBusyToResolveAsync(busy, opts, ct).ConfigureAwait(false);
                return await CompleteShutdownAsync(opts, ct).ConfigureAwait(false);

            case CanCloseResponse.OkR or CanCloseResponse.TimeoutR:
            default:
                return await CompleteShutdownAsync(opts, ct).ConfigureAwait(false);
        }
    }

    private async Task WaitForBusyToResolveAsync(CanCloseResponse.BusyR initial, ShutdownOptions opts, CancellationToken ct)
    {
        if (_connection is null) return;
        var lastSignalUtc = DateTime.UtcNow;
        var resolvedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Action onOk = () => resolvedTcs.TrySetResult(true);
        Action<int, string> onProgress = (_, _) => lastSignalUtc = DateTime.UtcNow;
        _connection.CanCloseOk += onOk;
        _connection.BusyProgressReceived += onProgress;

        try
        {
            while (!resolvedTcs.Task.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                var silence = (DateTime.UtcNow - lastSignalUtc).TotalMilliseconds;
                if (silence > opts.BusyHeartbeatTimeoutMs)
                {
                    WpsDebugSender.Log(
                        $"ShutdownAsync: silence Busy > {opts.BusyHeartbeatTimeoutMs}ms — Kill",
                        LogLevel.Warning, LogTag);
                    return;
                }
                var poll = Task.Delay(500, ct);
                await Task.WhenAny(resolvedTcs.Task, poll).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _connection.CanCloseOk -= onOk;
            _connection.BusyProgressReceived -= onProgress;
        }
    }

    /// <summary>Parse une version "major.minor" et retourne true si elle est >= la version
    /// minimale donnée. Tolérant : si parse échoue, renvoie false (= legacy fallback).</summary>
    private static bool ParseContractAtLeast(string version, int minMajor, int minMinor)
    {
        if (string.IsNullOrEmpty(version)) return false;
        var parts = version.Split('.');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var major)) return false;
        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var minor)) return false;
        return major > minMajor || (major == minMajor && minor >= minMinor);
    }

    /// <summary>
    /// Terminaison canonique du client : envoie CLOSE via IPC, attend l'exit du process
    /// pendant <paramref name="gracePeriodMs"/> puis Kill(true) si grâce dépassée, et libère
    /// toutes les ressources (connexion + process). Idempotent — appelable plusieurs fois
    /// sans risque, devient no-op après le premier appel.
    /// <para>Default grace = <b>7000ms</b> : couvre les cleanups réalistes en production
    /// (flush SMB, ABM_REMOVE d'AppBar, commit MariaDB) avec marge pour les configs un peu
    /// justes en mémoire qui peuvent swapper. L'ancien default de 1500ms shootait MiniBoard
    /// pendant son désenregistrement AppBar — d'où ce bump.</para>
    /// <para>Utiliser <c>gracePeriodMs=0</c> pour "kill immédiat propre" (envoie CLOSE puis
    /// Kill sans attendre — le service essaie son cleanup en best-effort dans la fenêtre du
    /// pipe duplex avant disposition). Réservé aux cas où l'on sait qu'aucun cleanup n'est
    /// attendu.</para>
    /// <para>C'est le SEUL chemin de fermeture exposé : <see cref="Dispose"/> route ici en
    /// version sync avec grace=2000 (compromis cleanup minimal vs blocage thread sync).
    /// Les consumers ne peuvent pas contourner le cleanup gracieux par accident.</para>
    /// </summary>
    public async Task ShutdownAsync(int gracePeriodMs = 7000)
    {
        if (_disposed) return;
        _disposed = true;

        var sidShort = string.IsNullOrEmpty(_sessionId) ? "?" : _sessionId[..Math.Min(8, _sessionId.Length)];

        if (_process is null || _process.HasExited)
        {
            WpsDebugSender.Log($"ShutdownAsync [{sidShort}]: process already exited", LogLevel.Info, LogTag);
            FailAllPendingInvokes("service already exited");
            DisposeUnmanaged();
            return;
        }

        // 1) Demande gracieuse via CLOSE IPC (même si grace=0, on tente — le service peut
        //    faire son cleanup dans la fenêtre où le pipe est encore ouvert).
        try
        {
            WpsDebugSender.Log($"ShutdownAsync [{sidShort}]: sending CLOSE (grace={gracePeriodMs}ms)", LogLevel.Info, LogTag);
            await RequestCloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"ShutdownAsync [{sidShort}]: SendCloseAsync threw {ex.GetType().Name}: {ex.Message} — falling back to Kill", LogLevel.Warning, LogTag);
        }

        // 2) Attendre exit gracieux (sauté si grace=0)
        if (gracePeriodMs > 0)
        {
            try
            {
                using var cts = new CancellationTokenSource(gracePeriodMs);
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                WpsDebugSender.Log($"ShutdownAsync [{sidShort}]: process exited gracefully", LogLevel.Success, LogTag);
            }
            catch (OperationCanceledException)
            {
                WpsDebugSender.Log($"ShutdownAsync [{sidShort}]: grace period expired → Kill(true)", LogLevel.Warning, LogTag);
            }
            catch (Exception ex)
            {
                WpsDebugSender.Log($"ShutdownAsync [{sidShort}]: WaitForExitAsync threw {ex.GetType().Name}: {ex.Message}", LogLevel.Warning, LogTag);
            }
        }

        // 3) Fallback Kill si encore vivant
        if (_process is { HasExited: false })
        {
            try { _process.Kill(true); }
            catch (Exception ex)
            {
                WpsDebugSender.Log($"ShutdownAsync [{sidShort}]: Kill threw {ex.GetType().Name}: {ex.Message}", LogLevel.Trace, LogTag);
            }
        }

        FailAllPendingInvokes("service shut down");
        DisposeUnmanaged();
    }

    /// <summary>
    /// Version synchrone de <see cref="ShutdownAsync"/> avec grace=2000ms (envoie CLOSE,
    /// laisse 2s au service pour son cleanup, puis Kill si nécessaire). À utiliser
    /// uniquement quand on ne peut pas await (ex : <c>using</c> statement, finalizer-like
    /// context). Préférer <c>await ShutdownAsync()</c> partout où possible — le default
    /// 7000ms donne plus d'air pour un cleanup propre.
    /// <para>Pourquoi 2000 et pas 0 : <c>Dispose</c> est souvent appelé depuis un contexte
    /// sync où bloquer 2s n'est pas dramatique, mais où shooter le cleanup applicatif l'est.
    /// 2s couvre les cleanups courts (close handles, flush log) sans bloquer trop longtemps
    /// le thread appelant. Pour des cleanups longs : <c>await ShutdownAsync()</c> avec
    /// default 7000.</para>
    /// <para>NB : malgré le nom <see cref="IDisposable.Dispose"/>, ce chemin envoie CLOSE
    /// via IPC en premier — le service a sa fenêtre de cleanup avant Kill éventuel. Le SDK
    /// ne dispose JAMAIS d'un process sans avoir au moins notifié le service.</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        // Routage vers ShutdownAsync(2000) en sync. .GetAwaiter().GetResult() est OK ici car
        // 2s reste un délai borné, raisonnable pour un Dispose synchrone.
        try { ShutdownAsync(2000).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"Dispose: ShutdownAsync(2000) threw {ex.GetType().Name}: {ex.Message}", LogLevel.Warning, LogTag);
            // Filet ultime : si ShutdownAsync a planté avant le DisposeUnmanaged, on libère ici.
            DisposeUnmanaged();
        }
    }

    private void DisposeUnmanaged()
    {
        try { _connection?.Dispose(); } catch { }
        _connection = null;
        try { _process?.Dispose(); } catch { }
    }
}

/// <summary>
/// Exception levée par <see cref="WpsModuleServiceClient.InvokeAsync"/> quand le service
/// retourne un statut ERROR ou que l'invocation échoue (timeout, désérialisation, etc.).
/// </summary>
public sealed class WpsServiceInvokeException : Exception
{
    public string Method { get; }

    public WpsServiceInvokeException(string method, string message)
        : base($"Invoke '{method}' failed: {message}")
    {
        Method = method;
    }
}
