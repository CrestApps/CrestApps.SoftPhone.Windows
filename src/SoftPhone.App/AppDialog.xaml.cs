using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SoftPhone.App;

public enum AppDialogKind
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// The app's own message and confirmation dialog, styled like the rest of the app (the stock
/// Windows message box looked foreign next to it). Modal, centered on its owner, Esc cancels.
/// </summary>
public partial class AppDialog : Window
{
    private AppDialog(Window? owner, AppDialogKind kind, string title, string message,
        string confirmText, string? cancelText, bool confirmIsDefault)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;

        if (cancelText is null)
            CancelButton.Visibility = Visibility.Collapsed;
        else
            CancelButton.Content = cancelText;

        // A confirmation defaults to the safe choice, so Enter never triggers the risky action by accident.
        var defaultButton = confirmIsDefault || cancelText is null ? ConfirmButton : CancelButton;
        defaultButton.IsDefault = true;

        var (glyph, color, tint) = kind switch
        {
            AppDialogKind.Warning => ("", "Warn", "#FEF3C7"),
            AppDialogKind.Error => ("", "Fail", "#FEE2E2"),
            _ => ("", "Brand", "#CCFBF1"),
        };
        IconGlyph.Text = glyph;
        IconGlyph.Foreground = (Brush)FindResource(color);
        IconCircle.Background = (Brush)new BrushConverter().ConvertFromString(tint)!;

        if (owner is { IsLoaded: true, IsVisible: true })
        {
            Owner = owner;
            Topmost = owner.Topmost; // stay above an "always on top" phone window
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Loaded += (_, _) => defaultButton.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            DialogResult = false;
        };
    }

    /// <summary>Ask the agent to confirm an action. True only when they chose <paramref name="confirmText"/>.</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirmText,
        string cancelText = "Cancel", AppDialogKind kind = AppDialogKind.Warning) =>
        new AppDialog(owner, kind, title, message, confirmText, cancelText, confirmIsDefault: false).ShowDialog() == true;

    /// <summary>Tell the agent something, with a single OK button.</summary>
    public static void Show(Window? owner, string title, string message, AppDialogKind kind = AppDialogKind.Info) =>
        new AppDialog(owner, kind, title, message, "OK", cancelText: null, confirmIsDefault: true).ShowDialog();

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
