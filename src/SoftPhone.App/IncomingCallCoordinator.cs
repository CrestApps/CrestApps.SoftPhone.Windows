using System.Windows;
using System.Windows.Threading;
using SoftPhone.Core.Contract;
using SoftPhone.Core.Incoming;

namespace SoftPhone.App;

/// <summary>What the coordinator hands back to the app.</summary>
/// <param name="Carry">Carry out an agent's choice (post to the page, reload to answer, or hub Reject/Voicemail).</param>
/// <param name="OpenUrl">Open a matched record's link in the default browser.</param>
/// <param name="PostToPage">Post a host-bridge message to the loaded /softphone page.</param>
/// <param name="ResolveUrl">Resolve a card link to an absolute web URL (null if it is not one).</param>
public sealed record IncomingCallActions(
    Action<IncomingCallChoice> Carry,
    Action<string> OpenUrl,
    Action<string> PostToPage,
    Func<string?, Uri?> ResolveUrl);

/// <summary>
/// The WPF side of the incoming-call UX (contract §7/§8): one popup per ringing call, stacked in
/// the bottom-right corner, plus the looping ringtone. <see cref="IncomingCallTracker"/> decides
/// what shows and what the /softphone page is told; this class only opens, updates and closes
/// the windows it is asked to, and reports back when a popup is rendered or closed by the agent.
/// </summary>
public sealed class IncomingCallCoordinator : IIncomingCallSurface, IDisposable
{
    private const double Gap = 8;
    private const double ScreenMargin = 12;

    private readonly App _app;
    private readonly IncomingCallActions _actions;
    private readonly RingtonePlayer _ring = new();
    private readonly List<IncomingCallWindow> _popups = new();
    private readonly DispatcherTimer _expiry;

    public IncomingCallCoordinator(App app, IncomingCallActions actions)
    {
        _app = app;
        _actions = actions;
        Tracker = new IncomingCallTracker(this, Log.Info);

        // Close a background popup the moment its offer reservation expires (the page does the same
        // for calls it reported), rather than on the next current-offer poll.
        _expiry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _expiry.Tick += (_, _) =>
        {
            if (Tracker.Calls.Count > 0) Tracker.ExpireDue(DateTimeOffset.UtcNow);
        };
        _expiry.Start();
    }

    public IncomingCallTracker Tracker { get; }

    // ---- inputs from the app ----

    public void HandleIncoming(Call call, CallContext context) => Tracker.BackgroundIncoming(call, context);
    public void HandleStateChanged(Call call) => Tracker.BackgroundStateChanged(call);
    public void OnNoActiveOffer() => Tracker.BackgroundNoActiveOffer();
    public void OnCallAnsweredElsewhere(IncomingCallAnsweredNotice notice) =>
        Tracker.BackgroundCallAnswered(notice.CallId, notice.OfferId);
    public void OnPageBridgeChanged(bool ready) => Tracker.PageBridgeChanged(ready);
    public void OnPageMessage(PageMessage message) => Tracker.Page(message);

    /// <summary>The phone window gained or lost focus, or was minimized.</summary>
    public void OnPhoneFocusChanged() => Tracker.Refresh();

    // ---- IIncomingCallSurface ----

    public bool IsPhoneWindowFocused => _app.IsPhoneWindowFocused;
    public bool RingtoneEnabled => _app.RingtoneEnabled;

    public void ShowPopup(RingingCall call)
    {
        var popup = new IncomingCallWindow(call, _actions.ResolveUrl);
        popup.Chosen += (action, url) => Tracker.Choose(popup.CallId, action, url);
        popup.OpenRequested += url => _actions.OpenUrl(url);
        popup.ContentRendered += (_, _) => Tracker.PopupRendered(popup.CallId);
        popup.Loaded += (_, _) => Layout();
        popup.SizeChanged += (_, _) => Layout();
        popup.ClosedByAgent += () =>
        {
            _popups.Remove(popup);
            Layout();
            Tracker.PopupClosedByAgent(popup.CallId);
        };

        _popups.Add(popup);
        popup.Show();
        Layout();
        popup.Activate();
    }

    public void UpdatePopup(RingingCall call) => Find(call.CallId)?.Render(call);

    public void ClosePopup(string callId)
    {
        var popup = Find(callId);
        if (popup is null) return;
        _popups.Remove(popup);
        popup.CloseByHost();
        Layout();
    }

    public void SetRinging(bool ringing)
    {
        if (ringing) _ring.Play();
        else _ring.Stop();
    }

    public void PostToPage(string json) => _actions.PostToPage(json);
    public void Carry(IncomingCallChoice choice) => _actions.Carry(choice);

    private IncomingCallWindow? Find(string callId) => _popups.FirstOrDefault(p => p.CallId == callId);

    /// <summary>Stack the popups upward from the bottom-right of the work area, oldest at the bottom.</summary>
    private void Layout()
    {
        var wa = SystemParameters.WorkArea;
        var bottom = wa.Bottom - ScreenMargin;
        foreach (var popup in _popups)
        {
            if (!popup.IsLoaded) continue; // positioned by its own Loaded
            popup.Left = wa.Right - popup.ActualWidth - ScreenMargin;
            popup.Top = Math.Max(wa.Top, bottom - popup.ActualHeight);
            bottom = popup.Top - Gap;
        }
    }

    public void Dispose()
    {
        _expiry.Stop();
        Tracker.Clear();
        _ring.Dispose();
    }
}
