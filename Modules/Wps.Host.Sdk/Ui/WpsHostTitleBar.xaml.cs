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

    // (TEMPORAIREMENT DÉSACTIVÉ — investigation bug "page noire / snapshot obsolète" au
    // survol du LeftActionBtn de la WpsTitleBar enfant. On commente tout le bloc lié au
    // pane gauche pour isoler. Re-activer en retirant le /* */ ci-dessous et en restaurant
    // LeftActionClick="OnInnerLeftActionClick" dans le XAML.)
    /*
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

    /// <summary>Met à jour le glyph + tooltip de la WpsTitleBar enfant selon l'état courant
    /// de <see cref="IsLeftPaneOpen"/>. Glyphs Segoe MDL2 Assets :
    /// <list type="bullet">
    ///   <item>U+E89F = ClosePane (pane est ouvert, clic pour fermer)</item>
    ///   <item>U+E8A0 = OpenPane (pane est fermé, clic pour ouvrir)</item>
    /// </list>
    /// Escape Unicode explicite (\u) plutôt que caractères littéraux pour éviter que les
    /// glyphs de la Private Use Area soient avalés par éditeurs / clipboard.</summary>
    private void ApplyLeftActionVisual()
    {
        if (Inner is null) return;
        if (IsLeftPaneOpen)
        {
            Inner.LeftActionGlyph = "";  // ClosePane
            Inner.LeftActionTooltip = "Masquer le volet latéral";
        }
        else
        {
            Inner.LeftActionGlyph = "";  // OpenPane
            Inner.LeftActionTooltip = "Afficher le volet latéral";
        }
    }

    private void OnInnerLeftActionClick(object sender, RoutedEventArgs e)
    {
        // Toggle l'état — le PropertyChangedCallback re-met à jour le glyph automatiquement.
        IsLeftPaneOpen = !IsLeftPaneOpen;
    }
    */

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

        // (TEMPORAIREMENT DÉSACTIVÉ avec le reste du bloc LeftAction.)
        // ApplyLeftActionVisual();
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
