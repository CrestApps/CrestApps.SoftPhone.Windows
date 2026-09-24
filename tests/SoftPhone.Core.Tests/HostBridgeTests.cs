using System.Text.Json;
using SoftPhone.Core.Contract;
using Xunit;

namespace SoftPhone.Core.Tests;

/// <summary>The WebView2 message shapes shared with the /softphone page's soft-phone/host-bridge.js.</summary>
public class HostBridgeTests
{
    [Fact]
    public void Parses_ready_only_for_a_supported_protocol()
    {
        Assert.Equal(new PageReadyMessage(1), HostBridge.ParsePageMessage("""{"type":"softphone-ready","protocol":1}"""));
        Assert.Null(HostBridge.ParsePageMessage("""{"type":"softphone-ready","protocol":0}"""));
        Assert.Null(HostBridge.ParsePageMessage("""{"type":"softphone-ready"}"""));
    }

    [Fact]
    public void Parses_an_incoming_call_with_its_matched_records()
    {
        var json = """
        {
          "type": "incoming-call", "protocol": 1, "callId": "v3:abc", "from": "+17025550100",
          "queue": "Support", "heading": "Matched customers", "canVoicemail": true,
          "cards": [{
            "id": "c1", "title": "Jane Doe", "subtitle": "+17025550100", "description": "",
            "badges": ["Support", 7], "url": "https://tenant.example.com/Admin/activity/1",
            "answerAndOpenText": "Answer & open activity", "openText": "Open activity",
            "links": [{ "text": "Customer record", "url": "https://tenant.example.com/Admin/contact/1" }, { "text": "none" }]
          }]
        }
        """;

        var message = Assert.IsType<PageIncomingCallMessage>(HostBridge.ParsePageMessage(json));

        Assert.Equal("v3:abc", message.Call.CallId);
        Assert.Equal("+17025550100", message.Call.From);
        Assert.True(ContractHelpers.IsRingingState(message.Call.State));
        Assert.True(message.CanVoicemail);
        Assert.Equal("Matched customers", message.Context.Heading);
        Assert.Equal("Support", message.Context.Properties!["queue"]);

        var card = Assert.Single(message.Context.Cards!);
        Assert.Equal("Jane Doe", card.Title);
        Assert.Null(card.Description);
        Assert.Equal(new[] { "Support" }, card.Badges);
        Assert.Equal("Answer & open activity", card.AnswerAndOpenText);
        Assert.Equal("Open activity", card.OpenText);
        var link = Assert.Single(card.Links!);
        Assert.Equal("Customer record", link.Text);
    }

    [Fact]
    public void Caps_the_number_of_matched_records()
    {
        var cards = string.Join(",", Enumerable.Range(0, HostBridge.MaxCards + 10).Select(i => $$"""{"title":"C{{i}}"}"""));
        var message = Assert.IsType<PageIncomingCallMessage>(
            HostBridge.ParsePageMessage($$"""{"type":"incoming-call","callId":"x","cards":[{{cards}}]}"""));

        Assert.Equal(HostBridge.MaxCards, message.Context.Cards!.Count);
    }

    [Theory]
    [InlineData("""{"type":"incoming-call"}""")]
    [InlineData("""{"type":"incoming-call-ended"}""")]
    [InlineData("""{"type":"dial","number":"100"}""")]
    [InlineData("""["incoming-call"]""")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_unknown_or_malformed_messages(string? json)
    {
        Assert.Null(HostBridge.ParsePageMessage(json));
    }

    [Fact]
    public void Parses_ended_and_action_result()
    {
        Assert.Equal(new PageIncomingCallEndedMessage("c1"), HostBridge.ParsePageMessage("""{"type":"incoming-call-ended","callId":"c1"}"""));
        Assert.Equal(new PageActionResultMessage("c1", "answer", true),
            HostBridge.ParsePageMessage("""{"type":"incoming-call-action-result","callId":"c1","action":"answer","handled":true}"""));
    }

    [Fact]
    public void Builds_the_host_messages_the_page_reads()
    {
        AssertJson("""{"type":"host-ready","protocol":1}""", HostBridge.HostReady());
        AssertJson("""{"type":"incoming-call-shown","callId":"c1","ringing":true}""", HostBridge.Shown("c1", ringing: true));
        AssertJson("""{"type":"incoming-call-dismissed","callId":"c1"}""", HostBridge.Dismissed("c1"));
        AssertJson("""{"type":"incoming-call-action","callId":"c1","action":"answer"}""", HostBridge.Action("c1", IncomingCallAction.Answer));
        AssertJson("""{"type":"incoming-call-action","callId":"c1","action":"decline"}""", HostBridge.Action("c1", IncomingCallAction.Decline));
        AssertJson("""{"type":"incoming-call-action","callId":"c1","action":"voicemail"}""", HostBridge.Action("c1", IncomingCallAction.Voicemail));
    }

    [Theory]
    [InlineData("https://tenant.example.com/Admin/x", "https://tenant.example.com", "https://tenant.example.com/Admin/x")]
    [InlineData("/Admin/Contents/ContentItems/abc/Edit", "https://tenant.example.com", "https://tenant.example.com/Admin/Contents/ContentItems/abc/Edit")]
    [InlineData("http://crm.example.com/a", "https://tenant.example.com", "http://crm.example.com/a")]
    [InlineData("javascript:alert(1)", "https://tenant.example.com", null)]
    [InlineData("file:///c:/windows/system32/calc.exe", "https://tenant.example.com", null)]
    [InlineData("ms-settings:privacy", "https://tenant.example.com", null)]
    [InlineData("/Admin/x", null, null)]
    [InlineData("", "https://tenant.example.com", null)]
    public void Resolves_only_web_urls(string url, string? origin, string? expected)
    {
        Assert.Equal(expected, HostBridge.ResolveWebUrl(url, origin)?.AbsoluteUri);
    }

    private static void AssertJson(string expected, string actual) =>
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(expected).RootElement, JsonDocument.Parse(actual).RootElement),
            $"Expected {expected} but got {actual}");
}
