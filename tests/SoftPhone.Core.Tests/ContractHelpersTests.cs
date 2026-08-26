using System.Text.Json;
using SoftPhone.Core.Contract;
using Xunit;

namespace SoftPhone.Core.Tests;

public class ContractHelpersTests
{
    [Theory]
    [InlineData("Ringing", true)]
    [InlineData("incoming", true)]
    [InlineData("OFFER", true)]
    [InlineData("alerting", true)]
    [InlineData("pending", true)]
    [InlineData("Connected", false)]
    [InlineData("Ended", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsRingingState_matches_by_keyword(string? state, bool expected)
    {
        Assert.Equal(expected, ContractHelpers.IsRingingState(state));
    }

    [Fact]
    public void CallerDisplayName_prefers_heading_then_first_card_then_fallback()
    {
        Assert.Equal("Maya", ContractHelpers.CallerDisplayName(new CallContext { Heading = "Maya" }, "fb"));
        Assert.Equal("Card One", ContractHelpers.CallerDisplayName(
            new CallContext { Cards = new() { new ContextCard { Title = "Card One" } } }, "fb"));
        Assert.Equal("fb", ContractHelpers.CallerDisplayName(new CallContext(), "fb"));
        Assert.Equal("fb", ContractHelpers.CallerDisplayName(null, "fb"));
    }

    [Fact]
    public void ScreenPopTarget_returns_first_card_with_url()
    {
        var ctx = new CallContext
        {
            Cards = new()
            {
                new ContextCard { Title = "no url" },
                new ContextCard { Title = "has url", Url = "https://x/y", OpenInNewTab = true },
            },
        };
        var target = ContractHelpers.ScreenPopTargetOf(ctx);
        Assert.NotNull(target);
        Assert.Equal("https://x/y", target!.Value.Url);
        Assert.True(target.Value.OpenInNewTab);

        Assert.Null(ContractHelpers.ScreenPopTargetOf(new CallContext()));
    }

    [Fact]
    public void ExtensionConfig_binds_from_camelCase_json()
    {
        const string json = """
        {"hubUrl":"https://t/hub","currentIncomingOfferUrl":"https://t/offer","softPhoneUrl":"https://t/softphone","displayName":"Maya Rodriguez","userId":"u1"}
        """;
        var cfg = JsonSerializer.Deserialize<ExtensionConfig>(json, ContractHelpers.Json)!;
        Assert.Equal("https://t/hub", cfg.HubUrl);
        Assert.Equal("https://t/offer", cfg.CurrentIncomingOfferUrl);
        Assert.Equal("Maya Rodriguez", cfg.DisplayName);
    }

    [Fact]
    public void Call_binds_from_PascalCase_json()
    {
        const string json = """
        {"CallId":"c1","From":"+15551112222","To":"+15553334444","State":"Ringing","Direction":"Inbound","ProviderName":"Acme"}
        """;
        var call = JsonSerializer.Deserialize<Call>(json, ContractHelpers.Json)!;
        Assert.Equal("c1", call.CallId);
        Assert.Equal("+15551112222", call.From);
        Assert.Equal("Ringing", call.State);
    }
}
