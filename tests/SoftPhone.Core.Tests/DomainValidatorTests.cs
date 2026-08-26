using System.Net;
using System.Text;
using SoftPhone.Core.Config;
using Xunit;

namespace SoftPhone.Core.Tests;

public class DomainValidatorTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string? _body;
        public StubHandler(HttpStatusCode status, string? body = null) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var res = new HttpResponseMessage(_status);
            if (_body is not null) res.Content = new StringContent(_body, Encoding.UTF8, "application/json");
            return Task.FromResult(res);
        }
    }

    private static ConfigClient Client(HttpStatusCode status, string? body = null) =>
        new(new HttpClient(new StubHandler(status, body)));

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Unauthenticated_is_valid(HttpStatusCode status)
    {
        Assert.Equal(DomainValidationResult.Valid,
            await DomainValidator.ValidateAsync("t.example.com", Client(status)));
    }

    [Fact]
    public async Task Success_is_valid()
    {
        const string body = """{"hubUrl":"https://t/h","currentIncomingOfferUrl":"https://t/o","softPhoneUrl":"https://t/s","displayName":"m","userId":"u"}""";
        Assert.Equal(DomainValidationResult.Valid,
            await DomainValidator.ValidateAsync("t.example.com", Client(HttpStatusCode.OK, body)));
    }

    [Fact]
    public async Task NotFound_is_feature_not_enabled()
    {
        Assert.Equal(DomainValidationResult.FeatureNotEnabled,
            await DomainValidator.ValidateAsync("t.example.com", Client(HttpStatusCode.NotFound)));
    }

    [Fact]
    public async Task ServerError_is_unreachable()
    {
        Assert.Equal(DomainValidationResult.Unreachable,
            await DomainValidator.ValidateAsync("t.example.com", Client(HttpStatusCode.InternalServerError)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a domain")]
    [InlineData("localhost")]
    public async Task Malformed_domain_is_unreachable(string domain)
    {
        Assert.Equal(DomainValidationResult.Unreachable,
            await DomainValidator.ValidateAsync(domain, Client(HttpStatusCode.OK)));
    }
}
