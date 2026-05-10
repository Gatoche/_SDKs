using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace Wps.Module.Hosting.Ui;

/// <summary>
/// (v1.3 final) Dialog modale custom affichée côté HOST en réponse à un
/// <see cref="WpsModuleContract.NotifCanCloseNeedUser"/>. Remplace <c>MessageBox.Show</c>
/// pour permettre :
/// <list type="bullet">
///   <item>Labels custom de boutons (id → label venant du module/service)</item>
///   <item>Croix de fermeture optionnelle (neutralisée si <c>!AllowClose</c>)</item>
///   <item>Esc neutralisé si <c>!AllowClose</c> (l'utilisateur DOIT cliquer un bouton)</item>
///   <item>Ordre des boutons préservé selon le dict <c>Answers</c> du module</item>
///   <item>1er bouton = IsDefault (Enter), bouton id <c>cancel</c> = IsCancel (Esc)</item>
/// </list>
/// <para>Usage typique côté host :</para>
/// <code>
/// var dlg = new WpsConfirmDialog(payload, ownerWindow) { Title = $"{target.Name} — {payload.Reason}" };
/// dlg.ShowDialog();
/// var buttonId = dlg.ClickedButtonId;  // null si Esc/croix utilisés et AllowClose=true
/// </code>
/// </summary>
public partial class WpsConfirmDialog : Window
{
    private readonly bool _allowClose;
    private readonly string? _cancelButtonId;

    /// <summary>Id du bouton cliqué par l'utilisateur, ou <c>null</c> si la dialog a été
    /// fermée via la croix/Esc (uniquement possible si <c>AllowClose=true</c>).
    /// <para>Si <c>null</c> et qu'il existe un bouton id <c>cancel</c>, le caller doit
    /// traiter ça comme un clic sur ce bouton (mapping standard côté SDK module).</para></summary>
    public string? ClickedButtonId { get; private set; }

    public WpsConfirmDialog(NeedUserPayload payload, Window? owner)
    {
        InitializeComponent();
        Owner = owner;
        _allowClose = payload.AllowClose;

        // Sous-titre / contexte : caché si pas de reason
        if (string.IsNullOrWhiteSpace(payload.Reason))
        {
            ReasonText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ReasonText.Text = payload.Reason;
        }

        QuestionText.Text = payload.Ask;

        // Génération des boutons dans l'ordre du dictionnaire (Dictionary<,> .NET 6+
        // préserve l'ordre d'insertion). 1er = default (Enter), id "cancel" = IsCancel (Esc).
        var isFirst = true;
        foreach (var (id, label) in payload.Answers)
        {
            var btn = new Button
            {
                Content = label,
                Tag = id,
                Style = (Style)FindResource("ActionButtonStyle"),
                IsDefault = isFirst,
                IsCancel = string.Equals(id, "cancel", System.StringComparison.OrdinalIgnoreCase),
            };
            btn.Click += OnAnswerButtonClick;
            ButtonsHost.Items.Add(btn);
            if (btn.IsCancel) _cancelButtonId = id;
            isFirst = false;
        }

        // Si pas de bouton cancel et qu'on ne permet pas la fermeture, on doit intercepter
        // Esc au niveau Window pour ne pas avoir un raccourci qui ne fait rien (ou pire,
        // ferme la dialog en laissant ClickedButtonId à null).
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnAnswerButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ClickedButtonId = id;
            DialogResult = true;
            Close();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc bloqué si !AllowClose ET pas de bouton "cancel" (sinon WPF route Esc vers
        // IsCancel=true et la dialog ferme avec ClickedButtonId à null → ambigu).
        if (e.Key == Key.Escape && !_allowClose && _cancelButtonId is null)
        {
            e.Handled = true;
        }
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Si !AllowClose : masque la croix de fermeture via Win32 (RemoveMenu sur SC_CLOSE
        // du menu système). Plus propre que de juste intercepter Closing — l'utilisateur
        // ne voit même pas le bouton X, pas de confusion.
        if (!_allowClose)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var sysMenu = GetSystemMenu(hwnd, false);
            if (sysMenu != IntPtr.Zero)
            {
                RemoveMenu(sysMenu, SC_CLOSE, MF_BYCOMMAND);
            }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Filet supplémentaire : si pour une raison X la fermeture est tentée alors que
        // AllowClose=false et qu'aucun bouton n'a été cliqué, on bloque. En pratique avec
        // la croix masquée et Esc intercepté ça ne devrait pas arriver, mais défensif.
        if (!_allowClose && ClickedButtonId is null)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    // ====== P/Invoke pour masquer la croix de fermeture ======

    private const uint SC_CLOSE = 0xF060;
    private const uint MF_BYCOMMAND = 0x00000000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern int RemoveMenu(IntPtr hMenu, uint uPosition, uint uFlags);
}
