using System.Diagnostics;
using System.IO;
using Wps.Module.Core;

namespace Wps.Module.Hosting;

/// <summary>
/// Représente un slot d'embedding pour un module wipiSoft. Encapsule tout le cycle de vie :
/// lancement détaché du process, handshake IPC (HELLO/WELCOME avec validation contract version),
/// récupération du HWND du module, surveillance heartbeat, et kill propre.
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
/// slot.KillImmediate();
/// </code>
/// </summary>
public sealed class WpsModuleSlot : IDisposable
{
    private WpsHostConnection? _connection;
    private Process? _process;
    private string _sessionId = "";

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

    /// <summary>Demande la fermeture propre via CLOSE (le module devrait Window.Close()).</summary>
    public Task RequestCloseAsync() => _connection?.SendCloseAsync() ?? Task.CompletedTask;

    /// <summary>Notifie le module des dimensions courantes du slot d'affichage (DIPs).</summary>
    public Task SendResizeAsync(double dipW, double dipH, double dpi) =>
        _connection?.SendResizeAsync(dipW, dipH, dpi) ?? Task.CompletedTask;

    /// <summary>Tue le process immédiatement (sans grâce). À utiliser si CLOSE ne répond pas
    /// ou pour fermeture forcée à la sortie du host.</summary>
    public void KillImmediate()
    {
        var sidShort = string.IsNullOrEmpty(_sessionId) ? "?" : _sessionId[..Math.Min(8, _sessionId.Length)];
        WpsDebugSender.Log($"KillImmediate [{sidShort}]: pid={_process?.Id ?? -1} hasExited={_process?.HasExited}", LogLevel.Info, LogTag);
        try { if (_process is { HasExited: false }) _process.Kill(true); }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"KillImmediate [{sidShort}]: Kill threw {ex.GetType().Name}: {ex.Message}", LogLevel.Trace, LogTag);
        }
        _connection?.Dispose();
        _connection = null;
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _process?.Dispose();
    }
}
