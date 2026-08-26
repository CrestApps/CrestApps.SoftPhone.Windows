using System.Net;
using SoftPhone.Core.Config;
using SoftPhone.Core.Contract;

namespace SoftPhone.Core.Hub;

/// <summary>
/// The persistent background connection host (contract §3). UI-free: it owns the SignalR
/// <see cref="TelephonyHubClient"/>, fetches the tenant config with the WebView2 cookie,
/// catches an in-flight ring on connect, and raises events the app turns into a toast +
/// ringtone + popup. Reject/Voicemail act on a ringing parked call directly (no media leg).
///
/// Mirrors the extension's src/connection/connectionHost.ts, minus the ringtone (kept in
/// the UI layer here so Core stays unit-testable).
/// </summary>
public sealed class ConnectionHost : IAsyncDisposable
{
    private readonly Func<string, Task<CookieContainer>> _cookieProvider;

    private TelephonyHubClient? _client;
    private ExtensionConfig? _config;
    private CookieContainer? _cookies;
    private string? _domain;

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Idle;
    public ExtensionConfig? Config => _config;

    public event Action<ConnectionStatus, string?>? StatusChanged;
    public event Action<Call, CallContext>? IncomingCall;
    public event Action<Call>? CallStateChanged;

    /// <param name="cookieProvider">
    /// Supplies the tenant cookies for a domain (in the app, reads them from the hidden
    /// WebView2 cookie source).
    /// </param>
    public ConnectionHost(Func<string, Task<CookieContainer>> cookieProvider) =>
        _cookieProvider = cookieProvider;

    private void SetStatus(ConnectionStatus status, string? detail = null)
    {
        Status = status;
        StatusChanged?.Invoke(status, detail);
    }

    /// <summary>
    /// (Re)connect: read cookies → fetch extension-config → open the hub → catch an
    /// in-flight ring via current-incoming-offer.
    /// </summary>
    public async Task ConnectAsync(string domain, CancellationToken ct = default)
    {
        _domain = domain;
        await DisconnectAsync();
        SetStatus(ConnectionStatus.Connecting);

        try
        {
            _cookies = await _cookieProvider(domain);
        }
        catch (Exception e)
        {
            SetStatus(ConnectionStatus.Error, $"Cookie read failed: {e.Message}");
            return;
        }

        var configClient = ConfigClient.WithCookies(_cookies);
        try
        {
            _config = await configClient.FetchExtensionConfigAsync(domain, ct);
        }
        catch (ConfigException e)
        {
            // Unauthenticated / feature-off / unreachable — surface as signed-out so the UI
            // prompts the user to open the phone and sign in.
            SetStatus(ConnectionStatus.SignedOut, e.Message);
            return;
        }

        _client = new TelephonyHubClient(
            new HubClientOptions { HubUrl = _config.HubUrl, Cookies = _cookies },
            new HubClientCallbacks
            {
                OnIncomingCall = (call, context) => IncomingCall?.Invoke(call, context),
                OnCallStateChanged = call => CallStateChanged?.Invoke(call),
                OnStatus = (status, detail) => SetStatus(status, detail),
            });

        try
        {
            await _client.StartAsync(ct);
        }
        catch (Exception e)
        {
            SetStatus(ConnectionStatus.Error, e.Message);
            return;
        }

        // Catch an in-flight ring on connect (contract §B).
        try
        {
            var offer = await configClient.FetchCurrentOfferAsync(_config.CurrentIncomingOfferUrl, ct);
            if (offer is not null)
                IncomingCall?.Invoke(offer.Call, offer.Context);
        }
        catch
        {
            // Non-fatal: the connection is up even if the offer probe fails.
        }
    }

    public async Task DisconnectAsync()
    {
        if (_client is not null)
        {
            await _client.StopAsync();
            await _client.DisposeAsync();
            _client = null;
        }
    }

    /// <summary>Decline a ringing parked call — no media leg required (contract §B).</summary>
    public Task RejectAsync(string callId) =>
        _client is not null ? _client.RejectAsync(callId) : Task.CompletedTask;

    /// <summary>Send a ringing parked call to voicemail — no media leg required (contract §B).</summary>
    public Task VoicemailAsync(string callId) =>
        _client is not null ? _client.VoicemailAsync(callId) : Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
