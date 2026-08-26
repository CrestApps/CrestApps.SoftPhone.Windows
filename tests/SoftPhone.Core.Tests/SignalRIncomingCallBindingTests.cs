using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SoftPhone.Core.Contract;
using Xunit;

namespace SoftPhone.Core.Tests;

/// <summary>
/// Reproduces the exact failure the real tenant exhibited: a strongly-typed hub pushes
/// IncomingCall(call, richContext) where the context mirrors the server's IncomingCallContext
/// (extra/nested fields our CallContext doesn't declare). Proves whether the .NET client's
/// STRICT On&lt;Call, CallContext&gt; binding drops the invocation while the LOOSE (raw JSON)
/// binding receives it — i.e. whether the fix belongs in the client (binding) or the server
/// (delivery). CallStateChanged (a single simple arg) is included as the control that must work.
/// </summary>
public class SignalRIncomingCallBindingTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private string _hubUrl = "";

    // Mirrors the server's ITelephonyClient signatures (rich, nested context).
    public interface ITelephonyClientLike
    {
        Task IncomingCall(object call, object context);
        Task CallStateChanged(object call);
    }

    public sealed class TestHub : Hub<ITelephonyClientLike>
    {
        public async Task PushIncoming()
        {
            // Enum-like fields sent as strings (matches the observed server behavior where
            // CallStateChanged delivered State="Disconnected"), plus a context shaped like the
            // server's IncomingCallContext with fields our CallContext/ContextCard don't declare.
            var call = new
            {
                CallId = "call-1",
                From = "+17024993350",
                To = "+15550000000",
                State = "Ringing",
                Direction = "Inbound",
                ProviderName = "Telnyx",
            };
            var context = new
            {
                Heading = "Maya Rodriguez",
                Cards = new[]
                {
                    new
                    {
                        Id = "cust-1",
                        Title = "Maya Rodriguez",
                        Subtitle = "VIP customer",
                        Description = "Priority account",
                        Icon = "person",
                        Url = "https://tenant/customers/1",
                        OpenInNewTab = true,
                        Source = "crm",
                        Priority = 1,
                        Badges = new[] { "Support" },
                        Links = new[] { new { Text = "Open", Url = "https://tenant/x", Icon = "link", OpenInNewTab = true } },
                    },
                },
                Properties = new Dictionary<string, string> { ["queueId"] = "Support" },
            };
            await Clients.Caller.IncomingCall(call, context);
        }

        public Task PushStateChanged()
        {
            var call = new { CallId = "call-1", From = "+1", To = "+2", State = "Disconnected", Direction = "Inbound", ProviderName = "Telnyx" };
            return Clients.Caller.CallStateChanged(call);
        }
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        _app.MapHub<TestHub>("/hub");
        await _app.StartAsync();
        var addr = _app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();
        _hubUrl = addr.TrimEnd('/') + "/hub";
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static HubConnection Build(string url) =>
        new HubConnectionBuilder().WithUrl(url, o => o.Transports = HttpTransportType.WebSockets).Build();

    [Fact]
    public async Task CallStateChanged_binds_and_fires_the_control()
    {
        await using var conn = Build(_hubUrl);
        var tcs = new TaskCompletionSource<Call>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<Call>("CallStateChanged", c => tcs.TrySetResult(c));
        await conn.StartAsync();
        await conn.InvokeAsync("PushStateChanged");

        var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.True(done == tcs.Task, "CallStateChanged (single simple arg) should always bind.");
        Assert.Equal("call-1", (await tcs.Task).CallId);
    }

    [Fact]
    public async Task IncomingCall_STRICT_binding_to_CallContext()
    {
        await using var conn = Build(_hubUrl);
        var tcs = new TaskCompletionSource<CallContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<Call, CallContext>("IncomingCall", (_, ctx) => tcs.TrySetResult(ctx));
        await conn.StartAsync();
        await conn.InvokeAsync("PushIncoming");

        var fired = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task;
        // This assertion documents reality: if it FAILS, strict binding drops IncomingCall and
        // the client-side loose-binding fix is the correct place to fix it (server is fine).
        Assert.True(fired, "STRICT On<Call, CallContext> did NOT receive IncomingCall — binding drops it.");
    }

    [Fact]
    public async Task IncomingCall_LOOSE_binding_raw_json_always_fires()
    {
        await using var conn = Build(_hubUrl);
        var tcs = new TaskCompletionSource<CallContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<Call, JsonElement>("IncomingCall", (_, json) =>
        {
            var ctx = JsonSerializer.Deserialize<CallContext>(json.GetRawText(), ContractHelpers.Json) ?? new CallContext();
            tcs.TrySetResult(ctx);
        });
        await conn.StartAsync();
        await conn.InvokeAsync("PushIncoming");

        var fired = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task;
        Assert.True(fired, "LOOSE raw-JSON binding should always receive IncomingCall.");
        var ctx = (await tcs.Task);
        Assert.Equal("Maya Rodriguez", ctx.Heading);
        Assert.Equal("Support", ctx.Properties!["queueId"]);
        Assert.Equal("Maya Rodriguez", ctx.Cards![0].Title);
    }
}
