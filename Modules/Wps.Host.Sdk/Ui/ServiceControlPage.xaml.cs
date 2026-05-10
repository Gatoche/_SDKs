using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wps.Module.Core;

namespace Wps.Module.Hosting.Ui;

/// <summary>
/// Pageslot d'un ModuleService dans le TabControl central d'un host wipiSoft. Affiche le nom +
/// version, le statut (Running/Stopped), un toggle "Démarrer le service au lancement"
/// (persistance HKCU via <see cref="WpsServiceDaemonConfig"/>), et des actions
/// (Paramètres, Redémarrer, Arrêter).
///
/// <para>Le client IPC <see cref="WpsModuleServiceClient"/> est créé/géré ici. Si le service
/// est en mode Daemon (HKCU=true), le host l'aura déjà lancé au démarrage et nous passe
/// l'instance via <see cref="AttachClient"/> ; sinon (OnDemand), c'est cette page qui le
/// lance à <see cref="OnIsVisibleChangedHandler"/> et le tue à la sortie de visibilité.</para>
///
/// <para>Pattern d'utilisation côté host :</para>
/// <code>
/// var page = new ServiceControlPage();
/// page.Initialize("ModuloSlot", svc, existingDaemonClient);
/// page.DaemonToggleChanged += (svc, enabled) => /* gérer cycle de vie Daemon vs OnDemand */;
/// </code>
/// </summary>
public partial class ServiceControlPage : UserControl
{
    private string _hostName = "";
    private WpsDiscoveredModule? _service;
    private WpsModuleServiceClient? _client;
    private bool _ownsClient;  // true si on a lancé le client nous-mêmes (OnDemand)
    private readonly DispatcherTimer _statusTimer;

    private const string LogTag = "Wps.Host.Sdk.Ui";

    public ServiceControlPage()
    {
        InitializeComponent();
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        IsVisibleChanged += OnIsVisibleChangedHandler;
    }

    /// <summary>
    /// Initialise la page avec le descripteur du service et le nom du host appelant
    /// (utilisé pour préfixer la clé HKCU du flag Daemon).
    /// Si <paramref name="existingClient"/> est non-null, c'est que le service tourne déjà
    /// en mode Daemon — on s'y attache au lieu d'en relancer un.
    /// </summary>
    public void Initialize(string hostName, WpsDiscoveredModule service, WpsModuleServiceClient? existingClient)
    {
        _hostName = hostName ?? throw new ArgumentNullException(nameof(hostName));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _client = existingClient;
        _ownsClient = false;  // Daemon → host possède le client

        ServiceTitle.Text = service.DisplayName;
        ServiceSubtitle.Text = $"v{service.Version}  ·  {service.Name}";
        ServiceIcon.Source = service.Icon;
        ServiceDescription.Text = string.IsNullOrWhiteSpace(service.Description)
            ? "(aucune description embarquée)"
            : service.Description;

        DaemonToggle.IsChecked = WpsServiceDaemonConfig.IsDaemonEnabled(_hostName, service.Name);

        RefreshStatus();
    }

    private async void OnIsVisibleChangedHandler(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_service is null) return;
        bool visible = (bool)e.NewValue;
        if (visible)
        {
            // On NE lance PAS automatiquement le service à l'ouverture de la pageslot. Si l'user
            // l'a désactivé en Daemon, il ne veut pas qu'il tourne juste parce qu'il consulte
            // les paramètres/le statut. Il clique Redémarrer s'il veut le lancer.
            _statusTimer.Start();
            RefreshStatus();
        }
        else
        {
            _statusTimer.Stop();

            // OnDemand actif (l'user avait cliqué Redémarrer pendant la visibilité) : on ferme
            // gracieusement à la sortie pour que le service ait le temps de cleanup
            // (Window_Closing → AppBar Unregister, etc.). Sauf si le user a basculé Daemon=true
            // pendant la visite, auquel cas le client a été promu au host (_ownsClient=false).
            if (_ownsClient && _client is not null)
            {
                WpsDebugSender.Log($"ServiceControlPage [{_service.Name}]: page hidden → graceful shutdown (OnDemand)",
                    LogLevel.Info, LogTag);
                var c = _client;
                _client = null;
                _ownsClient = false;
                try { await c.ShutdownAsync().ConfigureAwait(true); }
                catch (Exception ex)
                {
                    WpsDebugSender.Log($"ServiceControlPage [{_service?.Name}]: ShutdownAsync threw {ex.GetType().Name}: {ex.Message}",
                        LogLevel.Warning, LogTag);
                }
                // Pas de c.Dispose() ici : ShutdownAsync libère déjà connexion + process
                // (cf. WpsModuleServiceClient.DisposeUnmanaged appelé en fin de Shutdown).
                // Le double dispose serait idempotent grâce à _disposed mais reste du bruit.
            }
        }
    }

    private async Task EnsureClientLaunchedAsync()
    {
        if (_service is null) return;
        if (_client is not null) return;

        WpsDebugSender.Log($"ServiceControlPage [{_service.Name}]: launching OnDemand…",
            LogLevel.Info, LogTag);
        var client = new WpsModuleServiceClient();
        _client = client;
        _ownsClient = true;
        try
        {
            await client.LaunchAsync(_service.ExePath);
            // Lancement OK → on efface une éventuelle erreur de déploiement précédente.
            SetDeployError(null);
        }
        catch (WpsDeployInvalidException ex)
        {
            // Le déploiement n'a pas passé la vérification d'intégrité. On annule le
            // launch côté client (sinon il reste dans un état "OnDemand actif sans process")
            // et on affiche le message d'erreur dans la zone dédiée à droite du statut.
            _client = null;
            _ownsClient = false;
            SetDeployError(ex.Result.DisplayMessage);
            WpsDebugSender.Log($"ServiceControlPage [{_service.Name}]: deploy invalid → {ex.Result.DisplayMessage}",
                LogLevel.Error, LogTag);
        }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"ServiceControlPage [{_service.Name}]: launch failed {ex.GetType().Name}: {ex.Message}",
                LogLevel.Error, LogTag);
        }
    }

    /// <summary>Pilote l'affichage de la zone d'erreur de déploiement (à droite du statut).
    /// Passer <c>null</c> ou chaîne vide pour masquer.</summary>
    private void SetDeployError(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            DeployErrorText.Text = "";
            DeployErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            DeployErrorText.Text = $"🟥 Deploy invalide : {message}";
            DeployErrorText.Visibility = Visibility.Visible;
        }
    }

    private void RefreshStatus()
    {
        // Le timer tick toutes les 1s. Pendant la fermeture du service, le Process peut être
        // déjà Dispose() côté WpsModuleServiceClient (DisposeUnmanaged) — la référence
        // _client.Process reste non-null mais .HasExited lève InvalidOperationException
        // ("No process is associated with this object"). On considère ce cas comme "not
        // running" sans laisser remonter l'exception.
        bool running;
        try { running = _client?.Process is { HasExited: false }; }
        catch (InvalidOperationException) { running = false; }
        bool ready = _client?.IsReady == true;
        if (running && ready)
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x4D, 0xC2, 0x6F));
            StatusText.Text = "En fonctionnement";
        }
        else if (running)
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x3A, 0x7B, 0xBF));
            StatusText.Text = "Démarrage en cours…";
        }
        else
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x80, 0x90, 0x9A));
            StatusText.Text = "Arrêté";
        }

        SettingsBtn.IsEnabled = ready;
        StopBtn.IsEnabled = running;
        RestartBtn.IsEnabled = true; // toujours utile (relance après crash)
    }

    private void OnDaemonToggleClick(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        bool enabled = DaemonToggle.IsChecked == true;
        WpsServiceDaemonConfig.SetDaemonEnabled(_hostName, _service.Name, enabled);

        // Notifie le host pour qu'il prenne en charge le cycle de vie au runtime :
        //  - si activé : host lance le service (s'il ne tourne pas déjà), prend possession du client
        //  - si désactivé : host lâche le client → on retombe en OnDemand piloté par cette page
        DaemonToggleChanged?.Invoke(_service, enabled);

        WpsDebugSender.Log($"ServiceControlPage [{_service.Name}]: Daemon={enabled} (HKCU + runtime)",
            LogLevel.Info, LogTag);
    }

    /// <summary>Émis quand l'utilisateur bascule le toggle Daemon. Le host s'occupe du cycle
    /// de vie (lance ou prend en charge l'arrêt selon le nouveau mode).</summary>
    public event Action<WpsDiscoveredModule, bool>? DaemonToggleChanged;

    /// <summary>Permet au host de pousser un client lancé en mode Daemon dans la pageslot,
    /// pour synchroniser l'affichage du statut.</summary>
    public void AttachClient(WpsModuleServiceClient client)
    {
        _client = client;
        _ownsClient = false;
        RefreshStatus();
    }

    /// <summary>Détache le client (utilisé quand le user désactive Daemon → host décide
    /// de la suite : kill du client, ou re-adopt en OnDemand). Le client retourné n'est
    /// PAS tué ici — c'est au caller de décider. RefreshStatus est appelé pour que l'UI
    /// passe immédiatement à "Arrêté" sans attendre le tick du timer.</summary>
    public WpsModuleServiceClient? DetachClient()
    {
        var c = _client;
        if (c is not null)
        {
            _client = null;
            _ownsClient = false;
            RefreshStatus();
        }
        return c;
    }

    /// <summary>(v1.3 final) Lecture pure de la référence au client sans la nullifier côté
    /// page (contrairement à <see cref="DetachClient"/>). Utile quand un caller a besoin
    /// d'opérer sur le client (ex : orchestrateur de shutdown qui collecte les targets) sans
    /// pour autant priver la pageslot de sa référence — si l'opération est annulée, la
    /// page continue à fonctionner normalement.</summary>
    public WpsModuleServiceClient? PeekClient() => _client;

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_client is null || !_client.IsReady) return;
        try { await _client.ShowSettingsAsync(); }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"ShowSettings [{_service?.Name}] FAILED: {ex.GetType().Name}: {ex.Message}",
                LogLevel.Warning, LogTag);
        }
    }

    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        WpsDebugSender.Log($"ServiceControlPage [{_service.Name}]: Restart requested", LogLevel.Info, LogTag);
        if (_client is not null)
        {
            var c = _client;
            _client = null;
            try { await c.ShutdownAsync().ConfigureAwait(true); }
            catch (Exception ex)
            {
                WpsDebugSender.Log($"ServiceControlPage [{_service.Name}]: ShutdownAsync threw {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Warning, LogTag);
            }
            // Pas de c.Dispose() ici : ShutdownAsync libère déjà connexion + process.
        }
        // Relance via le même chemin que OnDemand (et notifie le host si Daemon)
        DaemonToggleChanged?.Invoke(_service, WpsServiceDaemonConfig.IsDaemonEnabled(_hostName, _service.Name));
        await Task.Delay(150);
        if (_client is null) await EnsureClientLaunchedAsync();
        RefreshStatus();
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_service is null || _client is null) return;
        WpsDebugSender.Log($"ServiceControlPage [{_service.Name}]: Stop requested → graceful shutdown", LogLevel.Info, LogTag);
        var c = _client;
        _client = null;
        _ownsClient = false;
        try { await c.ShutdownAsync().ConfigureAwait(true); }
        catch (Exception ex)
        {
            WpsDebugSender.Log($"ServiceControlPage [{_service?.Name}]: ShutdownAsync threw {ex.GetType().Name}: {ex.Message}",
                LogLevel.Warning, LogTag);
        }
        // Pas de c.Dispose() ici : ShutdownAsync libère déjà connexion + process.
        RefreshStatus();
    }
}
