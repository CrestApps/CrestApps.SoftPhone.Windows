using System.Windows;
using SoftPhone.Core.Contract;

namespace SoftPhone.App;

/// <summary>
/// In-app incoming-call popup (mirrors the extension popup). Shown when a call rings and
/// the phone window isn't focused. Buttons map to Answer / Decline / Voicemail on the call.
/// </summary>
public partial class IncomingCallWindow : Window
{
    public string CallId { get; }

    public event Action<string>? Answered;
    public event Action<string>? Declined;
    public event Action<string>? SentToVoicemail;

    public IncomingCallWindow(Call call, CallContext context)
    {
        InitializeComponent();
        CallId = call.CallId;

        CallerName.Text = ContractHelpers.CallerDisplayName(context, string.IsNullOrEmpty(call.From) ? "Incoming call" : call.From);
        CallerNumber.Text = call.From;

        var queue = QueueNameOf(context);
        if (!string.IsNullOrEmpty(queue))
        {
            QueueText.Text = queue;
            QueueBadge.Visibility = Visibility.Visible;
        }

        PositionBottomRight();
    }

    private static string? QueueNameOf(CallContext? context)
    {
        if (context?.Properties is not null && context.Properties.TryGetValue("queueId", out var q) && !string.IsNullOrEmpty(q))
            return q;
        var badge = context?.Cards?.SelectMany(c => c.Badges ?? Array.Empty<string>()).FirstOrDefault();
        return badge;
    }

    private void PositionBottomRight()
    {
        var wa = SystemParameters.WorkArea;
        Loaded += (_, _) =>
        {
            Left = wa.Right - ActualWidth - 12;
            Top = wa.Bottom - ActualHeight - 12;

            // Force above everything — including other topmost windows — regardless of the
            // phone window's "always on top" preference. Toggling re-asserts z-order.
            Topmost = false;
            Topmost = true;
            Activate();
        };
    }

    private void Answer_Click(object sender, RoutedEventArgs e) { Answered?.Invoke(CallId); Close(); }
    private void Decline_Click(object sender, RoutedEventArgs e) { Declined?.Invoke(CallId); Close(); }
    private void Voicemail_Click(object sender, RoutedEventArgs e) { SentToVoicemail?.Invoke(CallId); Close(); }
}
