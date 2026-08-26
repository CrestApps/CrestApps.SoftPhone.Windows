using SoftPhone.Core.Contract;

namespace SoftPhone.App;

/// <summary>Actions the coordinator invokes for the three call responses.</summary>
public sealed record IncomingCallActions(
    Action<string> Answer,
    Action<string> Decline,
    Action<string> Voicemail);

/// <summary>
/// Turns background <c>IncomingCall</c> / <c>CallStateChanged</c> events into the local
/// incoming UX: loops the ringtone and shows the in-app popup (contract §7/§8).
///
/// The popup is shown whenever the phone window is NOT focused during a ringing call — so it
/// appears immediately if no window is open, AND it appears the moment the user minimizes or
/// clicks away from the phone window while a call is still ringing. When the phone window is
/// focused (the page shows its own incoming UI) the popup is hidden to avoid double UI.
/// </summary>
public sealed class IncomingCallCoordinator : IDisposable
{
    private readonly App _app;
    private readonly IncomingCallActions _actions;
    private readonly RingtonePlayer _ring = new();
    private IncomingCallWindow? _popup;

    private string? _currentCallId;
    private Call? _currentCall;
    private CallContext? _currentContext;

    public IncomingCallCoordinator(App app, IncomingCallActions actions)
    {
        _app = app;
        _actions = actions;
    }

    public void HandleIncoming(Call call, CallContext context)
    {
        if (call is null || string.IsNullOrEmpty(call.CallId)) return;

        // Dedup: ignore repeat offers for a call we're already ringing.
        if (_currentCallId == call.CallId && _popup is not null) return;

        _currentCallId = call.CallId;
        _currentCall = call;
        _currentContext = context;
        Log.Info($"Incoming call {call.CallId} from {call.From}");

        if (_app.RingtoneEnabled)
            _ring.Play();

        ShowPopupIfNeeded();
    }

    /// <summary>Show the popup if a call is ringing and the phone window isn't focused.</summary>
    public void ShowPopupIfNeeded()
    {
        if (_currentCallId is null || _currentCall is null) return; // nothing ringing
        if (_popup is not null) return;                             // already visible
        if (_app.IsPhoneWindowFocused) return;                      // page shows its own UI

        _popup = new IncomingCallWindow(_currentCall, _currentContext ?? new CallContext());
        _popup.Answered += id => { _actions.Answer(id); Clear(id); };
        _popup.Declined += id => { _actions.Decline(id); Clear(id); };
        _popup.SentToVoicemail += id => { _actions.Voicemail(id); Clear(id); };
        _popup.Show();
        _popup.Activate();
    }

    /// <summary>Phone window came to the foreground — hide the popup (the page shows the call).</summary>
    public void OnPhoneForegrounded() => ClosePopup();

    /// <summary>Phone window was minimized or lost focus — show the popup if still ringing.</summary>
    public void OnPhoneBackgrounded() => ShowPopupIfNeeded();

    public void HandleStateChanged(Call call)
    {
        if (call is null) return;
        if (call.CallId == _currentCallId && !ContractHelpers.IsRingingState(call.State))
            Clear(call.CallId);
    }

    /// <summary>Stop the ring and dismiss the popup for <paramref name="callId"/> (or the current call).</summary>
    public void Clear(string? callId = null)
    {
        if (callId is not null && _currentCallId is not null && callId != _currentCallId) return;
        _ring.Stop();
        ClosePopup();
        _currentCallId = null;
        _currentCall = null;
        _currentContext = null;
    }

    private void ClosePopup()
    {
        if (_popup is null) return;
        var p = _popup;
        _popup = null;
        try { p.Close(); } catch { }
    }

    public void Dispose()
    {
        Clear();
        _ring.Dispose();
    }
}
