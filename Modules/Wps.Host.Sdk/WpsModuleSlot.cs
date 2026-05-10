using System.Diagnostics;
using System.IO;
using Wps.Module.Core;

namespace Wps.Module.Hosting;

/// <summary>
/// Représente un slot d'embedding pour un module wipiSoft. Encapsule tout le cycle de vie :
/// lancement détaché du process, handshake IPC (HELLO/WELCOME avec validation contract version),
/// récupération du HWND du module, surveillance heartbeat, fermeture (négociée v1.3 ou directe v1.2).
///
/// L'embedding visuel (SetParent + positionnement du HWND dans le slot) est délégué au host
/// — ce SDK fournit le hwnd via <see cref="ModuleHwnd"/>, le host l'embarque où il veut.
///
/// Pattern d'utilisation :
/// <code>
/// var slot = new WpsModuleSlot();
/// slot.HungStateChanged += hung => /* UI orange */;
/// slot.ProcessExited += () => /* UI rouge */;
/// slot.Ready += () => /* embed slot.ModuleHwnd ici */;
/// await slot.LaunchAsync(@"C:\path\to\module.exe");
/// // ... plus tard ...
/// await slot.ShutdownAsync();   // ou slot.Dispose() en contexte sync
/// </code>
///
/// <para><b>v1.3 :</b> implémente <see cref="IWpsShutdownTarget"/> pour permettre à l'orchestrateur
/// (<see cref="ShutdownOrchestrator"/>) de piloter la fermeture en plusieurs phases. Les call sites
/// simples (toggle daemon, Stop pageslot) utilisent <see cref="ShutdownAsync(ShutdownOptions, CancellationToken)"/>
/// qui fait toute la séquence. L'API legacy <see cref="ShutdownAsync(int)"/> (avec un int gracePeriodMs)
/// reste comme overload pour les callers existants.</para>
/// </summary>
public sealed class WpsModuleSlot : IDisposable, IWpsShutdownTarget
{
    private WpsHostConnection? _connection;
    private Process? _process;
    private string _sessionId = "";
    private bool _disposed;

    /// <summary>Path de l'exécutable du module (set au moment du Launch).</summary>
    public string? ModulePath { get; private set; }

    /// <summary>HWND de la fenêtre principale du module, valide après l'event <see cref="Ready"/>.</summary>
    public IntPtr ModuleHwnd { get; private set; }

    /// <summary>Process du module (peut être null avant Launch ou après Kill).</summary>
    public Process? Process => _process;

    /// <summary>Version du contrat annoncée par le module au handshake (vide tant que pas connecté).</summary>
    public string ModuleContractVersion { get; private set; } = "";

    /// <summary>Nom du module annoncé au handshake (vide tant que pas connecté).</summary>
    public string ModuleName { get; private set; } = "";

    /// <summary>Type du peer (Module ou ModuleService) annoncé dans le HELLO. Pour un peer en
    /// v1.0 du contrat (sans le champ kind), <see cref="WpsModuleKind.Module"/> est supposé.</summary>
    public WpsModuleKind ModuleKind { get; private set; } = WpsModuleKind.Module;

    /// <summary>Émis quand le module est prêt à être embarqué (handshake terminé + READY|hwnd reçu).</summary>
    public event Action? Ready;

    /// <summary>Émis sur passage hung↔normal (heartbeat). <c>true</c> = figé (UI thread bloqué).</summary>
    public event Action<bool>? HungStateChanged;

    /// <summary>Émis quand le process du module se termine (crash ou shutdown propre).</summary>
    public event Action? ProcessExited;

    /// <summary>Émis si le module annonce une version de contrat incompatible.</summary>
    public event Action<string>? IncompatibleContract;

    // ====== v1.3 : surface IWpsShutdownTarget + events de progression ======

    /// <inheritdoc/>
    string IWpsShutdownTarget.Name => string.IsNullOrEmpty(ModuleName) ? "(unknown)" : ModuleName;

    /// <inheritdoc/>
    public bool SupportsNegotiatedShutdown => ParseContractAtLeast(ModuleContractVersion, 1, 3);

    /// <summary>(v1.3) Émis pendant un Busy long quand le module envoie un BUSY_PROGRESS.</summary>
    public event Action<HostBusyProgress>? BusyProgressChanged;

    /// <summary>(v1.3) Émis si le module signale NEED_USER en cours de séquence (hors phase 1).
    /// Utile au pattern Busy → NeedUser : RequestCanCloseAsync n'aura renvoyé que Busy en phase 1,
    /// le passage en NeedUser arrive ultérieurement via cet event.</summary>
    public event Action<string>? NeedUserSignaled;

    /// <summary>(v1.3) Émis quand le module se ferme à son initiative (SELF_CLOSING reçu).
    /// Permet au host de griser le slot proprement (état "Closed" plutôt que "Failed").</summary>
    public event Action<string>? SelfClosing;

    /// <summary>(v1.3) Émis si le process meurt pendant la séquence (alias plus sémantique de
    /// <see cref="ProcessExited"/> pour les callers de <see cref="IWpsShutdownTarget"/>).</summary>
    event Action? IWpsShutdownTarget.Disconnected
    {
        add { ProcessExited += value; }
        remove { ProcessExited -= value; }
    }

    /// <summary>
    /// Lance le module : génère un sessionId, démarre la connexion IPC côté host (server NOTIF +
    /// client CMD), lance le process via WMI avec <c>--wps-session</c>, attend le HELLO,
    /// valide la contract version, envoie WELCOME, attend READY|hwnd, démarre le heartbeat.
    /// </summary>
    private const string LogTag = "Wps.Host.Sdk";

    public async Task LaunchAsync(string exePath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            throw new FileNotFoundException("Module exe not found", exePath);

        // Vérification d'intégrité du déploiement avant tout lancement
        // (cf. WpsModuleServiceClient.LaunchAsync pour la motivation détaillée).
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

        ModulePath = exePath;
        _sessionId = Guid.NewGuid().ToString("N");
        var sidShort = _sessionId[..8];
        WpsDebugSender.Log($"LaunchAsync [{sidShort}]: exe='{Path.GetFileName(exePath)}'", LogLevel.Info, LogTag);

        _connection = new WpsHostConnection(_sessionId);
        _connection.ModuleHello += OnModuleHello;
        _connection.ModuleReady += hwnd =>
        {
            ModuleHwnd = new IntPtr(hwnd);
            _connection.StartHeartbeat();
            WpsDebugSender.Log($"LaunchAsync [{sidShort}]: module ready, heartbeat started", LogLevel.Info, LogTag);
            Ready?.Invoke();
        };
        _connection.HungStateChanged += hung => HungStateChanged?.Invoke(hung);

        // (v1.3) Republie les events de la connexion vers les events publics du slot pour que
        // l'orchestrateur (ou un caller direct) puisse s'y abonner sans connaître la connexion.
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
                ProcessExited?.Invoke();
            };
        }

        await startTask.ConfigureAwait(false);
        WpsDebugSender.Log($"LaunchAsync [{sidShort}]: pipes connected, waiting handshake…", LogLevel.Trace, LogTag);
    }

    private async void OnModuleHello(string moduleVersion, string moduleName, WpsModuleKind kind)
    {
        ModuleContractVersion = moduleVersion;
        ModuleName = moduleName;
        ModuleKind = kind;
        var sidShort = _sessionId[..Math.Min(8, _sessionId.Length)];

        // Validation semver lax (cf. WpsContractVersion) : major identique, minor module ≤ minor host
        if (!WpsContractVersion.IsCompatible(moduleVersion, WpsModuleContract.CurrentVersion))
        {
            var msg = $"Module v{moduleVersion} incompatible avec host v{WpsModuleContract.CurrentVersion}";
            WpsDebugSender.Log($"OnModuleHello [{sidShort}]: REJECT {msg}", LogLevel.Error, LogTag);
            IncompatibleContract?.Invoke(msg);
            _connection?.Dispose();
            return;
        }

        try
        {
            await _connection!.SendWelcomeAsync(WpsModuleContract.CurrentVersion).ConfigureAwait(false);
            WpsDebugSender.Log($"OnModuleHello [{sidShort}]: contract v{moduleVersion} OK, WELCOME sent", LogLevel.Trace, LogTag);
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"OnModuleHello [{sidShort}]: SendWelcome failed ({ex.Message}) — pipe coupé, ProcessExited remontera", LogLevel.Warning, LogTag);
        }
    }

    /// <summary>Demande la fermeture propre via CLOSE direct (le module devrait Window.Close()).
    /// API bas-niveau, sans wait ni Kill — pour les usages avancés. Préférer
    /// <see cref="ShutdownAsync(ShutdownOptions, CancellationToken)"/> qui orchestre tout.</summary>
    public Task RequestCloseAsync() => _connection?.SendCloseAsync() ?? Task.CompletedTask;

    /// <summary>Notifie le module des dimensions courantes du slot d'affichage (DIPs).</summary>
    public Task SendResizeAsync(double dipW, double dipH, double dpi) =>
        _connection?.SendResizeAsync(dipW, dipH, dpi) ?? Task.CompletedTask;

    // ====== v1.3 : API canonique étendue ======

    /// <summary>(v1.3) Phase 1 : envoie CAN_CLOSE et attend la réponse Ok/Busy/NeedUser/Rejected
    /// avec timeout (<paramref name="opts"/>.CanCloseTimeoutMs). Si pas de réponse,
    /// renvoie <see cref="CanCloseResponse.Timeout"/>.
    ///
    /// <para>Si le module n'est pas v1.3+ (cf. <see cref="SupportsNegotiatedShutdown"/>), cette
    /// méthode lève <see cref="InvalidOperationException"/> — l'orchestrateur doit checker
    /// <see cref="SupportsNegotiatedShutdown"/> et router vers
    /// <see cref="ShutdownAsync(ShutdownOptions, CancellationToken)"/> pour les modules legacy.</para></summary>
    public async Task<CanCloseResponse> RequestCanCloseAsync(ShutdownOptions opts, CancellationToken ct = default)
    {
        if (_disposed || _connection is null) return CanCloseResponse.Ok;
        if (!SupportsNegotiatedShutdown)
            throw new InvalidOperationException(
                $"Module '{ModuleName}' v{ModuleContractVersion} ne supporte pas le shutdown négocié — " +
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

    /// <summary>(v1.3) Annulation cascade : libère le verrou côté module (Locked → Idle).
    /// À appeler par l'orchestrateur sur les modules ayant déjà répondu Ok quand un autre
    /// module a renvoyé Rejected.</summary>
    public Task SendCanCloseAbortedAsync()
    {
        if (_disposed || _connection is null) return Task.CompletedTask;
        return _connection.SendCanCloseAbortedAsync();
    }

    /// <summary>(v1.3) Phase finale : envoie CLOSE, attend CLOSING_DONE OU Process.Exited
    /// (selon <paramref name="opts"/>.CleanupGracePeriodMs), fallback Kill si timeout et
    /// <c>opts.KillFallback</c>. Retourne le résultat de la séquence.</summary>
    public async Task<ShutdownResult> CompleteShutdownAsync(ShutdownOptions opts, CancellationToken ct = default)
    {
        if (_disposed) return ShutdownResult.NoOp;
        if (_process is null || _process.HasExited)
        {
            DisposeUnmanaged();
            return ShutdownResult.AlreadyExited;
        }

        var sidShort = string.IsNullOrEmpty(_sessionId) ? "?" : _sessionId[..Math.Min(8, _sessionId.Length)];

        // S'abonne à CLOSING_DONE pour court-circuiter le wait Process.Exited (le module signale
        // explicitement la fin de cleanup avant exit, plus rapide qu'attendre l'exit OS).
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

            // Attendre soit CLOSING_DONE soit Process.Exited (premier qui arrive)
            var processExitTask = _process.WaitForExitAsync(cts.Token);
            var first = await Task.WhenAny(closingDoneTcs.Task, processExitTask).ConfigureAwait(false);

            // Si CLOSING_DONE est arrivé en premier, laisser un court délai au process pour exit
            // physiquement avant de constater l'état (sinon HasExited peut être encore false).
            if (first == closingDoneTcs.Task && !_process.HasExited)
            {
                using var shortCts = new CancellationTokenSource(2000);
                try { await _process.WaitForExitAsync(shortCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* ok, on tombe sur le Kill ci-dessous si nécessaire */ }
            }

            if (_process.HasExited)
            {
                WpsDebugSender.Log($"CompleteShutdownAsync [{sidShort}]: process exited gracefully", LogLevel.Success, LogTag);
                _disposed = true;
                DisposeUnmanaged();
                return ShutdownResult.Completed;
            }

            // Timeout : Kill fallback
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
                DisposeUnmanaged();
                return ShutdownResult.Killed;
            }

            // KillFallback=false : on rend la main, le caller décidera.
            _disposed = true;
            DisposeUnmanaged();
            return ShutdownResult.Completed;
        }
        finally
        {
            if (_connection is not null) _connection.ClosingDone -= onClosingDone;
        }
    }

    /// <summary>(v1.3) API tout-en-un pour les call sites simples (toggle daemon, Stop pageslot).
    /// Route automatiquement vers le flow négocié v1.3 (CAN_CLOSE → CompleteShutdown) ou le
    /// fallback v1.2 (CLOSE direct + grace + Kill) selon la version contrat du module.
    ///
    /// <para>Pour les call sites complexes qui pilotent N modules en parallèle (fermeture host,
    /// shutdown OS), préférer l'orchestrateur <see cref="ShutdownOrchestrator"/> qui utilise
    /// les méthodes phasées (RequestCanCloseAsync / CompleteShutdownAsync) pour gérer la queue
    /// NEED_USER et la cancellation cascade.</para></summary>
    public async Task<ShutdownResult> ShutdownAsync(ShutdownOptions opts, CancellationToken ct = default)
    {
        if (_disposed) return ShutdownResult.NoOp;
        if (_process is null || _process.HasExited)
        {
            DisposeUnmanaged();
            _disposed = true;
            return ShutdownResult.AlreadyExited;
        }

        // Routing v1.3 vs v1.2
        if (!SupportsNegotiatedShutdown)
        {
            // Legacy v1.2 : CLOSE direct + grace + Kill via la signature historique
            await ShutdownAsync(opts.CleanupGracePeriodMs).ConfigureAwait(false);
            return _process is null || _process.HasExited ? ShutdownResult.Completed : ShutdownResult.Killed;
        }

        // v1.3 : phase 1 puis phase finale
        var canCloseResult = await RequestCanCloseAsync(opts, ct).ConfigureAwait(false);

        // En tout-en-un, on traite simplement les variantes : Ok / Timeout → CompleteShutdown.
        // Busy → on attend qu'il passe Ok (ou timeout BusyHeartbeat). NeedUser/Rejected ne sont
        // pas gérés ici (l'orchestrateur a la logique UI nécessaire) — on passe en CompleteShutdown
        // et on laisse Kill faire son fallback.
        switch (canCloseResult)
        {
            case CanCloseResponse.RejectedR rejected:
                WpsDebugSender.Log($"ShutdownAsync: module REJECTED ({rejected.Reason}) — annulation",
                    LogLevel.Info, LogTag);
                return ShutdownResult.Aborted;

            case CanCloseResponse.NeedUserR needUser:
                // Pas de UI ici — on traite comme Timeout (Kill direct). L'orchestrateur ne devrait
                // pas appeler ShutdownAsync sur un module qui peut faire NeedUser, mais si ça
                // arrive (call site simple), on ne bloque pas.
                WpsDebugSender.Log($"ShutdownAsync: NeedUser ({needUser.Reason}) reçu hors orchestrateur → Kill",
                    LogLevel.Warning, LogTag);
                if (opts.KillFallback) try { _process.Kill(true); } catch { }
                _disposed = true;
                DisposeUnmanaged();
                return ShutdownResult.Killed;

            case CanCloseResponse.BusyR busy:
                // Attente raisonnable que le Busy se résolve en Ok. Boucle BUSY_PROGRESS.
                await WaitForBusyToResolveAsync(busy, opts, ct).ConfigureAwait(false);
                return await CompleteShutdownAsync(opts, ct).ConfigureAwait(false);

            case CanCloseResponse.OkR or CanCloseResponse.TimeoutR:
            default:
                return await CompleteShutdownAsync(opts, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Attend qu'un Busy se résolve en Ok ou timeout. Surveille les BUSY_PROGRESS pour
    /// reset le watchdog et l'event CanCloseOk pour la sortie en succès.</summary>
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
        catch (OperationCanceledException) { /* propagé par Task.Delay si ct cancel */ }
        finally
        {
            _connection.CanCloseOk -= onOk;
            _connection.BusyProgressReceived -= onProgress;
        }
    }

    // ====== Legacy v1.2 : ShutdownAsync(int gracePeriodMs) ======

    /// <summary>
    /// Terminaison legacy v1.2 : envoie CLOSE via IPC, attend l'exit du process pendant
    /// <paramref name="gracePeriodMs"/> puis Kill(true) si grâce dépassée. Préservée pour
    /// rétrocompat des callers existants. Pour les nouveaux callers, préférer
    /// <see cref="ShutdownAsync(ShutdownOptions, CancellationToken)"/> qui route automatiquement
    /// vers v1.3 si le module le supporte.
    /// <para>Default grace = <b>7000ms</b> : couvre les cleanups réalistes en production
    /// (flush SMB, ABM_REMOVE d'AppBar, commit MariaDB) avec marge pour les configs en pression
    /// mémoire qui peuvent swapper. L'ancien default de 1500ms shootait MiniBoard pendant son
    /// désenregistrement AppBar — d'où ce bump.</para>
    /// <para>Utiliser <c>gracePeriodMs=0</c> pour "kill immédiat propre" (envoie CLOSE puis
    /// Kill sans attendre — le module essaie son cleanup en best-effort dans la fenêtre du
    /// pipe avant disposition). Réservé aux cas où l'on sait qu'aucun cleanup n'est attendu.</para>
    /// <para>C'est le SEUL chemin de fermeture exposé : <see cref="Dispose"/> route ici en
    /// version sync avec grace=2000 (compromis cleanup minimal vs blocage UI thread sync).
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
            DisposeUnmanaged();
            return;
        }

        // 1) Demande gracieuse via CLOSE IPC (même si grace=0, on tente — le module peut
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

        DisposeUnmanaged();
    }

    /// <summary>
    /// Version synchrone de <see cref="ShutdownAsync(int)"/> avec grace=2000ms (envoie CLOSE,
    /// laisse 2s au module pour son cleanup, puis Kill si nécessaire). À utiliser
    /// uniquement quand on ne peut pas await. Préférer <c>await ShutdownAsync()</c> partout
    /// où possible — le default 7000ms donne plus d'air pour un cleanup propre.
    /// <para>Pourquoi 2000 et pas 0 : <c>Dispose</c> est souvent appelé depuis un contexte
    /// sync (using, finalizer-like) où bloquer 2s n'est pas dramatique, mais où shooter le
    /// cleanup applicatif l'est. 2s couvre les cleanups courts (fermeture handles, log
    /// flush) sans bloquer trop longtemps un thread appelant. Pour les cleanups longs il
    /// faut passer par <c>await ShutdownAsync()</c> avec le default 7000.</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        try { ShutdownAsync(2000).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"Dispose: ShutdownAsync(2000) threw {ex.GetType().Name}: {ex.Message}", LogLevel.Warning, LogTag);
            DisposeUnmanaged();
        }
    }

    private void DisposeUnmanaged()
    {
        try { _connection?.Dispose(); } catch { }
        _connection = null;
        try { _process?.Dispose(); } catch { }
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
}
