using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;
using SoftPhone.Core.Contract;

namespace SoftPhone.Core.Hub;

/// <summary>
/// Reconnect forever (capped backoff) instead of the default give-up-after-~1-minute, so a
/// dropped WebSocket recovers on its own. The tenant hub was observed closing the socket
/// without a clean handshake; polling covers detection either way, but a live socket keeps
/// CallStateChanged flowing for fast popup clearing.
/// </summary>
internal sealed class InfiniteRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(retryContext.PreviousRetryCount, 5))));
}

public sealed class HubClientCallbacks
{
    public Action<Call, CallContext>? OnIncomingCall { get; init; }
    public Action<Call>? OnCallStateChanged { get; init; }
    public Action<ConnectionStatus, string?>? OnStatus { get; init; }
}

public sealed class HubClientOptions
{
    public required string HubUrl { get; init; }

    /// <summary>
    /// The tenant session cookies read from WebView2 — the preferred auth path
    /// (attached to negotiate + the WebSocket handshake). Contract §D / §9.
    /// </summary>
    public CookieContainer? Cookies { get; init; }

    /// <summary>
    /// Optional bearer-token factory (cookie-auth fallback, contract §9). When set,
    /// SignalR sends the token; when omitted, negotiation relies on the cookie.
    /// </summary>
    public Func<Task<string?>>? AccessTokenProvider { get; init; }
}

/// <summary>
/// SignalR .NET wrapper for TelephonyHub (contract §B). Provider-agnostic: it speaks
/// only to the tenant's own hub — never a telephony SDK. Mirrors src/connection/hubClient.ts.
/// </summary>
public sealed class TelephonyHubClient : IAsyncDisposable
{
    private readonly HubClientOptions _options;
    private readonly HubClientCallbacks _callbacks;
    private HubConnection? _connection;

    public TelephonyHubClient(HubClientOptions options, HubClientCallbacks? callbacks = null)
    {
        _options = options;
        _callbacks = callbacks ?? new HubClientCallbacks();
    }

    public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;

    public bool IsConnected => State == HubConnectionState.Connected;

    private HubConnection Build()
    {
        var conn = new HubConnectionBuilder()
            .WithUrl(_options.HubUrl, options =>
            {
                // WebSockets transport, but keep the negotiate step: it is an authenticated
                // POST that must carry the tenant cookie too, so it is part of the auth proof
                // and is the most compatible path for a standard ASP.NET Core SignalR hub.
                options.Transports = HttpTransportType.WebSockets;

                if (_options.Cookies is not null)
                {
                    options.Cookies = _options.Cookies; // send the tenant cookie on negotiate + WS handshake
                }

                if (_options.AccessTokenProvider is not null)
                {
                    options.AccessTokenProvider = async () => await _options.AccessTokenProvider() ?? "";
                }
            })
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .Build();

        conn.On<Call, CallContext>("IncomingCall", (call, context) =>
            _callbacks.OnIncomingCall?.Invoke(call, context));
        conn.On<Call>("CallStateChanged", call =>
            _callbacks.OnCallStateChanged?.Invoke(call));

        conn.Reconnecting += err =>
        {
            _callbacks.OnStatus?.Invoke(ConnectionStatus.Reconnecting, err?.Message);
            return Task.CompletedTask;
        };
        conn.Reconnected += _ =>
        {
            _callbacks.OnStatus?.Invoke(ConnectionStatus.Connected, null);
            return Task.CompletedTask;
        };
        conn.Closed += err =>
        {
            _callbacks.OnStatus?.Invoke(err is not null ? ConnectionStatus.Error : ConnectionStatus.Idle, err?.Message);
            return Task.CompletedTask;
        };

        return conn;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_connection is not null && State != HubConnectionState.Disconnected) return;
        _connection = Build();
        _callbacks.OnStatus?.Invoke(ConnectionStatus.Connecting, null);
        try
        {
            await _connection.StartAsync(ct);
            _callbacks.OnStatus?.Invoke(ConnectionStatus.Connected, null);
        }
        catch (Exception e)
        {
            _callbacks.OnStatus?.Invoke(ConnectionStatus.Error, e.Message);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_connection is null) return;
        try
        {
            await _connection.StopAsync(ct);
        }
        finally
        {
            await _connection.DisposeAsync();
            _connection = null;
            _callbacks.OnStatus?.Invoke(ConnectionStatus.Idle, null);
        }
    }

    /// <summary>Decline a ringing parked call — no media leg required (contract §B).</summary>
    public Task RejectAsync(string callId) =>
        InvokeAsync("Reject", new CallReference { CallId = callId });

    /// <summary>Send a ringing parked call to voicemail — no media leg required (contract §B).</summary>
    public Task VoicemailAsync(string callId) =>
        InvokeAsync("Voicemail", new CallReference { CallId = callId });

    private async Task InvokeAsync(string method, object arg)
    {
        if (_connection is null || !IsConnected)
            throw new InvalidOperationException($"Cannot invoke {method}: hub not connected.");
        await _connection.InvokeAsync(method, arg);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
