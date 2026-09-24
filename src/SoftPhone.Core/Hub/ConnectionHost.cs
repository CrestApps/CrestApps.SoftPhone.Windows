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
    private readonly Action<string>? _log;

    private TelephonyHubClient? _client;
    private ExtensionConfig? _config;
    private CookieContainer? _cookies;
    private string? _domain;

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Idle;
    public ExtensionConfig? Config => _config;

    public event Action<ConnectionStatus, string?>? StatusChanged;
    public event Action<Call, CallContext>? IncomingCall;
    public event Action<Call>? CallStateChanged;

    /// <summary>
    /// Raised when the server pushes <c>DialRequested</c> (an operator started an outbound call
    /// from outside the phone). The app places the call using the soft phone's normal dial path.
    /// </summary>
    public event Action<TelephonyDialRequest>? DialRequested;

    /// <summary>Raised when a current-offer poll finds no pending inbound call (clear any popup).</summary>
    public event Action? NoActiveOffer;

    /// <summary>Raised when the server says a ringing call was answered on another of the user's soft phones.</summary>
    public event Action<IncomingCallAnsweredNotice>? IncomingCallAnswered;

    /// <summary>True once we've successfully fetched the tenant config (safe to poll the offer).</summary>
    public bool IsConfigured => _config is not null;

    /// <param name="cookieProvider">
    /// Supplies the tenant cookies for a domain (in the app, reads them from the hidden
    /// WebView2 cookie source).
    /// </param>
    public ConnectionHost(Func<string, Task<CookieContainer>> cookieProvider, Action<string>? log = null)
    {
        _cookieProvider = cookieProvider;
        _log = log;
    }

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
            _log?.Invoke($"Background: read {_cookies.GetAllCookies().Count} cookie(s) for {domain}.");
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
            _log?.Invoke($"Background: config ok; hubUrl={_config.HubUrl}; user={_config.DisplayName}.");
        }
        catch (ConfigException e)
        {
            // Unauthenticated / feature-off / unreachable — surface as signed-out so the UI
            // prompts the user to open the phone and sign in.
            SetStatus(ConnectionStatus.SignedOut, e.Message);
            return;
        }

        _client = new TelephonyHubClient(
            new HubClientOptions { HubUrl = _config.HubUrl, Cookies = _cookies, Log = _log },
            new HubClientCallbacks
            {
                OnIncomingCall = (call, context) =>
                {
                    _log?.Invoke($"Background: hub IncomingCall {call.CallId} from {call.From}.");
                    IncomingCall?.Invoke(call, context);
                },
                OnCallStateChanged = call =>
                {
                    _log?.Invoke($"Background: hub CallStateChanged {call.CallId} state={call.State}.");
                    CallStateChanged?.Invoke(call);
                },
                OnDialRequested = request =>
                {
                    _log?.Invoke($"Background: hub DialRequested number={request.Number}.");
                    DialRequested?.Invoke(request);
                },
                OnIncomingCallAnswered = notice =>
                {
                    _log?.Invoke($"Background: hub IncomingCallAnswered {notice.CallId} (offer {notice.OfferId}).");
                    IncomingCallAnswered?.Invoke(notice);
                },
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
        await CheckCurrentOfferAsync(quiet: false, ct);
    }

    /// <summary>
    /// Poll the current-incoming-offer endpoint (plain HTTP + cookie) and raise IncomingCall
    /// if a call is ringing. This is a robust fallback that does NOT depend on the real-time
    /// hub event reaching this secondary connection — used on connect and whenever the phone
    /// window is minimized/backgrounded, so a ringing call always surfaces our popup.
    /// </summary>
    private string? _lastPollError;

    public async Task CheckCurrentOfferAsync(bool quiet = false, CancellationToken ct = default)
    {
        if (_domain is null) return;
        try
        {
            var cookies = await _cookieProvider(_domain);
            var configClient = ConfigClient.WithCookies(cookies);
            var config = _config ?? await configClient.FetchExtensionConfigAsync(_domain, ct);
            _config = config;
            var offer = await configClient.FetchCurrentOfferAsync(config.CurrentIncomingOfferUrl, ct);
            _lastPollError = null;
            if (offer?.Call is not null)
            {
                _log?.Invoke($"Background: current-offer ringing {offer.Call.CallId} from {offer.Call.From}.");
                IncomingCall?.Invoke(offer.Call, offer.Context ?? new CallContext());
            }
            else
            {
                if (!quiet) _log?.Invoke("Background: current-offer check — nothing ringing.");
                NoActiveOffer?.Invoke();
            }
        }
        catch (Exception e)
        {
            // Log poll errors only when the message changes, to avoid flooding at the poll rate.
            if (!quiet || e.Message != _lastPollError)
            {
                _lastPollError = e.Message;
                _log?.Invoke($"Background: current-offer check failed: {e.Message}");
            }
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
