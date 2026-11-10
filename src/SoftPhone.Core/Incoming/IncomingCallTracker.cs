using System.Text.Json;
using SoftPhone.Core.Contract;

namespace SoftPhone.Core.Incoming;

/// <summary>Where an agent's choice in the popup is carried out.</summary>
public enum IncomingCallRoute
{
    /// <summary>Post the action to the loaded /softphone page, which holds the call audio and the offer.</summary>
    Page,

    /// <summary>No page to talk to: load /softphone?answerCallId= so the page answers on load.</summary>
    ReloadPageToAnswer,

    /// <summary>No page owns the call: act on the parked call through the hub (Reject / Voicemail).</summary>
    Server,
}

/// <summary>An agent's choice in the popup, and where to carry it out.</summary>
/// <param name="OpenUrl">An absolute web URL to open as well ("Answer &amp; open"), or null.</param>
public sealed record IncomingCallChoice(string CallId, IncomingCallAction Action, IncomingCallRoute Route, string? OpenUrl);

/// <summary>The UI side the tracker drives. Implemented by the app's WPF coordinator.</summary>
public interface IIncomingCallSurface
{
    bool IsPhoneWindowFocused { get; }
    bool RingtoneEnabled { get; }

    /// <summary>Open the popup for the call. Report <see cref="IncomingCallTracker.PopupRendered"/> once it is on screen.</summary>
    void ShowPopup(RingingCall call);

    /// <summary>The call's caller details or matched records changed while its popup is open.</summary>
    void UpdatePopup(RingingCall call);

    /// <summary>Close the call's popup. Must not report it as closed by the agent.</summary>
    void ClosePopup(string callId);

    void SetRinging(bool ringing);
    void PostToPage(string json);
    void Carry(IncomingCallChoice choice);
}

/// <summary>A call this app is (or was) notifying for.</summary>
public sealed class RingingCall
{
    internal RingingCall(Call call, CallContext context)
    {
        CallId = call.CallId;
        Call = call;
        Context = context;
    }

    public string CallId { get; }
    public Call Call { get; internal set; }
    public CallContext Context { get; internal set; }
    public bool CanVoicemail { get; internal set; } = true;

    /// <summary>The loaded /softphone page reported this call and waits for our confirmation.</summary>
    public bool FromPage { get; internal set; }

    /// <summary>Our own background connection (hub IncomingCall or the current-offer poll) reported it.</summary>
    public bool FromBackground { get; internal set; }

    /// <summary>The agent chose an action; nothing is shown until the call ends.</summary>
    public bool Handled { get; internal set; }

    /// <summary>The agent closed the popup without an action; it is not reopened.</summary>
    public bool Dismissed { get; internal set; }

    public bool IsSimulated => CallId.StartsWith("SIM-", StringComparison.Ordinal);

    /// <summary>
    /// When the server said the offer stops ringing (<c>expiresUtc</c> from our background connection),
    /// kept even once the page owns the call and its details win.
    /// </summary>
    public DateTimeOffset? OfferExpiresUtc { get; internal set; }

    /// <summary>The offer's reservation id from our background connection, kept like <see cref="OfferExpiresUtc"/>.</summary>
    public string? OfferId { get; internal set; }

    internal bool PopupOpen;
    internal bool PopupRendered;
    internal bool Confirmed;
    internal string Signature = "";
}

/// <summary>
/// Decides, for every ringing call, whether the popup shows, whether the app rings, what the
/// /softphone page is told, and where the agent's choice goes. UI-free so the rules are unit-tested;
/// call it on the UI thread only.
///
/// The rule that matters: once the page hands a call to us it hides its own modal, so a call the
/// page reported keeps its popup until the page itself says the call stopped ringing — not when
/// the phone window gains focus, and not when our background poll finds no Contact Center offer.
/// We confirm to the page only after the popup is rendered, and tell it the moment the popup is
/// closed without an answer so it shows its modal again.
///
/// A person takes one call at a time, so at most one call rings in a popup. While the page takes
/// part, it is the one that decides which call that is: it reports a single call at a time (and
/// refuses an offer while the agent is on a call), so only the call it reports now gets a popup.
/// A call only our background connection heard about waits, hidden, for the page to report it.
/// Two exceptions keep a popup from outliving its call when the page never ends it: the server
/// saying the call was answered elsewhere closes it, and so does the offer's expiry passing.
/// </summary>
public sealed class IncomingCallTracker
{
    private readonly IIncomingCallSurface _surface;
    private readonly Action<string>? _log;
    private readonly List<RingingCall> _calls = new();

    // The call the page reports now. The page reports one call at a time.
    private string? _pageCallId;

    // Calls answered on another of the agent's phones, by call id: the offer id the answer named and when.
    private readonly Dictionary<string, (string? OfferId, DateTimeOffset At)> _answeredElsewhere = new();
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>How long a call answered elsewhere is kept from popping again when the page reports it ringing.</summary>
    public static readonly TimeSpan AnsweredElsewhereMemory = TimeSpan.FromMinutes(2);

    public IncomingCallTracker(IIncomingCallSurface surface, Action<string>? log = null, Func<DateTimeOffset>? clock = null)
    {
        _surface = surface;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>True while the loaded page takes part in the handoff (it answered our host-ready).</summary>
    public bool PageReady { get; private set; }

    public IReadOnlyList<RingingCall> Calls => _calls;

    public RingingCall? Find(string? callId) =>
        callId is null ? null : _calls.FirstOrDefault(c => c.CallId == callId);

    // ---------------------------------------------------------------- background connection

    /// <summary>Hub <c>IncomingCall</c> or a current-offer poll hit.</summary>
    public void BackgroundIncoming(Call call, CallContext? context)
    {
        if (call is null || string.IsNullOrEmpty(call.CallId)) return;

        // The server offering the call now, under an offer other than the one answered elsewhere, is a new offer.
        var reportedOfferId = context?.Properties is { } offered && offered.TryGetValue("reservationId", out var id) ? id : null;
        if (_answeredElsewhere.TryGetValue(call.CallId, out var answered)
            && !string.IsNullOrEmpty(reportedOfferId) && reportedOfferId != answered.OfferId)
        {
            _answeredElsewhere.Remove(call.CallId);
        }

        var ringing = Find(call.CallId) ?? Add(call, context ?? new CallContext());
        ringing.FromBackground = true;

        if (IsAnsweredElsewhere(call.CallId))
            ringing.Handled = true;

        if (ExpiryOf(context) is { } expires)
            ringing.OfferExpiresUtc = expires;

        if (context?.Properties is { } props && props.TryGetValue("reservationId", out var offerId) && !string.IsNullOrEmpty(offerId))
            ringing.OfferId = offerId;

        // Once the page owns the call, its details win (it filters the tenant's own number, etc.).
        if (!ringing.FromPage && !ringing.Handled && !ringing.Dismissed)
            SetDetails(ringing, call, context ?? new CallContext());

        Refresh();
    }

    public void BackgroundStateChanged(Call call)
    {
        var ringing = Find(call?.CallId);
        if (ringing is null || ContractHelpers.IsRingingState(call!.State)) return;

        // The page decides when a call it reported stops ringing; it hears the same state change.
        if (ringing.FromPage)
        {
            ringing.FromBackground = false;
            return;
        }

        Remove(ringing);
    }

    /// <summary>
    /// The current-offer poll found no Contact Center offer. That poll never sees a call the page rang
    /// for on its own (a direct extension call), so it only clears calls the page did not report.
    /// </summary>
    public void BackgroundNoActiveOffer()
    {
        foreach (var ringing in _calls.ToList())
        {
            if (ringing.FromPage)
                ringing.FromBackground = false;
            else if (!ringing.IsSimulated)
                Remove(ringing);
        }
    }

    /// <summary>
    /// The server says the call was answered on another of the user's soft phones (hub
    /// <c>IncomingCallAnswered</c>), matched by call id or by the offer's reservation id.
    /// </summary>
    public void BackgroundCallAnswered(string? callId, string? offerId)
    {
        // The platform reports the call ringing while it connects the phone that answered, and the page may report
        // it ringing again: remembered, so that does not pop a popup that would outlive the call.
        if (!string.IsNullOrEmpty(callId))
            RememberAnsweredElsewhere(callId, offerId);

        foreach (var ringing in _calls.ToList())
        {
            var matches = (!string.IsNullOrEmpty(callId) && ringing.CallId == callId)
                || (!string.IsNullOrEmpty(offerId)
                    && (PropertyOf(ringing, "reservationId") == offerId || ringing.OfferId == offerId));
            if (!matches) continue;

            RememberAnsweredElsewhere(ringing.CallId, offerId);

            if (!ringing.FromPage)
            {
                Remove(ringing);
                continue;
            }

            // The page hears the same event and ends the call itself, or finishes the accept it made. Its
            // popup closes now all the same: the call was answered, so there is nothing left to ask, and a
            // page that never ends it (asleep, or it missed the event) must not leave a ringing popup up
            // for a call the agent is already talking on. The call is kept, handled, until the page ends it.
            ringing.FromBackground = false;
            if (!ringing.Handled)
            {
                _log?.Invoke($"Call {ringing.CallId} was answered elsewhere.");
                ringing.Handled = true;
                ClosePopup(ringing);
                Refresh();
            }
        }
    }

    /// <summary>
    /// Drop background calls whose offer reservation has expired (<c>expiresUtc</c>), the same moment
    /// the page gives up on it, instead of waiting for the next current-offer poll. A call the page
    /// reported is the page's to end, but only for <see cref="PageExpiryGrace"/> past the expiry our
    /// background connection heard: a page that never ends it must not leave its popup up for good.
    /// </summary>
    public void ExpireDue(DateTimeOffset now)
    {
        foreach (var ringing in _calls.ToList())
        {
            if (ringing.IsSimulated) continue;

            if (ringing.FromPage)
            {
                if (ringing.Handled || ringing.OfferExpiresUtc is not { } pageExpires || now <= pageExpires + PageExpiryGrace)
                    continue;

                _log?.Invoke($"Offer {ringing.CallId} expired and the page never ended it.");

                // Its modal was hidden on our word: let the page show it again if it still rings there.
                if (PageReady && !ringing.Dismissed)
                    _surface.PostToPage(HostBridge.Dismissed(ringing.CallId));
                Remove(ringing);
                continue;
            }

            if ((ExpiryOf(ringing.Context) ?? ringing.OfferExpiresUtc) is { } expires && now > expires + ExpiryGrace)
            {
                _log?.Invoke($"Offer {ringing.CallId} expired.");
                Remove(ringing);
            }
        }
    }

    /// <summary>Matches the page's own margin past <c>expiresUtc</c> before it clears an offer.</summary>
    public static readonly TimeSpan ExpiryGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long past the offer's expiry a call the page reported may stay when the page never ends it.
    /// Longer than <see cref="ExpiryGrace"/> so the page, which ends it itself, always goes first.
    /// </summary>
    public static readonly TimeSpan PageExpiryGrace = TimeSpan.FromSeconds(5);

    private void RememberAnsweredElsewhere(string callId, string? offerId)
    {
        var now = _clock();
        foreach (var stale in _answeredElsewhere.Where(e => now - e.Value.At > AnsweredElsewhereMemory).Select(e => e.Key).ToList())
            _answeredElsewhere.Remove(stale);

        _answeredElsewhere[callId] = (string.IsNullOrEmpty(offerId) ? null : offerId, now);
    }

    private bool IsAnsweredElsewhere(string callId) =>
        _answeredElsewhere.TryGetValue(callId, out var answered) && _clock() - answered.At <= AnsweredElsewhereMemory;

    private static DateTimeOffset? ExpiryOf(CallContext? context) =>
        context?.Properties is { } props && props.TryGetValue("expiresUtc", out var raw) && !string.IsNullOrEmpty(raw)
            && DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var expires)
            ? expires
            : null;

    private static string? PropertyOf(RingingCall ringing, string key) =>
        ringing.Context.Properties is { } props && props.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;

    // ---------------------------------------------------------------- the /softphone page

    /// <summary>The page answered host-ready (true), or navigated away / crashed (false).</summary>
    public void PageBridgeChanged(bool ready)
    {
        if (PageReady == ready) return;
        PageReady = ready;
        _log?.Invoke(ready ? "Page bridge ready." : "Page bridge gone.");

        if (!ready)
        {
            // The page that rang these calls is gone. A Contact Center offer that still rings comes
            // back through our own poll; a call only the page knew about ended with it.
            _pageCallId = null;
            foreach (var ringing in _calls.ToList())
            {
                if (!ringing.FromPage) continue;
                ringing.FromPage = false;
                ringing.Confirmed = false;
                if (!ringing.FromBackground) Remove(ringing);
            }
        }

        Refresh();
    }

    public void Page(PageMessage message)
    {
        switch (message)
        {
            case PageIncomingCallMessage incoming:
                PageIncoming(incoming);
                break;

            case PageIncomingCallEndedMessage ended:
                if (Find(ended.CallId) is { } done)
                {
                    _log?.Invoke($"Page: call {ended.CallId} no longer rings.");
                    Remove(done);
                }
                break;

            case PageActionResultMessage result:
                _log?.Invoke($"Page: {result.Action} for {result.CallId} {(result.Handled ? "ran" : "was not run (no such ringing call on the page)")}.");
                break;
        }
    }

    private void PageIncoming(PageIncomingCallMessage message)
    {
        if (!PageReady) return;

        // The page reports one call at a time and ends the last one before it reports the next, so a
        // different call still on record as the page's is one whose end we missed: it is over too.
        if (_pageCallId is not null && _pageCallId != message.Call.CallId && Find(_pageCallId) is { } previous)
        {
            _log?.Invoke($"Page: call {previous.CallId} replaced by {message.Call.CallId}.");
            Remove(previous);
        }

        _pageCallId = message.Call.CallId;

        var ringing = Find(message.Call.CallId) ?? Add(message.Call, message.Context);
        ringing.FromPage = true;

        if (IsAnsweredElsewhere(ringing.CallId) && !ringing.Handled)
        {
            _log?.Invoke($"Page: call {ringing.CallId} reported ringing after it was answered elsewhere; no popup.");
            ringing.Handled = true;
            Refresh();
        }

        // The agent already chose for this call here (e.g. answered it from a background popup): the
        // page finishes that. We do not confirm, so if it still rings the page shows its own modal.
        if (ringing.Handled) return;

        // The agent closed our popup for it: let the page show its modal right away.
        if (ringing.Dismissed)
        {
            _surface.PostToPage(HostBridge.Dismissed(ringing.CallId));
            return;
        }

        ringing.CanVoicemail = message.CanVoicemail;
        SetDetails(ringing, message.Call, message.Context);
        Refresh();
        Confirm(ringing);
    }

    // ---------------------------------------------------------------- popup

    /// <summary>The call's popup is on screen (WPF ContentRendered).</summary>
    public void PopupRendered(string callId)
    {
        var ringing = Find(callId);
        if (ringing is null || !ringing.PopupOpen) return;
        ringing.PopupRendered = true;
        Confirm(ringing);
    }

    /// <summary>The agent closed the popup without answering (e.g. Alt+F4).</summary>
    public void PopupClosedByAgent(string callId)
    {
        var ringing = Find(callId);
        if (ringing is null || !ringing.PopupOpen) return;

        ringing.PopupOpen = false;
        ringing.PopupRendered = false;
        ringing.Confirmed = false;
        ringing.Dismissed = true;
        if (ringing.FromPage && PageReady)
            _surface.PostToPage(HostBridge.Dismissed(ringing.CallId));
        Refresh();
    }

    /// <summary>The agent chose Answer / Decline / Voicemail (optionally "Answer &amp; open").</summary>
    public IncomingCallChoice? Choose(string callId, IncomingCallAction action, string? openUrl = null)
    {
        var ringing = Find(callId);
        if (ringing is null || ringing.Handled) return null;

        ringing.Handled = true;
        ClosePopup(ringing);

        IncomingCallRoute route;
        if (action == IncomingCallAction.Answer)
            // Never reload a loaded page to answer: that would drop any live call on it. A page that
            // has not surfaced this call yet answers it the moment it arrives.
            route = PageReady ? IncomingCallRoute.Page : IncomingCallRoute.ReloadPageToAnswer;
        else
            // The page declines a Contact Center offer through its reservation endpoint, which only
            // the page can call; a call the page does not have is rejected through the hub.
            route = ringing.FromPage && PageReady ? IncomingCallRoute.Page : IncomingCallRoute.Server;

        var choice = new IncomingCallChoice(callId, action, route, openUrl);
        _surface.Carry(choice);

        if (ringing.IsSimulated) Remove(ringing); // nothing else ends a simulated call
        Refresh();
        return choice;
    }

    /// <summary>Close every popup and stop ringing (app exit).</summary>
    public void Clear()
    {
        foreach (var ringing in _calls.ToList()) Remove(ringing);
    }

    /// <summary>Re-apply the show/hide rules (e.g. the phone window gained or lost focus).</summary>
    public void Refresh()
    {
        foreach (var ringing in _calls)
        {
            // With the page taking part, the popup is the only prompt the page will not duplicate, so it
            // shows regardless of focus. Without it (older server), the page shows its own modal while the
            // phone window is focused, so the popup stays out of the way there.
            var wanted = Rings(ringing) && (PageReady || !_surface.IsPhoneWindowFocused);

            if (wanted && !ringing.PopupOpen)
            {
                ringing.PopupOpen = true;
                ringing.PopupRendered = false;
                ringing.Confirmed = false;
                _surface.ShowPopup(ringing);
            }
            else if (!wanted && ringing.PopupOpen)
            {
                ClosePopup(ringing);
            }
        }

        _surface.SetRinging(_surface.RingtoneEnabled && _calls.Any(Rings));
    }

    // Whether the call is one the agent is being asked to take. With the page taking part, that is only
    // the call the page reports now: one call rings at a time, and the page knows which (it refuses an
    // offer while the agent is on a call). A simulated call is the app's own test and always rings.
    private bool Rings(RingingCall ringing) =>
        !ringing.Handled && !ringing.Dismissed
        && (!PageReady || ringing.IsSimulated || (ringing.FromPage && ringing.CallId == _pageCallId));

    private RingingCall Add(Call call, CallContext context)
    {
        var ringing = new RingingCall(call, context) { Signature = SignatureOf(call, context) };
        _calls.Add(ringing);
        _log?.Invoke($"Incoming call {call.CallId} from {call.From}");
        return ringing;
    }

    private void SetDetails(RingingCall ringing, Call call, CallContext context)
    {
        var signature = SignatureOf(call, context);
        if (signature == ringing.Signature) return;
        ringing.Signature = signature;
        ringing.Call = call;
        ringing.Context = context;
        if (ringing.PopupOpen) _surface.UpdatePopup(ringing);
    }

    private void Confirm(RingingCall ringing)
    {
        if (!ringing.FromPage || !PageReady || !ringing.PopupOpen || !ringing.PopupRendered || ringing.Confirmed) return;
        ringing.Confirmed = true;
        _surface.PostToPage(HostBridge.Shown(ringing.CallId, _surface.RingtoneEnabled));
    }

    private void ClosePopup(RingingCall ringing)
    {
        if (!ringing.PopupOpen) return;
        ringing.PopupOpen = false;
        ringing.PopupRendered = false;
        ringing.Confirmed = false;
        _surface.ClosePopup(ringing.CallId);
    }

    private void Remove(RingingCall ringing)
    {
        ClosePopup(ringing);
        _calls.Remove(ringing);
        if (ringing.CallId == _pageCallId) _pageCallId = null;
        Refresh();
    }

    private static string SignatureOf(Call call, CallContext context) =>
        JsonSerializer.Serialize(new { call.From, context }, ContractHelpers.Json);
}
