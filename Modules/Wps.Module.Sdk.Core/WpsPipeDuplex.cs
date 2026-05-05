using System.IO;
using System.IO.Pipes;

namespace Wps.Module.Core;

/// <summary>
/// Abstraction duplex named-pipe : ouvre un pipe <b>inbound</b> en mode serveur (lecture) et
/// un pipe <b>outbound</b> en mode client (écriture), démarre une boucle de lecture en
/// arrière-plan et expose <see cref="LineReceived"/> pour chaque ligne reçue + <see cref="SendAsync"/>
/// thread-safe pour envoyer.
///
/// Cette classe est <b>neutre</b> : elle ne connaît aucun vocabulaire métier (HELLO, WELCOME,
/// READY, PING, PONG…). C'est aux callers (<c>WpsModuleConnection</c>, <c>WpsHostConnection</c>,
/// <c>WpsModuleServiceConnection</c>) d'interpréter les lignes reçues / construire celles à
/// envoyer selon leurs propres conventions.
///
/// Pourquoi un duplex deux-pipes ? Le contrat wipiSoft utilise deux pipes nommés différents
/// (un par sens) pour pouvoir lire et écrire sans bloquer l'un l'autre, et pour profiter du
/// modèle <c>NamedPipeServerStream</c> côté lecteur (qui attend la connexion entrante)
/// + <c>NamedPipeClientStream</c> côté écrivain (qui se connecte au pipe correspondant
/// ouvert par l'autre process).
/// </summary>
public sealed class WpsPipeDuplex : IDisposable
{
    private readonly string _inboundPipeName;
    private readonly string _outboundPipeName;
    private readonly string _logTag;

    private NamedPipeServerStream? _inbound;
    private NamedPipeClientStream? _outbound;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Émis pour chaque ligne reçue sur le pipe inbound. Invoqué depuis le thread
    /// du ReadLoop (ThreadPool) — le caller est responsable du marshalling vers son thread
    /// applicatif (UI Dispatcher si pertinent).</summary>
    public event Action<string>? LineReceived;

    /// <summary>Émis quand le ReadLoop se termine (EOF du pipe inbound, IOException, ou cancel).
    /// Permet aux SDKs côté ModuleService / Module de détecter qu'ils ont perdu le Host
    /// et sortir de leur boucle d'attente.</summary>
    public event Action? Closed;

    public WpsPipeDuplex(string inboundPipeName, string outboundPipeName, string logTag)
    {
        _inboundPipeName = inboundPipeName;
        _outboundPipeName = outboundPipeName;
        _logTag = logTag;
    }

    /// <summary>
    /// Ouvre le pipe inbound (serveur, attend la connexion) et le pipe outbound (client, se
    /// connecte à l'autre process avec un timeout de 10s) en parallèle. Démarre le ReadLoop.
    /// Lève <see cref="TimeoutException"/> si le client ne peut pas se connecter en 10s.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        WpsDebugSender.Log(
            $"WpsPipeDuplex.StartAsync: in='{_inboundPipeName}' out='{_outboundPipeName}'",
            LogLevel.Trace, _logTag);

        _inbound = new NamedPipeServerStream(
            _inboundPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        _outbound = new NamedPipeClientStream(
            ".", _outboundPipeName, PipeDirection.Out, PipeOptions.Asynchronous);

        var waitInbound = _inbound.WaitForConnectionAsync(_cts.Token);
        var connectOutbound = _outbound.ConnectAsync(10_000, _cts.Token);

        try
        {
            await Task.WhenAll(waitInbound, connectOutbound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            WpsDebugSender.Log(
                $"WpsPipeDuplex.StartAsync: TIMEOUT (10s) sur outbound '{_outboundPipeName}' — peer absent / crashé / mauvais nom de pipe",
                LogLevel.Error, _logTag);
            throw;
        }

        _reader = new StreamReader(_inbound);
        _writer = new StreamWriter(_outbound) { AutoFlush = true };

        _ = Task.Run(ReadLoopAsync, _cts.Token);
    }

    /// <summary>Envoie une ligne sur le pipe outbound. Thread-safe via semaphore. Silencieux
    /// sur <see cref="IOException"/> (pipe coupé : le caller le saura via la fin du ReadLoop).</summary>
    public async Task SendAsync(string line)
    {
        if (_writer is null) return;
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { await _writer.WriteLineAsync(line).ConfigureAwait(false); }
        catch (IOException) { /* peer disconnected, normal pendant un teardown */ }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts!.IsCancellationRequested)
            {
                var line = await _reader!.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    WpsDebugSender.Log(
                        $"WpsPipeDuplex.ReadLoop: EOF inbound '{_inboundPipeName}' (peer fermé)",
                        LogLevel.Trace, _logTag);
                    break;
                }
                try { LineReceived?.Invoke(line); }
                catch (Exception ex)
                {
                    WpsDebugSender.Log(
                        $"WpsPipeDuplex.ReadLoop: LineReceived handler threw {ex.GetType().Name}: {ex.Message}",
                        LogLevel.Warning, _logTag);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown propre */ }
        catch (IOException ex)
        {
            WpsDebugSender.Log(
                $"WpsPipeDuplex.ReadLoop: IOException (pipe coupé, normal au shutdown): {ex.Message}",
                LogLevel.Trace, _logTag);
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log(
                $"WpsPipeDuplex.ReadLoop: unexpected {ex.GetType().Name}: {ex.Message}",
                LogLevel.Error, _logTag);
        }
        finally
        {
            try { Closed?.Invoke(); }
            catch (Exception ex)
            {
                WpsDebugSender.Log(
                    $"WpsPipeDuplex: Closed handler threw {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Warning, _logTag);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        // Tolérant aux IOException : le peer peut être déjà parti, Flush au Dispose lèverait
        // "Pipe is broken". Ce dispose étant typiquement appelé au shutdown / kill, c'est attendu.
        try { _reader?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        try { _writer?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        try { _inbound?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        try { _outbound?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        _writeLock.Dispose();
    }
}
