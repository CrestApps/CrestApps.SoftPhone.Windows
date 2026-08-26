using SoftPhone.Core.Contract;

namespace SoftPhone.App;

/// <summary>Actions the coordinator invokes for the three call responses.</summary>
public sealed record IncomingCallActions(
    Action<string> Answer,
    Action<string> Decline,
    Action<string> Voicemail);

/// <summary>
/// Turns background <c>IncomingCall</c> / <c>CallStateChanged</c> events into the local
/// incoming UX: loops the ringtone and shows the in-app popup (contract §7/§8). Suppresses
/// the popup when the phone window is already focused (the page shows its own incoming UI —
/// avoids double UI). Clears everything when the call stops ringing or is handled.
/// </summary>
public sealed class IncomingCallCoordinator : IDisposable
{
    private readonly App _app;
    private readonly IncomingCallActions _actions;
    private readonly RingtonePlayer _ring = new();
    private IncomingCallWindow? _popup;
    private string? _currentCallId;

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
        Log.Info($"Incoming call {call.CallId} from {call.From}");

        if (_app.RingtoneEnabled)
            _ring.Play();

        // Double-UI suppression: if the phone window is open AND focused, let the page's own
        // incoming UI handle it — no popup, no toast.
        if (_app.IsPhoneWindowFocused)
            return;

        _popup?.Close();
        _popup = new IncomingCallWindow(call, context);
        _popup.Answered += id => { _actions.Answer(id); Clear(id); };
        _popup.Declined += id => { _actions.Decline(id); Clear(id); };
        _popup.SentToVoicemail += id => { _actions.Voicemail(id); Clear(id); };
        _popup.Show();
        _popup.Activate();
    }

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
        if (_popup is not null)
        {
            var p = _popup;
            _popup = null;
            try { p.Close(); } catch { }
        }
        _currentCallId = null;
    }

    public void Dispose()
    {
        Clear();
        _ring.Dispose();
    }
}
