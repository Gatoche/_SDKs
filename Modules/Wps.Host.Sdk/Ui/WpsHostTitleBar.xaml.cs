using System.Windows;
using System.Windows.Controls;

namespace Wps.Module.Hosting.Ui;

/// <summary>
/// Wrapper SDK Host de la <see cref="wipisoft.Ui.WpsTitleBar"/> (lib _libs). Ajoute la
/// sémantique "barre de titre du host" avec 2 DependencyProperties pilotées par le caller :
/// <list type="bullet">
///   <item><see cref="HostName"/> : nom du host (ex: "wipiSoft wipiX"). Si null/vide,
///         fallback automatique sur <see cref="Window.Title"/> de la Window parente.</item>
///   <item><see cref="SlotName"/> : nom du module/service actuellement sélectionné dans le
///         host. Le caller le met à jour à chaque changement d'onglet, typiquement avec
///         l'<c>AssemblyTitle</c> lu depuis le PE du module via
///         <see cref="WpsModuleMetadata"/>.</item>
/// </list>
///
/// <para>À chaque changement de l'une des deux props, le contrôle recompose
/// <c>"HostName — SlotName"</c> et injecte cette string dans
/// <see cref="wipisoft.Ui.WpsTitleBar.Title"/> de la WpsTitleBar enfant. Gestion des cas
/// vides : si l'un manque, on affiche l'autre sans séparateur (pas de dash orphelin).</para>
///
/// <para><b>Pourquoi ce wrapper ?</b> La <see cref="wipisoft.Ui.WpsTitleBar"/> est partagée
/// avec les apps standalone (via _libs), qui n'ont pas la notion de "Slot" — elles utilisent
/// juste la prop <c>Title</c> directement. Le SDK Host ajoute ici une couche sémantique
/// host-spécifique sans polluer la lib de base.</para>
/// </summary>
public partial class WpsHostTitleBar : UserControl
{
    /// <summary>Nom du host affiché à gauche du titre composé. Si null/vide, fallback sur
    /// <see cref="Window.Title"/> de la Window parente.</summary>
    public static readonly DependencyProperty HostNameProperty =
        DependencyProperty.Register(nameof(HostName), typeof(string), typeof(WpsHostTitleBar),
            new PropertyMetadata(null, OnTitlePartChanged));

    public string? HostName
    {
        get => (string?)GetValue(HostNameProperty);
        set => SetValue(HostNameProperty, value);
    }

    /// <summary>Nom du module/service actuellement sélectionné dans le host (typiquement
    /// l'AssemblyTitle du PE du module). Affiché à droite du titre composé après un em-dash.
    /// Si null/vide, le titre ne montre que <see cref="HostName"/> sans séparateur.</summary>
    public static readonly DependencyProperty SlotNameProperty =
        DependencyProperty.Register(nameof(SlotName), typeof(string), typeof(WpsHostTitleBar),
            new PropertyMetadata(null, OnTitlePartChanged));

    public string? SlotName
    {
        get => (string?)GetValue(SlotNameProperty);
        set => SetValue(SlotNameProperty, value);
    }

    /// <summary>True quand l'utilisateur considère l'application comme "active" — transmis
    /// tel quel à la WpsTitleBar enfant (binding XAML). Cf. doc équivalent côté lib.</summary>
    public static readonly DependencyProperty IsHostActiveProperty =
        DependencyProperty.Register(nameof(IsHostActive), typeof(bool), typeof(WpsHostTitleBar),
            new PropertyMetadata(true));

    public bool IsHostActive
    {
        get => (bool)GetValue(IsHostActiveProperty);
        set => SetValue(IsHostActiveProperty, value);
    }

    /// <summary>État 2-positions du bouton d'action gauche (typiquement représente un pane
    /// latéral du host : ouvert ou fermé). Par défaut <c>true</c> (= ouvert). Le SDK met à
    /// jour le glyph + tooltip de la WpsTitleBar enfant à chaque changement, et toggle cette
    /// prop quand l'utilisateur clique le bouton.
    /// <para>L'app peut s'abonner aux changements via <c>DependencyPropertyDescriptor</c>
    /// pour réagir (ex: cacher/afficher la sidebar) :</para>
    /// <code>
    /// var dpd = DependencyPropertyDescriptor.FromProperty(
    ///     WpsHostTitleBar.IsLeftPaneOpenProperty, typeof(WpsHostTitleBar));
    /// dpd.AddValueChanged(HostTitleBar, (_, _) =&gt; {
    ///     MyPane.Visibility = HostTitleBar.IsLeftPaneOpen ? Visible : Collapsed;
    /// });
    /// </code></summary>
    public static readonly DependencyProperty IsLeftPaneOpenProperty =
        DependencyProperty.Register(nameof(IsLeftPaneOpen), typeof(bool), typeof(WpsHostTitleBar),
            new PropertyMetadata(true, OnIsLeftPaneOpenChanged));

    public bool IsLeftPaneOpen
    {
        get => (bool)GetValue(IsLeftPaneOpenProperty);
        set => SetValue(IsLeftPaneOpenProperty, value);
    }

    private static void OnIsLeftPaneOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WpsHostTitleBar bar) bar.ApplyLeftActionVisual();
    }

    /// <summary>Glyph affiché dans le bouton de réduction du leftpanel (injecté dans
    /// <c>LeftExtras</c> de la WpsTitleBar enfant). Bindé en XAML, mis à jour par
    /// <see cref="ApplyLeftActionVisual"/> selon <see cref="IsLeftPaneOpen"/>. DP interne,
    /// pas destinée à être set par le caller.</summary>
    public static readonly DependencyProperty LeftPaneToggleGlyphProperty =
        DependencyProperty.Register(nameof(LeftPaneToggleGlyph), typeof(string), typeof(WpsHostTitleBar),
            new PropertyMetadata(""));  // OpenPane par défaut (état IsLeftPaneOpen=true)

    public string? LeftPaneToggleGlyph
    {
        get => (string?)GetValue(LeftPaneToggleGlyphProperty);
        set => SetValue(LeftPaneToggleGlyphProperty, value);
    }

    /// <summary>Tooltip affiché sur le bouton de réduction du leftpanel. Mis à jour par
    /// <see cref="ApplyLeftActionVisual"/>. DP interne.</summary>
    public static readonly DependencyProperty LeftPaneToggleTooltipProperty =
        DependencyProperty.Register(nameof(LeftPaneToggleTooltip), typeof(string), typeof(WpsHostTitleBar),
            new PropertyMetadata("Masquer le volet latéral"));

    public string? LeftPaneToggleTooltip
    {
        get => (string?)GetValue(LeftPaneToggleTooltipProperty);
        set => SetValue(LeftPaneToggleTooltipProperty, value);
    }

    /// <summary>Active ou désactive le bouton de réduction/expansion du leftpanel. Default
    /// <c>true</c>. Le caller peut le mettre à <c>false</c> pour griser le bouton quand le
    /// leftpanel n'est pas pertinent dans la section courante (typiquement : le host ne
    /// montre la sidebar Modules que sur la section Modules ; sur Services/Système/etc.,
    /// le toggle n'a pas d'effet visible → autant le désactiver visuellement).
    /// <para>Le style XAML <c>HostRightBtn</c> rend l'icône à 40% d'opacité quand IsEnabled
    /// est false (cf. Trigger).</para></summary>
    public static readonly DependencyProperty IsLeftPaneToggleEnabledProperty =
        DependencyProperty.Register(nameof(IsLeftPaneToggleEnabled), typeof(bool), typeof(WpsHostTitleBar),
            new PropertyMetadata(true));

    public bool IsLeftPaneToggleEnabled
    {
        get => (bool)GetValue(IsLeftPaneToggleEnabledProperty);
        set => SetValue(IsLeftPaneToggleEnabledProperty, value);
    }

    /// <summary>Met à jour le glyph + tooltip du bouton de réduction du leftpanel (injecté
    /// via LeftExtras) selon <see cref="IsLeftPaneOpen"/>. Glyphs Segoe MDL2 Assets :
    /// <list type="bullet">
    ///   <item>U+E89F = ClosePane (panneau fermé → chevrons vers la droite, "ouvrir")</item>
    ///   <item>U+E8A0 = OpenPane (panneau ouvert → chevrons vers la gauche, "fermer")</item>
    /// </list>
    /// Escape Unicode explicite <c>\uXXXX</c> plutôt que caractères littéraux pour éviter que
    /// les glyphs de la Private Use Area soient avalés par éditeurs / clipboard.</summary>
    private void ApplyLeftActionVisual()
    {
        if (IsLeftPaneOpen)
        {
            LeftPaneToggleGlyph = "";   // OpenPane (suggère "fermer/replier")
            LeftPaneToggleTooltip = "Masquer le volet latéral";
        }
        else
        {
            LeftPaneToggleGlyph = "";   // ClosePane (suggère "ouvrir/déplier")
            LeftPaneToggleTooltip = "Afficher le volet latéral";
        }
    }

    private void OnLeftPaneToggleClick(object sender, RoutedEventArgs e)
    {
        // Toggle l'état — le PropertyChangedCallback re-met à jour le glyph automatiquement.
        IsLeftPaneOpen = !IsLeftPaneOpen;
    }

    // ============================================================
    // 4 sections top-level pilotées par les 4 RadioButton injectés dans LeftExtras
    // (GroupName="TopSections" Window-scoped → mutuellement exclusifs).
    //
    // Chaque section expose :
    //   - une DependencyProperty IsXxxSelected (TwoWay binding sur IsChecked du RadioButton),
    //   - un RoutedEvent XxxClick (raised quand l'utilisateur clique le bouton).
    //
    // Le caller (typiquement la MainWindow du host) :
    //   - lie un TabControl à 4 TabItems sur ces 4 DPs (ou s'abonne via DPD/event),
    //   - met IsModulesSelected=true au boot pour démarrer sur Modules (default au niveau DP).
    //
    // Pourquoi forcer IsXxxSelected = rb.IsChecked dans les handlers Click malgré le binding
    // TwoWay ? Les RadioButton sont injectés cross-namescope (via le ContentPresenter LeftExtras
    // de WpsTitleBar), et l'expérience montre que le binding TwoWay ne propage pas toujours
    // au bon timing dans ce contexte. Le set explicite garantit la cohérence DP ↔ IsChecked
    // au moment où l'event Click est levé.
    // ============================================================

    /// <summary>True quand la section Modules est sélectionnée (RadioButton "Modules" coché).
    /// Default <c>true</c> — section affichée au démarrage du host.</summary>
    public static readonly DependencyProperty IsModulesSelectedProperty =
        DependencyProperty.Register(nameof(IsModulesSelected), typeof(bool), typeof(WpsHostTitleBar),
            new PropertyMetadata(true));

    public bool IsModulesSelected
    {
        get => (bool)GetValue(IsModulesSelectedProperty);
        set => SetValue(IsModulesSelectedProperty, value);
    }

    /// <summary>True quand la section Services est sélectionnée (RadioButton "Services" coché).
    /// Default <c>false</c>.</summary>
    public static readonly DependencyProperty IsServicesSelectedProperty =
        DependencyProperty.Register(nameof(IsServicesSelected), typeof(bool), typeof(WpsHostTitleBar),
            new PropertyMetadata(false));

    public bool IsServicesSelected
    {
        get => (bool)GetValue(IsServicesSelectedProperty);
        set => SetValue(IsServicesSelectedProperty, value);
    }

    /// <summary>True quand la section Système est sélectionnée (RadioButton "Système" coché).
    /// Default <c>false</c>. Remplace l'ancien NavSystem qui vivait en bas du leftpanel.</summary>
    public static readonly DependencyProperty IsSystemSelectedProperty =
        DependencyProperty.Register(nameof(IsSystemSelected), typeof(bool), typeof(WpsHostTitleBar),
            new PropertyMetadata(false));

    public bool IsSystemSelected
    {
        get => (bool)GetValue(IsSystemSelectedProperty);
        set => SetValue(IsSystemSelectedProperty, value);
    }

    /// <summary>True quand la section Gestionnaire est sélectionnée (RadioButton "Gestionnaire"
    /// coché). Default <c>false</c>. Placeholder — à terme, lance wipiManager.Service comme
    /// module embarqué.</summary>
    public static readonly DependencyProperty IsManagerSelectedProperty =
        DependencyProperty.Register(nameof(IsManagerSelected), typeof(bool), typeof(WpsHostTitleBar),
            new PropertyMetadata(false));

    public bool IsManagerSelected
    {
        get => (bool)GetValue(IsManagerSelectedProperty);
        set => SetValue(IsManagerSelectedProperty, value);
    }

    /// <summary>Active ou désactive le RadioButton "Gestionnaire". Default <c>true</c>.
    /// Pattern : le caller peut le mettre à <c>false</c> au boot (icône grisée) puis le
    /// rebasculer à <c>true</c> quand un événement externe le signale (ex : wipiManager.Server
    /// pulse ssActive via wpsServices → le serveur interne est prêt, on active l'accès UI).
    /// <para>Le style XAML <c>HostRightToggle</c> rend l'icône à 40% d'opacité + cursor Arrow
    /// quand IsEnabled est false (cf. Trigger).</para></summary>
    public static readonly DependencyProperty IsManagerEnabledProperty =
        DependencyProperty.Register(nameof(IsManagerEnabled), typeof(bool), typeof(WpsHostTitleBar),
            new PropertyMetadata(true));

    public bool IsManagerEnabled
    {
        get => (bool)GetValue(IsManagerEnabledProperty);
        set => SetValue(IsManagerEnabledProperty, value);
    }

    /// <summary>Affiche ou masque le RadioButton "Gestionnaire" (Visibility Visible/Collapsed).
    /// Default <c>true</c>. Pattern : le caller le met à <c>false</c> quand le poste courant
    /// n'a pas le rôle qui justifie l'accès à la section Gestionnaire (typiquement : poste
    /// non-serveur ne montre pas le bouton de gestion du serveur).</summary>
    public static readonly DependencyProperty IsManagerVisibleProperty =
        DependencyProperty.Register(nameof(IsManagerVisible), typeof(bool), typeof(WpsHostTitleBar),
            new PropertyMetadata(true));

    public bool IsManagerVisible
    {
        get => (bool)GetValue(IsManagerVisibleProperty);
        set => SetValue(IsManagerVisibleProperty, value);
    }

    /// <summary>Event raised quand l'utilisateur clique le bouton Modules.</summary>
    public static readonly RoutedEvent ModulesClickEvent =
        EventManager.RegisterRoutedEvent(nameof(ModulesClick), RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(WpsHostTitleBar));

    public event RoutedEventHandler ModulesClick
    {
        add => AddHandler(ModulesClickEvent, value);
        remove => RemoveHandler(ModulesClickEvent, value);
    }

    /// <summary>Event raised quand l'utilisateur clique le bouton Services.</summary>
    public static readonly RoutedEvent ServicesClickEvent =
        EventManager.RegisterRoutedEvent(nameof(ServicesClick), RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(WpsHostTitleBar));

    public event RoutedEventHandler ServicesClick
    {
        add => AddHandler(ServicesClickEvent, value);
        remove => RemoveHandler(ServicesClickEvent, value);
    }

    /// <summary>Event raised quand l'utilisateur clique le bouton Système.</summary>
    public static readonly RoutedEvent SystemClickEvent =
        EventManager.RegisterRoutedEvent(nameof(SystemClick), RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(WpsHostTitleBar));

    public event RoutedEventHandler SystemClick
    {
        add => AddHandler(SystemClickEvent, value);
        remove => RemoveHandler(SystemClickEvent, value);
    }

    /// <summary>Event raised quand l'utilisateur clique le bouton Gestionnaire.</summary>
    public static readonly RoutedEvent ManagerClickEvent =
        EventManager.RegisterRoutedEvent(nameof(ManagerClick), RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(WpsHostTitleBar));

    public event RoutedEventHandler ManagerClick
    {
        add => AddHandler(ManagerClickEvent, value);
        remove => RemoveHandler(ManagerClickEvent, value);
    }

    /// <summary>Event raised quand l'utilisateur clique le bouton Recherche (loupe) de la barre droite.
    /// Pas encore câblé côté caller — placeholder pour future fonctionnalité de recherche app-wide.</summary>
    public static readonly RoutedEvent SearchClickEvent =
        EventManager.RegisterRoutedEvent(nameof(SearchClick), RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(WpsHostTitleBar));

    public event RoutedEventHandler SearchClick
    {
        add => AddHandler(SearchClickEvent, value);
        remove => RemoveHandler(SearchClickEvent, value);
    }

    /// <summary>Event raised quand l'utilisateur clique le bouton Aide (?) de la barre droite.
    /// Pas encore câblé côté caller — placeholder pour future fonctionnalité d'aide app-wide.</summary>
    public static readonly RoutedEvent HelpClickEvent =
        EventManager.RegisterRoutedEvent(nameof(HelpClick), RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(WpsHostTitleBar));

    public event RoutedEventHandler HelpClick
    {
        add => AddHandler(HelpClickEvent, value);
        remove => RemoveHandler(HelpClickEvent, value);
    }

    // Pattern commun aux 4 handlers des sections top-level : force le sync IsXxxSelected ←
    // IsChecked du RadioButton avant de lever le RoutedEvent. Le binding TwoWay du XAML est
    // censé propager, mais en cross-namescope (RadioButton injecté dans le ContentPresenter
    // LeftExtras de WpsTitleBar), l'expérience a montré des décalages de timing. On force
    // pour garantir la cohérence DP ↔ IsChecked au moment où le caller reçoit l'event.

    private void OnModulesClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb)
            IsModulesSelected = rb.IsChecked ?? false;
        RaiseEvent(new RoutedEventArgs(ModulesClickEvent, this));
    }

    private void OnServicesClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb)
            IsServicesSelected = rb.IsChecked ?? false;
        RaiseEvent(new RoutedEventArgs(ServicesClickEvent, this));
    }

    private void OnSystemClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb)
            IsSystemSelected = rb.IsChecked ?? false;
        RaiseEvent(new RoutedEventArgs(SystemClickEvent, this));
    }

    private void OnManagerClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb)
            IsManagerSelected = rb.IsChecked ?? false;
        RaiseEvent(new RoutedEventArgs(ManagerClickEvent, this));
    }

    private void OnSearchClick(object sender, RoutedEventArgs e)
        => RaiseEvent(new RoutedEventArgs(SearchClickEvent, this));

    private void OnHelpClick(object sender, RoutedEventArgs e)
        => RaiseEvent(new RoutedEventArgs(HelpClickEvent, this));

    public WpsHostTitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null) return;

        // Fallback HostName : si le caller n'a pas surchargé explicitement (UnsetValue), on
        // établit un binding live sur Window.Title pour rester en sync avec la valeur OS.
        if (ReadLocalValue(HostNameProperty) == DependencyProperty.UnsetValue)
        {
            SetBinding(HostNameProperty, new System.Windows.Data.Binding("Title") { Source = window });
        }

        // Premier calcul du titre composé (les PropertyChangedCallback ne déclencheront que
        // sur les modifications futures).
        RecomputeComposedTitle();

        // Premier calcul du glyph/tooltip du bouton d'action gauche selon l'état initial
        // de IsLeftPaneOpen (default true). Les PropertyChangedCallback ne déclencheront
        // que sur les modifications futures.
        ApplyLeftActionVisual();
    }

    private static void OnTitlePartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WpsHostTitleBar bar) bar.RecomputeComposedTitle();
    }

    private void RecomputeComposedTitle()
    {
        var host = HostName?.Trim() ?? "";
        var slot = SlotName?.Trim() ?? "";
        string composed;
        if (host.Length > 0 && slot.Length > 0) composed = $"{host} — {slot}";
        else if (host.Length > 0) composed = host;
        else composed = slot;
        Inner.Title = composed;
    }
}
