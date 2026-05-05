using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace Wps.Module.Hosting.Ui;

/// <summary>
/// Barre de titre custom pour les hosts wipiSoft. Préserve les comportements natifs Windows
/// (drag, resize, snap aux bords, Aero shake, animations Win11) via <see cref="WindowChrome"/>.
///
/// <para>Pattern d'utilisation côté host (XAML) :</para>
/// <code>
/// &lt;Window WindowStyle="None" ... xmlns:ui="clr-namespace:Wps.Module.Hosting.Ui;assembly=Wps.Host.Sdk"&gt;
///     &lt;Grid&gt;
///         &lt;Grid.RowDefinitions&gt;
///             &lt;RowDefinition Height="36" /&gt;
///             &lt;RowDefinition Height="*" /&gt;
///         &lt;/Grid.RowDefinitions&gt;
///         &lt;ui:WpsTitleBar Grid.Row="0" /&gt;
///         &lt;Grid Grid.Row="1"&gt; ... contenu ... &lt;/Grid&gt;
///     &lt;/Grid&gt;
/// &lt;/Window&gt;
/// </code>
///
/// <para>Le UserControl s'auto-attache la WindowChrome au Loaded si la Window parente n'en a pas.
/// Le caller peut aussi appeler <see cref="ApplyWindowChrome"/> avant le Loaded pour
/// pré-configurer (rare).</para>
///
/// <para>Le titre et l'icône sont bindés sur <c>Window.Title</c> et <c>Window.Icon</c> de la
/// Window parente — le caller ne les configure qu'à un seul endroit (sur la Window) comme
/// avant la barre custom.</para>
/// </summary>
public partial class WpsTitleBar : UserControl
{
    /// <summary>
    /// True quand l'utilisateur considère l'application comme "active" — typiquement quand le
    /// foreground est sur la fenêtre du host OU sur l'un de ses modules embedded.
    /// Le host doit set cette propriété explicitement (via son focus polling, son hook
    /// d'activation, etc.) — par défaut true, ce qui colle au comportement standard
    /// <see cref="Window.IsActive"/> tant que l'app n'a pas de modules embedded.
    /// </summary>
    public static readonly DependencyProperty IsHostActiveProperty =
        DependencyProperty.Register(nameof(IsHostActive), typeof(bool), typeof(WpsTitleBar),
            new PropertyMetadata(true));

    public bool IsHostActive
    {
        get => (bool)GetValue(IsHostActiveProperty);
        set => SetValue(IsHostActiveProperty, value);
    }

    public WpsTitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null) return;

        // Auto-attache la WindowChrome si pas déjà configurée par le caller.
        if (WindowChrome.GetWindowChrome(window) is null)
            ApplyWindowChrome(window);

        // Fallback Window.Icon : avec WindowStyle="None", Windows ne dessine plus la barre
        // native et n'utilise plus l'icône du PE automatiquement. Si le caller n'a pas défini
        // explicitement Window.Icon, on l'extrait du PE de l'exe en cours via shell32. Le
        // binding XAML du TextBlock icon se rafraîchira automatiquement.
        if (window.Icon is null)
        {
            try
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    window.Icon = ExtractIconAsImageSource(exePath);
            }
            catch { /* best-effort, pas d'icône si échec */ }
        }

        // Coins arrondis Win11 + liseret 1px gris discret (style VS 2022). Silencieusement
        // ignoré sur Win10 < Build 22000 → fenêtre carrée comme avant, pas de régression.
        ApplyWindow11Frame(window, Color.FromRgb(0x3F, 0x3F, 0x46));

        // Bascule du glyph maximize/restore selon WindowState (le user peut maximiser via
        // Win+flèche, double-clic sur barre, drag aux bords, etc. — pas seulement notre bouton).
        window.StateChanged += (_, _) => UpdateMaxRestoreGlyph(window);
        UpdateMaxRestoreGlyph(window);
    }

    /// <summary>
    /// Configure la <see cref="WindowChrome"/> sur la Window appelante. Caption 36 px (= hauteur
    /// du UserControl), bordure resize 6 px, pas de Aero buttons natifs (on dessine les nôtres).
    /// Appelé automatiquement au Loaded si la Window n'a pas déjà une WindowChrome.
    /// </summary>
    /// <summary>Largeur minimale par défaut imposée aux hosts wipiSoft (= SVGA classique).
    /// Le caller peut override en set <see cref="Window.MinWidth"/> explicitement dans son XAML
    /// avant que <see cref="ApplyWindowChrome"/> ne s'exécute.</summary>
    public const double DefaultMinWidth = 640;

    /// <summary>Hauteur minimale par défaut. Cf. <see cref="DefaultMinWidth"/>.</summary>
    public const double DefaultMinHeight = 480;

    public static void ApplyWindowChrome(Window window)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 36,
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });

        // Limites minimales par défaut pour les hosts wipiSoft : 640x480. Le caller peut
        // override en set MinWidth/MinHeight dans son XAML — on ne touche que si la valeur
        // est 0 (défaut WPF, donc non explicitement définie).
        if (window.MinWidth == 0) window.MinWidth = DefaultMinWidth;
        if (window.MinHeight == 0) window.MinHeight = DefaultMinHeight;

        // Hook WM_GETMINMAXINFO : 2 corrections nécessaires avec WindowStyle="None" + WindowChrome :
        //   1. Maximize ne doit pas déborder sur la taskbar → ajuste ptMaxSize/ptMaxPosition
        //      à la work area du moniteur.
        //   2. Le resize doit respecter Window.MinWidth/MinHeight → ajuste ptMinTrackSize
        //      (sinon WPF MinWidth est ignoré pendant le resize avec WindowChrome).
        // On capture la Window via closure pour pouvoir lire MinWidth/MinHeight + DPI.
        var helper = new WindowInteropHelper(window);
        var hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) hwnd = helper.EnsureHandle();
        var src = HwndSource.FromHwnd(hwnd);
        if (src is not null)
        {
            HwndSourceHook hook = (IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == WM_GETMINMAXINFO)
                {
                    AdjustMinMaxInfo(h, l, window);
                    handled = true;
                }
                return IntPtr.Zero;
            };
            src.AddHook(hook);
        }
    }

    private const int WM_GETMINMAXINFO = 0x0024;

    private static void AdjustMinMaxInfo(IntPtr hwnd, IntPtr lParam, Window window)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        // 1. MaxSize/MaxPosition : work area du moniteur (Maximize sans déborder sur taskbar).
        const int MONITOR_DEFAULTTONEAREST = 0x00000002;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref mi))
            {
                var work = mi.rcWork;
                var screen = mi.rcMonitor;
                // ptMaxPosition est relatif au moniteur, pas à l'écran virtuel — d'où le delta.
                mmi.ptMaxPosition.x = Math.Abs(work.left - screen.left);
                mmi.ptMaxPosition.y = Math.Abs(work.top - screen.top);
                mmi.ptMaxSize.x = Math.Abs(work.right - work.left);
                mmi.ptMaxSize.y = Math.Abs(work.bottom - work.top);
            }
        }

        // 2. MinTrackSize : Window.MinWidth/MinHeight (en DIPs) → pixels physiques via DPI.
        if (window.MinWidth > 0 || window.MinHeight > 0)
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            if (window.MinWidth > 0)
                mmi.ptMinTrackSize.x = (int)Math.Ceiling(window.MinWidth * dpi.DpiScaleX);
            if (window.MinHeight > 0)
                mmi.ptMinTrackSize.y = (int)Math.Ceiling(window.MinHeight * dpi.DpiScaleY);
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ====== Extraction icône native du PE (fallback si Window.Icon non défini) ======
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractAssociatedIconW")]
    private static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, StringBuilder pszIconPath, out ushort piIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static ImageSource? ExtractIconAsImageSource(string exePath)
    {
        try
        {
            var sb = new StringBuilder(exePath, 260);
            var hIcon = ExtractAssociatedIcon(IntPtr.Zero, sb, out _);
            if (hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(hIcon); }
        }
        catch { return null; }
    }

    // ====== DWM Win11 (coins arrondis + couleur de liseret) ======
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// Applique les coins arrondis Win11 et la couleur du liseret 1 px sur la Window appelante
    /// via les APIs DWM (<c>DwmSetWindowAttribute</c>). Silencieusement ignoré sur Win10 et
    /// versions antérieures à Build 22000 — la fenêtre reste carrée sans erreur visible.
    /// </summary>
    public static void ApplyWindow11Frame(Window window, Color borderColor)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch { /* Win10 ou DWM API absente : ignoré */ }

        try
        {
            // COLORREF est en BGR (0x00BBGGRR), pas RGB.
            int color = (borderColor.B << 16) | (borderColor.G << 8) | borderColor.R;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref color, sizeof(int));
        }
        catch { }
    }

    private void UpdateMaxRestoreGlyph(Window window)
    {
        // Segoe MDL2 :  = ChromeMaximize,  = ChromeRestore
        if (window.WindowState == WindowState.Maximized)
        {
            MaxRestoreBtn.Content = "";
            MaxRestoreBtn.ToolTip = "Restaurer";
        }
        else
        {
            MaxRestoreBtn.Content = "";
            MaxRestoreBtn.ToolTip = "Agrandir";
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is not null) window.WindowState = WindowState.Minimized;
    }

    private void OnMaxRestore(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null) return;
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Window.GetWindow(this)?.Close();
    }
}
