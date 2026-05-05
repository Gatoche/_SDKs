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
/// client.KillImmediate();
/// </code>
/// </summary>
public sealed class WpsModuleServiceClient : IDisposable
{
    private WpsHostConnection? _connection;
    private Process? _process;
    private string _sessionId = "";
    private bool _ready;

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

    /// <summary>Demande la fermeture propre via CLOSE.</summary>
    public Task RequestCloseAsync() => _connection?.SendCloseAsync() ?? Task.CompletedTask;

    /// <summary>Tue le process immédiatement.</summary>
    public void KillImmediate()
    {
        var sidShort = string.IsNullOrEmpty(_sessionId) ? "?" : _sessionId[..Math.Min(8, _sessionId.Length)];
        WpsDebugSender.Log($"KillImmediate [{sidShort}]: pid={_process?.Id ?? -1} hasExited={_process?.HasExited}", LogLevel.Info, LogTag);
        try { if (_process is { HasExited: false }) _process.Kill(true); }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"KillImmediate [{sidShort}]: Kill threw {ex.GetType().Name}: {ex.Message}", LogLevel.Trace, LogTag);
        }
        FailAllPendingInvokes("service killed");
        _connection?.Dispose();
        _connection = null;
    }

    public void Dispose()
    {
        FailAllPendingInvokes("client disposed");
        _connection?.Dispose();
        _process?.Dispose();
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
