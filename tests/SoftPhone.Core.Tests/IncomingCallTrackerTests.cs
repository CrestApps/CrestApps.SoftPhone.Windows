using System.Text.Json;
using SoftPhone.Core.Contract;
using SoftPhone.Core.Incoming;
using Xunit;

namespace SoftPhone.Core.Tests;

/// <summary>
/// The popup rules for the page handoff. Once the /softphone page hands a call to us it hides its
/// own modal, so a notification is a must: these pin down that nothing but the page ending the
/// call (or the agent acting) takes a handed-off call's popup away, and that the page hears about
/// every popup that is not on screen.
/// </summary>
public class IncomingCallTrackerTests
{
    private sealed class FakeSurface : IIncomingCallSurface
    {
        public bool IsPhoneWindowFocused { get; set; }
        public bool RingtoneEnabled { get; set; } = true;
        public HashSet<string> Open { get; } = new();
        public List<string> Updated { get; } = new();
        public List<string> Posted { get; } = new();
        public List<IncomingCallChoice> Carried { get; } = new();
        public bool Ringing { get; private set; }

        public void ShowPopup(RingingCall call) => Open.Add(call.CallId);
        public void UpdatePopup(RingingCall call) => Updated.Add(call.CallId);
        public void ClosePopup(string callId) => Open.Remove(callId);
        public void SetRinging(bool ringing) => Ringing = ringing;
        public void PostToPage(string json) => Posted.Add(json);
        public void Carry(IncomingCallChoice choice) => Carried.Add(choice);

        public IEnumerable<(string Type, string? CallId)> Messages => Posted.Select(json =>
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return (root.GetProperty("type").GetString()!,
                root.TryGetProperty("callId", out var id) ? id.GetString() : null);
        }).ToList();
    }

    private static Call RingingCall(string id) => new() { CallId = id, From = "+17025550100", State = "Ringing", Direction = "Inbound" };

    private static PageIncomingCallMessage PageCall(string id, params string[] cardTitles) => new(
        RingingCall(id),
        new CallContext { Cards = cardTitles.Select(t => new ContextCard { Title = t }).ToList() },
        CanVoicemail: true);

    private static (IncomingCallTracker Tracker, FakeSurface Surface) ReadyTracker()
    {
        var surface = new FakeSurface();
        var tracker = new IncomingCallTracker(surface);
        tracker.PageBridgeChanged(true);
        return (tracker, surface);
    }

    [Fact]
    public void Page_call_opens_popup_and_confirms_only_once_it_is_rendered()
    {
        var (tracker, surface) = ReadyTracker();

        tracker.Page(PageCall("c1"));

        Assert.Contains("c1", surface.Open);
        Assert.True(surface.Ringing);
        Assert.Empty(surface.Posted);

        tracker.PopupRendered("c1");

        Assert.Equal(new[] { ("incoming-call-shown", (string?)"c1") }, surface.Messages);
        Assert.Contains("\"ringing\":true", surface.Posted[0]);
    }

    [Fact]
    public void Confirmation_tells_the_page_when_the_app_does_not_ring()
    {
        var (tracker, surface) = ReadyTracker();
        surface.RingtoneEnabled = false;

        tracker.Page(PageCall("c1"));
        tracker.PopupRendered("c1");

        Assert.Contains("\"ringing\":false", surface.Posted.Single());
        Assert.False(surface.Ringing);
    }

    [Fact]
    public void Page_call_popup_shows_even_while_the_phone_window_is_focused()
    {
        var (tracker, surface) = ReadyTracker();
        surface.IsPhoneWindowFocused = true;

        tracker.Page(PageCall("c1"));
        tracker.Refresh();

        Assert.Contains("c1", surface.Open);
    }

    [Fact]
    public void Poll_finding_no_offer_does_not_close_a_page_call()
    {
        // A direct extension call rings only on the page; the Contact Center poll never sees it.
        var (tracker, surface) = ReadyTracker();

        tracker.Page(PageCall("browser-in-1"));
        tracker.BackgroundNoActiveOffer();

        Assert.Contains("browser-in-1", surface.Open);
        Assert.True(surface.Ringing);
    }

    [Fact]
    public void Background_state_change_does_not_close_a_page_call()
    {
        var (tracker, surface) = ReadyTracker();

        tracker.Page(PageCall("c1"));
        tracker.BackgroundStateChanged(new Call { CallId = "c1", State = "Connected" });

        Assert.Contains("c1", surface.Open);
    }

    [Fact]
    public void Page_ending_the_call_closes_the_popup_without_a_dismissal()
    {
        var (tracker, surface) = ReadyTracker();

        tracker.Page(PageCall("c1"));
        tracker.PopupRendered("c1");
        tracker.Page(new PageIncomingCallEndedMessage("c1"));

        Assert.Empty(surface.Open);
        Assert.False(surface.Ringing);
        Assert.DoesNotContain(surface.Messages, m => m.Type == "incoming-call-dismissed");
        Assert.Empty(tracker.Calls);
    }

    [Fact]
    public void Agent_closing_the_popup_tells_the_page_and_stops_the_app_ring()
    {
        var (tracker, surface) = ReadyTracker();

        tracker.Page(PageCall("c1"));
        tracker.PopupRendered("c1");
        surface.Open.Remove("c1"); // the window closed itself
        tracker.PopupClosedByAgent("c1");

        Assert.Contains(("incoming-call-dismissed", (string?)"c1"), surface.Messages);
        Assert.False(surface.Ringing);

        // It stays closed: the page shows its modal from here.
        tracker.Refresh();
        Assert.Empty(surface.Open);
    }

    [Fact]
    public void Page_reporting_a_call_the_agent_dismissed_gets_an_immediate_dismissal()
    {
        var (tracker, surface) = ReadyTracker();

        tracker.BackgroundIncoming(RingingCall("c1"), new CallContext());
        surface.Open.Remove("c1");
        tracker.PopupClosedByAgent("c1");
        tracker.Page(PageCall("c1"));

        Assert.Equal(new[] { ("incoming-call-dismissed", (string?)"c1") }, surface.Messages);
        Assert.Empty(surface.Open);
    }

    [Fact]
    public void Answer_for_a_page_call_goes_to_the_page()
    {
        var (tracker, surface) = ReadyTracker();

        tracker.Page(PageCall("c1"));
        var choice = tracker.Choose("c1", IncomingCallAction.Answer, "https://tenant.example.com/Admin/x");

        Assert.Equal(new IncomingCallChoice("c1", IncomingCallAction.Answer, IncomingCallRoute.Page, "https://tenant.example.com/Admin/x"), choice);
        Assert.Equal(choice, surface.Carried.Single());
        Assert.Empty(surface.Open);
        Assert.False(surface.Ringing);
    }

    [Fact]
    public void Decline_and_voicemail_for_a_page_call_go_to_the_page()
    {
        var (tracker, _) = ReadyTracker();

        tracker.Page(PageCall("c1"));
        tracker.Page(PageCall("c2"));

        Assert.Equal(IncomingCallRoute.Page, tracker.Choose("c1", IncomingCallAction.Decline)!.Route);
        Assert.Equal(IncomingCallRoute.Page, tracker.Choose("c2", IncomingCallAction.Voicemail)!.Route);
    }

    [Fact]
    public void Answer_never_reloads_a_page_that_takes_part()
    {
        // The call is known only to our background connection, but the page is loaded (possibly
        // with a live call): the answer is posted to it, never done by navigating.
        var (tracker, _) = ReadyTracker();

        tracker.BackgroundIncoming(RingingCall("c1"), new CallContext());

        Assert.Equal(IncomingCallRoute.Page, tracker.Choose("c1", IncomingCallAction.Answer)!.Route);
    }

    [Fact]
    public void Without_a_page_answer_reloads_and_decline_goes_to_the_server()
    {
        var surface = new FakeSurface();
        var tracker = new IncomingCallTracker(surface);

        tracker.BackgroundIncoming(RingingCall("c1"), new CallContext());
        tracker.BackgroundIncoming(RingingCall("c2"), new CallContext());

        Assert.Equal(IncomingCallRoute.ReloadPageToAnswer, tracker.Choose("c1", IncomingCallAction.Answer)!.Route);
        Assert.Equal(IncomingCallRoute.Server, tracker.Choose("c2", IncomingCallAction.Decline)!.Route);
    }

    [Fact]
    public void A_handled_call_does_not_come_back_when_the_page_or_poll_reports_it_again()
    {
        var (tracker, surface) = ReadyTracker();

        tracker.BackgroundIncoming(RingingCall("c1"), new CallContext());
        tracker.Choose("c1", IncomingCallAction.Answer);
        tracker.BackgroundIncoming(RingingCall("c1"), new CallContext());
        tracker.Page(PageCall("c1"));

        Assert.Empty(surface.Open);
        Assert.False(surface.Ringing);
        // Not confirmed, so if it still rings on the page, the page shows its own modal.
        Assert.Empty(surface.Posted);
    }

    [Fact]
    public void A_second_call_gets_its_own_popup()
    {
        var (tracker, surface) = ReadyTracker();

        tracker.Page(PageCall("c1"));
        tracker.BackgroundIncoming(RingingCall("c2"), new CallContext());

        Assert.Equal(new HashSet<string> { "c1", "c2" }, surface.Open);

        tracker.Choose("c1", IncomingCallAction.Answer);

        Assert.Equal(new HashSet<string> { "c2" }, surface.Open);
        Assert.True(surface.Ringing);
    }

    [Fact]
    public void Background_popup_is_confirmed_the_moment_the_page_claims_the_call()
    {
        var (tracker, surface) = ReadyTracker();

        tracker.BackgroundIncoming(RingingCall("c1"), new CallContext());
        tracker.PopupRendered("c1");

        Assert.Empty(surface.Posted); // the page has not asked yet

        tracker.Page(PageCall("c1", "Jane Doe"));

        Assert.Equal(new[] { ("incoming-call-shown", (string?)"c1") }, surface.Messages);
        Assert.Contains("c1", surface.Updated);
    }

    [Fact]
    public void Page_details_win_over_later_background_details()
    {
        var (tracker, surface) = ReadyTracker();

        tracker.Page(PageCall("c1", "Jane Doe"));
        tracker.BackgroundIncoming(RingingCall("c1"), new CallContext { Heading = "Other" });

        Assert.Equal("Jane Doe", tracker.Find("c1")!.Context.Cards![0].Title);
        Assert.Empty(surface.Updated);
    }

    [Fact]
    public void Repeated_identical_reports_do_not_rebuild_the_popup()
    {
        var (tracker, surface) = ReadyTracker();

        tracker.BackgroundIncoming(RingingCall("c1"), new CallContext());
        tracker.BackgroundIncoming(RingingCall("c1"), new CallContext());

        Assert.Empty(surface.Updated);
    }

    [Fact]
    public void Page_going_away_drops_calls_only_it_knew_and_keeps_offers_the_poll_sees()
    {
        var (tracker, surface) = ReadyTracker();

        tracker.Page(PageCall("browser-in-1"));
        tracker.BackgroundIncoming(RingingCall("c2"), new CallContext());
        tracker.Page(PageCall("c2"));

        tracker.PageBridgeChanged(false);

        Assert.Null(tracker.Find("browser-in-1"));
        Assert.False(tracker.Find("c2")!.FromPage);
        Assert.Contains("c2", surface.Open); // the phone window is not focused
    }

    [Fact]
    public void Without_a_page_the_popup_hides_while_the_phone_window_is_focused()
    {
        // An older server shows its own modal while the window is focused (the pre-handoff rule).
        var surface = new FakeSurface { IsPhoneWindowFocused = true };
        var tracker = new IncomingCallTracker(surface);

        tracker.BackgroundIncoming(RingingCall("c1"), new CallContext());

        Assert.Empty(surface.Open);
        Assert.True(surface.Ringing);

        surface.IsPhoneWindowFocused = false;
        tracker.Refresh();

        Assert.Contains("c1", surface.Open);
    }

    [Fact]
    public void Poll_finding_no_offer_clears_background_calls_but_not_simulated_ones()
    {
        var surface = new FakeSurface();
        var tracker = new IncomingCallTracker(surface);

        tracker.BackgroundIncoming(RingingCall("c1"), new CallContext());
        tracker.BackgroundIncoming(RingingCall("SIM-1"), new CallContext());
        tracker.BackgroundNoActiveOffer();

        Assert.Equal(new HashSet<string> { "SIM-1" }, surface.Open);
    }

    [Fact]
    public void Background_state_change_clears_a_background_call()
    {
        var surface = new FakeSurface();
        var tracker = new IncomingCallTracker(surface);

        tracker.BackgroundIncoming(RingingCall("c1"), new CallContext());
        tracker.BackgroundStateChanged(new Call { CallId = "c1", State = "Ringing" });

        Assert.Contains("c1", surface.Open);

        tracker.BackgroundStateChanged(new Call { CallId = "c1", State = "Connected" });

        Assert.Empty(surface.Open);
        Assert.False(surface.Ringing);
    }

    [Fact]
    public void Answered_elsewhere_closes_a_background_popup_by_call_id_or_offer_id()
    {
        var surface = new FakeSurface();
        var tracker = new IncomingCallTracker(surface);

        tracker.BackgroundIncoming(RingingCall("c1"), new CallContext());
        tracker.BackgroundIncoming(RingingCall("c2"), new CallContext { Properties = new() { ["reservationId"] = "res-2" } });
        tracker.BackgroundIncoming(RingingCall("c3"), new CallContext());

        tracker.BackgroundCallAnswered("c1", null);
        tracker.BackgroundCallAnswered("other", "res-2");

        Assert.Equal(new HashSet<string> { "c3" }, surface.Open);
    }

    [Fact]
    public void Answered_elsewhere_leaves_a_page_call_to_the_page()
    {
        // The page hears the same event and clears the call (or finishes an accept it made itself).
        var (tracker, surface) = ReadyTracker();

        tracker.Page(PageCall("c1"));
        tracker.BackgroundCallAnswered("c1", null);

        Assert.Contains("c1", surface.Open);

        tracker.Page(new PageIncomingCallEndedMessage("c1"));

        Assert.Empty(surface.Open);
    }

    [Fact]
    public void Expired_offer_closes_a_background_popup()
    {
        var surface = new FakeSurface();
        var tracker = new IncomingCallTracker(surface);
        var expires = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

        tracker.BackgroundIncoming(RingingCall("c1"), new CallContext { Properties = new() { ["expiresUtc"] = expires.ToString("O") } });
        tracker.BackgroundIncoming(RingingCall("c2"), new CallContext { Properties = new() { ["expiresUtc"] = "not a date" } });
        tracker.BackgroundIncoming(RingingCall("c3"), new CallContext());

        tracker.ExpireDue(expires);

        Assert.Equal(new HashSet<string> { "c1", "c2", "c3" }, surface.Open);

        tracker.ExpireDue(expires + IncomingCallTracker.ExpiryGrace + TimeSpan.FromMilliseconds(1));

        Assert.Equal(new HashSet<string> { "c2", "c3" }, surface.Open);
    }

    [Fact]
    public void Expiry_leaves_a_page_call_to_the_page()
    {
        var (tracker, surface) = ReadyTracker();
        var expires = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var message = new PageIncomingCallMessage(
            RingingCall("c1"),
            new CallContext { Properties = new() { ["expiresUtc"] = expires.ToString("O") } },
            CanVoicemail: true);

        tracker.Page(message);
        tracker.ExpireDue(expires.AddMinutes(1));

        Assert.Contains("c1", surface.Open);
    }

    [Fact]
    public void Page_messages_are_ignored_until_the_page_takes_part()
    {
        var surface = new FakeSurface();
        var tracker = new IncomingCallTracker(surface);

        tracker.Page(PageCall("c1"));

        Assert.Empty(surface.Open);
        Assert.Null(tracker.Find("c1"));
    }
}
