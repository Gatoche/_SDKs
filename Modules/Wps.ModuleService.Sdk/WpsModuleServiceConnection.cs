using System.Globalization;
using System.Text.Json;
using Wps.Module;
using Wps.Module.Core;

namespace Wps.ModuleService;

/// <summary>
/// Connexion IPC côté ModuleService. Symétrique à <c>WpsModuleConnection</c> (côté Module classique)
/// mais headless : pas de Dispatcher WPF, pas de gestion RESIZE/READY|hwnd. Annonce le Kind
/// <see cref="WpsModuleKind.ModuleService"/> dans le HELLO et dispatche les nouveaux messages
/// <c>INVOKE</c> / <c>SHOW_SETTINGS</c> du contrat 1.2.
///
/// Les handlers Invoke sont enregistrés via <see cref="RegisterInvokeHandler"/> (string method name
/// → async handler). Pour chaque INVOKE reçu, on cherche le handler correspondant, on désérialise
/// le payload JSON en <c>JsonElement</c>, on appelle le handler, on sérialise le résultat et on
/// répond INVOKE_RESULT|requestId|OK|jsonResult. En cas d'exception ou de handler manquant, on
/// répond INVOKE_RESULT|requestId|ERROR|message.
/// </summary>
internal sealed class WpsModuleServiceConnection : IDisposable
{
    private readonly string _sessionId;
    private readonly WpsPipeDuplex _duplex;

    // Handler signature : (jsonParams) → Task<jsonResult>. Le SDK fait l'aiguillage du JSON,
    // le caller utilise des wrappers typés (cf. WpsModuleService.RegisterInvokeHandler<T,U>).
    private readonly Dictionary<string, Func<JsonElement, Task<string>>> _invokeHandlers
        = new(StringComparer.OrdinalIgnoreCase);

    // Handler optionnel pour SHOW_SETTINGS (factory de fenêtre WPF — peut être null si le service
    // n'expose pas de paramétrage).
    private Action? _showSettingsHandler;

    private readonly TaskCompletionSource<string> _welcomeTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Émis quand le pipe est coupé par le Host (EOF). Le caller (typiquement le main
    /// du service console) peut s'en servir pour sortir de RunAsync.</summary>
    public event Action? PipeClosed;

    /// <summary>Émis quand le Host envoie CLOSE (shutdown propre). Le caller doit terminer
    /// son travail en cours et sortir.</summary>
    public event Action? ShutdownRequested;

    private const string LogTag = "Wps.ModuleService.Sdk";

    public WpsModuleServiceConnection(string sessionId)
    {
        _sessionId = sessionId;

        // Côté ModuleService : reçoit sur "Cmd" (server in), envoie sur "Notif" (client out).
        // Mêmes conventions de nommage que le Module classique — le contrat reste unifié.
        var cmdPipe = WpsModuleContract.IpcNames.CommandPipe(_sessionId);
        var notifPipe = WpsModuleContract.IpcNames.NotificationPipe(_sessionId);
        _duplex = new WpsPipeDuplex(inboundPipeName: cmdPipe, outboundPipeName: notifPipe, LogTag);
        _duplex.LineReceived += Dispatch;
        _duplex.Closed += () => PipeClosed?.Invoke();
    }

    public Task StartAsync(CancellationToken ct = default) => _duplex.StartAsync(ct);

    /// <summary>Envoie HELLO|version|name|ModuleService.</summary>
    public Task SendHelloAsync(string contractVersion, string serviceName) =>
        _duplex.SendAsync(FormattableString.Invariant(
            $"{WpsModuleContract.NotifHello}{WpsModuleContract.Separator}{contractVersion}{WpsModuleContract.Separator}{serviceName}{WpsModuleContract.Separator}{WpsModuleKind.ModuleService}"));

    public Task<string> WaitForWelcomeAsync(TimeSpan? timeout = null)
    {
        var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        cts.Token.Register(() => _welcomeTcs.TrySetException(new TimeoutException("WELCOME not received")));
        return _welcomeTcs.Task;
    }

    /// <summary>Envoie READY|0 pour signaler "service prêt à recevoir des Invoke" (pas de
    /// HWND à embed côté ModuleService — le 0 est explicite).</summary>
    public Task NotifyReadyAsync() =>
        _duplex.SendAsync($"{WpsModuleContract.NotifReady}{WpsModuleContract.Separator}0");

    public void RegisterInvokeHandler(string method, Func<JsonElement, Task<string>> handler)
    {
        if (string.IsNullOrEmpty(method)) throw new ArgumentException("method required", nameof(method));
        _invokeHandlers[method] = handler;
    }

    public void SetShowSettingsHandler(Action handler) => _showSettingsHandler = handler;

    private void Dispatch(string line)
    {
        // ⚠️ Pour CmdInvoke : le payload JSON peut contenir des '|' → on doit limiter le split
        // aux 4 premières parties (cmd, requestId, method, jsonPayload-recombiné).
        // Pour les autres commandes, un Split classique suffit.
        if (line.StartsWith(WpsModuleContract.CmdInvoke + WpsModuleContract.Separator, StringComparison.Ordinal))
        {
            HandleInvoke(line);
            return;
        }

        var parts = line.Split(WpsModuleContract.Separator);
        switch (parts[0])
        {
            case WpsModuleContract.CmdWelcome:
                _welcomeTcs.TrySetResult(parts.Length > 1 ? parts[1] : "");
                break;

            case WpsModuleContract.CmdClose:
                ShutdownRequested?.Invoke();
                break;

            case WpsModuleContract.CmdPing:
                // Pas de UI thread à sonder côté ModuleService console : on répond PONG direct
                // depuis le ReadLoop. Si le service a un workload bloquant qui occupe le pipe
                // d'écoute, le PONG ne sortira pas → c'est un signal "non réactif" légitime.
                _ = _duplex.SendAsync(WpsModuleContract.NotifPong);
                break;

            case WpsModuleContract.CmdShowSettings:
                try { _showSettingsHandler?.Invoke(); }
                catch (Exception ex)
                {
                    WpsDebugSender.Log($"ShowSettings handler threw {ex.GetType().Name}: {ex.Message}",
                        LogLevel.Warning, LogTag);
                }
                break;
        }
    }

    private async void HandleInvoke(string line)
    {
        // Format : INVOKE|requestId|method|jsonParams (le jsonParams peut contenir des '|')
        // string.Split avec count=4 limite à 4 parts : la 4e contient tout le reste.
        var parts = line.Split(WpsModuleContract.Separator, 4);
        if (parts.Length < 4)
        {
            WpsDebugSender.Log($"INVOKE malformed (parts={parts.Length}): {line}", LogLevel.Warning, LogTag);
            return;
        }

        var requestId = parts[1];
        var method = parts[2];
        var jsonParams = parts[3];

        if (!_invokeHandlers.TryGetValue(method, out var handler))
        {
            await SendInvokeResultAsync(requestId, ok: false, payload: $"unknown method '{method}'");
            return;
        }

        JsonElement paramsJson;
        try
        {
            using var doc = JsonDocument.Parse(jsonParams);
            paramsJson = doc.RootElement.Clone();
        }
        catch (JsonException jex)
        {
            await SendInvokeResultAsync(requestId, ok: false, payload: $"invalid JSON params: {jex.Message}");
            return;
        }

        try
        {
            var resultJson = await handler(paramsJson).ConfigureAwait(false);
            await SendInvokeResultAsync(requestId, ok: true, payload: resultJson);
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"INVOKE '{method}' handler threw {ex.GetType().Name}: {ex.Message}",
                LogLevel.Error, LogTag);
            await SendInvokeResultAsync(requestId, ok: false, payload: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private Task SendInvokeResultAsync(string requestId, bool ok, string payload)
    {
        var status = ok ? WpsModuleContract.InvokeStatusOk : WpsModuleContract.InvokeStatusError;
        return _duplex.SendAsync(
            $"{WpsModuleContract.NotifInvokeResult}{WpsModuleContract.Separator}{requestId}{WpsModuleContract.Separator}{status}{WpsModuleContract.Separator}{payload}");
    }

    public void Dispose() => _duplex.Dispose();
}
