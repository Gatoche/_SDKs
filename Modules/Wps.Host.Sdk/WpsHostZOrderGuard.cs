using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Wps.Module.Hosting;

/// <summary>
/// Garde-fou de z-order pour les hosts wipiSoft qui embarquent des fenêtres natives de modules
/// par-dessus leur slot WPF via <c>SetWindowPos</c> cross-process (pattern HwndHost-like). Quand
/// le host utilise une titlebar custom (<c>WpsTitleBar</c> + <c>WindowChrome</c> + <c>WindowStyle=None</c>),
/// <c>WindowChromeWorker</c> (interne WPF) traite au hover des boutons système des messages NC
/// (<c>WM_NCHITTEST</c>, <c>WM_NCMOUSEMOVE</c>) qui déclenchent un <c>SetWindowPos</c> parasite sur la
/// fenêtre host modifiant son z-order. Le host main passe alors AU-DESSUS du natif Module, qui
/// devient physiquement caché derrière la zone WPF noire du pageslot — symptôme : "page noire au
/// hover des boutons système custom".
///
/// <para><b>Diagnostic</b> : aucun <c>WM_WINDOWPOSCHANGED</c> n'arrive au HWND module (le natif ne
/// bouge pas, il est juste recouvert). Seul le HWND du host reçoit le <c>WM_WINDOWPOSCHANGED</c>
/// avec changement de z-order. La z-order chain (<c>GetWindow(GW_HWNDPREV)</c>) montre que la
/// MainWindow host est passée juste au-dessus du natif.</para>
///
/// <para><b>Fix réactif</b> : on hook <c>WM_WINDOWPOSCHANGED</c> sur le host et dès qu'on détecte un
/// changement de z-order (flag <c>SWP_NOZORDER</c> absent), on flip immédiatement le HWND natif
/// visible via <c>HWND_TOPMOST → HWND_NOTOPMOST</c> pour le ramener au top de la stack non-topmost.
/// Le flip est asynchrone (<c>SWP_ASYNCWINDOWPOS</c>) pour ne pas bloquer le pump si le module est
/// hangé.</para>
///
/// <para><b>Utilisation</b> :</para>
/// <code>
/// // Dans OnSourceInitialized du host :
/// WpsHostZOrderGuard.Attach(this, () =>
/// {
///     var visibleSlot = _slotPages.FirstOrDefault(p => p.IsVisible);
///     return visibleSlot?.ModuleSlot.ModuleHwnd ?? IntPtr.Zero;
/// });
/// </code>
///
/// <para>Le helper s'auto-désabonne quand la <c>Window</c> est fermée (<c>Closed</c> event).</para>
///
/// <para><b>TODO préemptif</b> : tentative d'intercepter <c>WM_WINDOWPOSCHANGING</c> pour annuler le
/// changement directement plutôt que le réverser. Première approche échouée (filtre
/// <c>hwndInsertAfter == HWND_TOP</c> ne couvre pas tous les cas WindowChrome). Approche à raffiner
/// avec une heuristique plus fine.</para>
/// </summary>
public static class WpsHostZOrderGuard
{
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>Attache le garde-fou de z-order à la <paramref name="hostWindow"/>. Le hook est
    /// installé une fois le HWND host créé (via <see cref="Window.SourceInitialized"/> si pas encore
    /// fait, sinon immédiatement) et retiré quand la fenêtre est fermée.
    /// <para>La callback <paramref name="getVisibleNativeHwnd"/> est invoquée à chaque changement
    /// de z-order détecté pour récupérer le HWND du natif visible à remettre au top. Si elle
    /// retourne <c>IntPtr.Zero</c>, le flip est skippé (cas où aucun module n'est encore embedded).</para>
    /// </summary>
    public static void Attach(Window hostWindow, Func<IntPtr> getVisibleNativeHwnd)
    {
        if (hostWindow is null) throw new ArgumentNullException(nameof(hostWindow));
        if (getVisibleNativeHwnd is null) throw new ArgumentNullException(nameof(getVisibleNativeHwnd));

        HwndSourceHook? hook = null;
        HwndSource? source = null;

        void Install()
        {
            var hwnd = new WindowInteropHelper(hostWindow).Handle;
            if (hwnd == IntPtr.Zero) return;
            source = HwndSource.FromHwnd(hwnd);
            if (source is null) return;
            hook = (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WM_WINDOWPOSCHANGED && lParam != IntPtr.Zero)
                {
                    try
                    {
                        // WINDOWPOS layout : hwnd(IntPtr) hwndInsertAfter(IntPtr) x(int) y(int) cx(int) cy(int) flags(uint)
                        int flagsOffset = IntPtr.Size * 2 + 16;
                        uint flags = (uint)Marshal.ReadInt32(lParam, flagsOffset);
                        bool zorderChanged = (flags & SWP_NOZORDER) == 0;
                        if (zorderChanged)
                        {
                            var nativeHwnd = getVisibleNativeHwnd();
                            if (nativeHwnd != IntPtr.Zero)
                            {
                                // Flip TOPMOST → NOTOPMOST : ramène le natif au top de la stack
                                // non-topmost de manière fiable, même quand WindowChrome est en
                                // plein WindowPosChanged.
                                SetWindowPos(nativeHwnd, HWND_TOPMOST, 0, 0, 0, 0,
                                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
                                SetWindowPos(nativeHwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
                            }
                        }
                    }
                    catch { /* defensive */ }
                }
                return IntPtr.Zero;
            };
            source.AddHook(hook);
        }

        // SourceInitialized peut avoir déjà fire (cas où Attach est appelé tardivement) → on
        // installe immédiatement. Sinon on diffère.
        if (new WindowInteropHelper(hostWindow).Handle != IntPtr.Zero)
        {
            Install();
        }
        else
        {
            hostWindow.SourceInitialized += (_, _) => Install();
        }

        // Auto-désabonnement à la fermeture
        hostWindow.Closed += (_, _) =>
        {
            try { if (source is not null && hook is not null) source.RemoveHook(hook); }
            catch { }
        };
    }
}
