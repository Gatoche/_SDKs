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
/// await slot.ShutdownAsync();   // ou slot.Dispose() en contexte sync
/// </code>
/// </summary>
public sealed class WpsModuleSlot : IDisposable
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

    /// <summary>
    /// Terminaison canonique du slot : envoie CLOSE via IPC, attend l'exit du process
    /// pendant <paramref name="gracePeriodMs"/> puis Kill(true) si grâce dépassée, et libère
    /// toutes les ressources (connexion + process). Idempotent.
    /// <para>Default grace = <b>7000ms</b> : couvre les cleanups réalistes en production
    /// (flush SMB, ABM_REMOVE d'AppBar, commit MariaDB) avec marge pour les configs un peu
    /// justes en mémoire qui peuvent swapper. L'ancien default de 1500ms shootait MiniBoard
    /// pendant son désenregistrement AppBar — d'où ce bump.</para>
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
    /// Version synchrone de <see cref="ShutdownAsync"/> avec grace=2000ms (envoie CLOSE,
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
}
