using System.Xml.Linq;
using SoftPhone.Notifications;
using Xunit;

namespace SoftPhone.Notifications.Tests;

public class IncomingCallToastTests
{
    private static readonly IncomingCallToastModel Model = new()
    {
        CallId = "call-123",
        CallerName = "Maya Rodriguez",
        FromNumber = "+1 555 111 2222",
        QueueName = "Support",
    };

    [Fact]
    public void BuildXml_is_wellformed_and_uses_incomingCall_scenario()
    {
        var xml = IncomingCallToast.BuildXml(Model);
        var doc = XDocument.Parse(xml); // throws if malformed
        var toast = doc.Root!;
        Assert.Equal("toast", toast.Name.LocalName);
        Assert.Equal("incomingCall", toast.Attribute("scenario")!.Value);
    }

    [Fact]
    public void BuildXml_body_click_launches_answer()
    {
        var xml = IncomingCallToast.BuildXml(Model);
        var launch = XDocument.Parse(xml).Root!.Attribute("launch")!.Value;
        var parsed = ToastArguments.Parse(launch);
        Assert.Equal(ToastActionKind.Answer, parsed.Kind);
        Assert.Equal("call-123", parsed.CallId);
    }

    [Fact]
    public void BuildXml_has_answer_decline_voicemail_buttons_with_correct_args()
    {
        var xml = IncomingCallToast.BuildXml(Model);
        var actions = XDocument.Parse(xml).Descendants("action").ToList();
        Assert.Equal(3, actions.Count);

        var kinds = actions
            .Select(a => ToastArguments.Parse(a.Attribute("arguments")!.Value).Kind)
            .ToList();
        Assert.Contains(ToastActionKind.Answer, kinds);
        Assert.Contains(ToastActionKind.Decline, kinds);
        Assert.Contains(ToastActionKind.Voicemail, kinds);

        // Answer is foreground (opens the media leg); decline/voicemail are background.
        var answer = actions.First(a => a.Attribute("content")!.Value == "Answer");
        Assert.Equal("foreground", answer.Attribute("activationType")!.Value);
        var decline = actions.First(a => a.Attribute("content")!.Value == "Decline");
        Assert.Equal("background", decline.Attribute("activationType")!.Value);
    }

    [Fact]
    public void BuildXml_shows_caller_name_and_queue()
    {
        var xml = IncomingCallToast.BuildXml(Model);
        var texts = XDocument.Parse(xml).Descendants("text").Select(t => t.Value).ToList();
        Assert.Contains("Maya Rodriguez", texts);
        Assert.Contains(texts, t => t.Contains("Support"));
    }

    [Fact]
    public void BuildXml_tolerates_missing_optional_fields()
    {
        var xml = IncomingCallToast.BuildXml(new IncomingCallToastModel { CallId = "c", CallerName = "Unknown" });
        var doc = XDocument.Parse(xml);
        Assert.NotNull(doc.Root);
    }
}
