// ============================================================================
// La saga des 9 heures sur la page noire au hover des boutons système custom
// ============================================================================
//
// Origine : observé d'abord côté ModuloSlot.Host après introduction de la titlebar
// custom WpsHostTitleBar (WindowChrome + WindowStyle=None). Symptôme : au survol
// (sans clic) des boutons Réduire / Agrandir / Fermer, le pageslot du module
// actif passe brièvement au noir ou affiche un snapshot stale, et le module ne
// réagit plus aux clics. Le natif du module est techniquement toujours visible,
// juste physiquement masqué.
//
// 9 heures de debug + ~25 fausses pistes avant d'identifier la cause root :
//   - Filtre Deactivated avec fgIsNull (transitions Windows momentanées) ✓
//     utile mais insuffisant
//   - Projection.Source = null après ShowOverlay (mitigation symptôme : noir
//     pur plutôt que snapshot trompeur) ✓ gardé côté host
//   - Timer différé 150ms sur Deactivated externe (annule park si souris
//     revient) ✓ gardé côté host
//   - SetForeground(module) en fin de fade-out cross-fade ✗ casse les
//     transitions suivantes
//   - WS_EX_NOACTIVATE sur le HWND module ✗ inefficace
//   - Commande IPC SELF_FOREGROUND (module se foregrounde lui-même) ✗
//     inefficace (restrictions Win32 cross-process)
//   - RedrawWindow périodique sur natif (théorie DWM frozen frame) ✗
//     inefficace
//   - Poll côté SDK module pour observer son propre état ✓ utile pour
//     diagnostic
//   - Hook WM_WINDOWPOSCHANGED/WM_SHOWWINDOW côté module ✓ utile : a confirmé
//     que le HWND module NE REÇOIT AUCUN message Win32 pendant le bug
//   - Hotkey global Ctrl+Shift+D pour dump diagnostic on-demand (z-order chain
//     complète + descripteur HWND tiers) ⭐ a permis d'identifier la cause root
//
// Cause root identifiée via dumps Ctrl+Shift+D (comparaison z-order chain
// AVANT/APRÈS bug) :
//   - AVANT bug : hwndPrev[+1] du natif = 'Hidden Window' (interne Host
//     inoffensive)
//   - APRÈS bug : hwndPrev[+1] du natif = 'ModuloSlot - Host' (la WPF
//     MainWindow principale est passée AU-DESSUS du natif dans le z-order)
//
// Explication technique : WindowChromeWorker (interne WPF), au hover des
// boutons système custom, traite des messages NC (WM_NCHITTEST,
// WM_NCMOUSEMOVE) qui déclenchent un SetWindowPos parasite sur la fenêtre
// host modifiant son z-order. Le host main passe alors au-dessus du natif
// Module qui avait été positionné par-dessus via SetWindowPos cross-process.
// Le natif est donc physiquement caché derrière la zone WPF noire du
// pageslot — sans que son HWND ne reçoive aucun message Win32.
//
// Fix réactif (cette classe) : intercepter WM_WINDOWPOSCHANGED sur le host.
// Si le z-order a été modifié (flag SWP_NOZORDER absent), refaire
// immédiatement un flip TOPMOST → NOTOPMOST sur le HWND du natif visible.
//
// TODO refactor préemptif : intercepter WM_WINDOWPOSCHANGING (qui fire AVANT
// le changement) et forcer SWP_NOZORDER dans les flags pour annuler le
// changement directement, plutôt que de le réverser après. Première tentative
// échouée car le filtre hwndInsertAfter == HWND_TOP ne couvre pas tous les
// cas — WindowChrome utilise d'autres valeurs. Approche à raffiner avec une
// heuristique plus fine (peut-être par tracking des messages NC précédents).
// Le fix réactif actuel est fonctionnel et stable, mais préemptif éviterait
// le bref flash invisible du changement-puis-réversion.
//
// Leçon : quand un bug visuel se manifeste sans aucun log côté code
// applicatif, c'est un bug de z-order Win32 ou de rendering DWM. L'arsenal de
// diagnostic à 3 niveaux (poll module 2s, hook WM_* côté module, dump
// on-demand de la z-order chain) a été décisif pour localiser. À
// industrialiser pour les futurs bugs similaires.
//
// ============================================================================

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
