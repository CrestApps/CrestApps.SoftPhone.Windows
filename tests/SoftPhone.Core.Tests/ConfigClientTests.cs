using System.Net;
using System.Text;
using SoftPhone.Core.Config;
using Xunit;

namespace SoftPhone.Core.Tests;

public class ConfigClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public HttpRequestMessage? LastRequest { get; private set; }
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_responder(request));
        }
    }

    private static ConfigClient ClientReturning(HttpStatusCode status, string? body = null, string contentType = "application/json")
    {
        var handler = new StubHandler(_ =>
        {
            var res = new HttpResponseMessage(status);
            if (body is not null) res.Content = new StringContent(body, Encoding.UTF8, contentType);
            return res;
        });
        return new ConfigClient(new HttpClient(handler));
    }

    [Fact]
    public async Task Fetch_returns_config_on_200()
    {
        const string body = """
        {"hubUrl":"https://t/hub","currentIncomingOfferUrl":"https://t/offer","softPhoneUrl":"https://t/softphone","displayName":"Maya","userId":"u1"}
        """;
        var client = ClientReturning(HttpStatusCode.OK, body);
        var cfg = await client.FetchExtensionConfigAsync("t.example.com");
        Assert.Equal("https://t/hub", cfg.HubUrl);
    }

    [Fact]
    public async Task Fetch_maps_302_to_unauthenticated()
    {
        var client = ClientReturning(HttpStatusCode.Redirect);
        var ex = await Assert.ThrowsAsync<ConfigException>(() => client.FetchExtensionConfigAsync("t.example.com"));
        Assert.Equal(ConfigErrorKind.Unauthenticated, ex.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Fetch_maps_401_403_to_unauthenticated(HttpStatusCode status)
    {
        var client = ClientReturning(status);
        var ex = await Assert.ThrowsAsync<ConfigException>(() => client.FetchExtensionConfigAsync("t.example.com"));
        Assert.Equal(ConfigErrorKind.Unauthenticated, ex.Kind);
    }

    [Fact]
    public async Task Fetch_maps_404_to_not_enabled()
    {
        var client = ClientReturning(HttpStatusCode.NotFound);
        var ex = await Assert.ThrowsAsync<ConfigException>(() => client.FetchExtensionConfigAsync("t.example.com"));
        Assert.Equal(ConfigErrorKind.NotEnabled, ex.Kind);
    }

    [Fact]
    public async Task Fetch_maps_missing_fields_to_bad_response()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{"displayName":"Maya"}""");
        var ex = await Assert.ThrowsAsync<ConfigException>(() => client.FetchExtensionConfigAsync("t.example.com"));
        Assert.Equal(ConfigErrorKind.BadResponse, ex.Kind);
    }

    [Fact]
    public async Task Fetch_targets_the_extension_config_url()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new ConfigClient(new HttpClient(handler));
        await Assert.ThrowsAsync<ConfigException>(() => client.FetchExtensionConfigAsync("Phone.Example.com/x"));
        Assert.Equal("https://phone.example.com/softphone/extension-config", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task CurrentOffer_returns_null_on_404()
    {
        var client = ClientReturning(HttpStatusCode.NotFound);
        var offer = await client.FetchCurrentOfferAsync("https://t/offer");
        Assert.Null(offer);
    }

    [Fact]
    public async Task CurrentOffer_parses_offer_on_200()
    {
        const string body = """
        {"Call":{"CallId":"c1","From":"+1","To":"+2","State":"Ringing","Direction":"Inbound","ProviderName":"Acme"},"Context":{"Heading":"Maya"},"ExpiresUtc":"2026-01-01T00:00:00Z","ServerTimeUtc":"2026-01-01T00:00:00Z"}
        """;
        var client = ClientReturning(HttpStatusCode.OK, body);
        var offer = await client.FetchCurrentOfferAsync("https://t/offer");
        Assert.NotNull(offer);
        Assert.Equal("c1", offer!.Call.CallId);
        Assert.Equal("Maya", offer.Context.Heading);
    }
}
