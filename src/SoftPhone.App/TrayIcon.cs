using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using SoftPhone.Core.Hub;

namespace SoftPhone.App;

/// <summary>Callbacks the tray menu routes to (kept UI-agnostic for wiring in App).</summary>
public sealed record TrayActions(
    Action OpenPhone,
    Action OpenSettings,
    Action RunConnectionTest,
    Action SimulateIncoming,
    Action Quit);

/// <summary>
/// The system-tray icon + context menu (contract §8). Left click opens/focuses the phone;
/// right click shows Open / Settings / (diagnostics) / Quit. Diagnostics items are hidden
/// unless the user has enabled diagnostics mode.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _statusItem;

    public TrayIcon(TrayActions actions, bool diagnosticsEnabled)
    {
        _icon = new TaskbarIcon
        {
            IconSource = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico")),
            ToolTipText = "Soft Phone",
        };

        _icon.TrayLeftMouseUp += (_, _) => actions.OpenPhone();

        var menu = new ContextMenu();

        _statusItem = new MenuItem { Header = "Not connected", IsEnabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("Open phone", actions.OpenPhone, isDefault: true));
        menu.Items.Add(Item("Settings…", actions.OpenSettings));

        if (diagnosticsEnabled)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Run connection test", actions.RunConnectionTest));
            menu.Items.Add(Item("Simulate incoming call", actions.SimulateIncoming));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit", actions.Quit));

        _icon.ContextMenu = menu;
        _icon.ForceCreate();
    }

    private static MenuItem Item(string header, Action onClick, bool isDefault = false)
    {
        var item = new MenuItem { Header = header, FontWeight = isDefault ? FontWeights.SemiBold : FontWeights.Normal };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>Reflect the background connection state in the tray tooltip + menu header.</summary>
    public void UpdateStatus(ConnectionStatus status, string? detail = null)
    {
        var text = status switch
        {
            ConnectionStatus.Connected => "Connected — ready for calls",
            ConnectionStatus.Connecting => "Connecting…",
            ConnectionStatus.Reconnecting => "Reconnecting…",
            ConnectionStatus.SignedOut => "Signed out — open the phone to sign in",
            ConnectionStatus.Error => "Connection error",
            _ => "Not connected",
        };
        _statusItem.Header = text;
        _icon.ToolTipText = $"CrestApps Soft Phone — {text}";
    }

    /// <summary>Pop a lightweight balloon (used by the dev simulate flow when toasts aren't available).</summary>
    public void ShowBalloon(string title, string message) =>
        _icon.ShowNotification(title, message);

    public void Dispose() => _icon.Dispose();
}
