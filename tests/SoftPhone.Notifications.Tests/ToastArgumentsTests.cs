using SoftPhone.Notifications;
using Xunit;

namespace SoftPhone.Notifications.Tests;

public class ToastArgumentsTests
{
    [Theory]
    [InlineData(ToastActionKind.Answer, "answer")]
    [InlineData(ToastActionKind.Decline, "decline")]
    [InlineData(ToastActionKind.Voicemail, "voicemail")]
    public void Build_then_Parse_roundtrips(ToastActionKind kind, string _)
    {
        var args = ToastArguments.Build(kind, "call-123");
        var parsed = ToastArguments.Parse(args);
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal("call-123", parsed.CallId);
        Assert.True(parsed.IsCallAction);
    }

    [Fact]
    public void Parse_handles_callId_needing_escaping()
    {
        var args = ToastArguments.ForAnswer("id with spaces & =weird");
        var parsed = ToastArguments.Parse(args);
        Assert.Equal(ToastActionKind.Answer, parsed.Kind);
        Assert.Equal("id with spaces & =weird", parsed.CallId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage-without-action")]
    [InlineData("action=bogus&callId=x")]
    public void Parse_unknown_or_empty_yields_unknown(string? args)
    {
        var parsed = ToastArguments.Parse(args);
        Assert.Equal(ToastActionKind.Unknown, parsed.Kind);
        Assert.False(parsed.IsCallAction);
    }

    [Fact]
    public void Answer_helper_encodes_action_answer()
    {
        Assert.Contains("action=answer", ToastArguments.ForAnswer("c1"));
        Assert.Contains("callId=c1", ToastArguments.ForAnswer("c1"));
    }
}
