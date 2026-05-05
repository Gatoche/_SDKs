using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Wps.Module;

/// <summary>
/// Configure une <see cref="Window"/> WPF pour le mode "module embarqué" :
/// <list type="bullet">
///   <item>Position parkée à (-32000, -32000) — invisible jusqu'à ce que le host la place</item>
///   <item>Pas de chrome (WindowStyle=None, ResizeMode=NoResize)</item>
///   <item>Pas d'icône dans la barre des tâches</item>
///   <item>Pas activée au lancement (ne vole pas le focus)</item>
///   <item><c>WS_EX_TOOLWINDOW</c> via Win32 → invisible dans Alt+Tab et Win+Tab</item>
///   <item>Coins carrés via DWM (override du round Win11) → pixel-perfect dans le slot</item>
/// </list>
/// À appeler AVANT <see cref="Window.Show"/>. La Window XAML reste "normale" (centerScreen,
/// taille raisonnable, chrome standard) — c'est le SDK qui adapte au runtime selon le mode.
/// </summary>
internal static class WpsModuleWindowSetup
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ConfigureForModuleMode(Window window)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));

        // Propriétés WPF — appliquées avant Show
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;

        // Styles Win32 — appliqués une fois le HWND créé
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            // WS_EX_TOOLWINDOW : disparait d'Alt+Tab, Win+Tab, taskbar (en plus du flag WPF)
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);

            // Coins carrés (Win11) — pour coller pixel-perfect au slot du host
            try
            {
                int corner = DWMWCP_DONOTROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            }
            catch { /* DWM API peut ne pas exister sur vieux Windows, on ignore */ }
        };
    }
}
