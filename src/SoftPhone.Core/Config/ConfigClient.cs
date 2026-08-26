using System.Net;
using System.Net.Http.Json;
using SoftPhone.Core.Contract;

namespace SoftPhone.Core.Config;

public enum ConfigErrorKind
{
    Unauthenticated,
    NotEnabled,
    Network,
    BadResponse,
}

/// <summary>Mirrors the extension's ConfigError with a machine-readable <see cref="Kind"/>.</summary>
public sealed class ConfigException : Exception
{
    public ConfigErrorKind Kind { get; }
    public int? Status { get; }

    public ConfigException(string message, ConfigErrorKind kind, int? status = null)
        : base(message)
    {
        Kind = kind;
        Status = status;
    }
}

/// <summary>
/// Fetches GET /softphone/extension-config (contract §C) and the current-incoming-offer
/// (contract §B) using the tenant cookie. Mirrors src/connection/config.ts, including the
/// "manual redirect = unauthenticated" behavior.
///
/// Testable: construct with any <see cref="HttpClient"/> (inject a fake handler in tests);
/// use <see cref="WithCookies"/> in the app to wire the WebView2 cookie container.
/// </summary>
public sealed class ConfigClient
{
    private readonly HttpClient _http;

    public ConfigClient(HttpClient http) => _http = http;

    /// <summary>Build a client whose requests carry <paramref name="cookies"/> and never auto-follow redirects.</summary>
    public static ConfigClient WithCookies(CookieContainer cookies)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            // A 302 to /Login means "not authenticated" — surface it, don't follow it.
            AllowAutoRedirect = false,
        };
        return new ConfigClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) });
    }

    /// <summary>
    /// Fetch the tenant-aware extension config. The cookie is attached by the handler's
    /// cookie container (populated from WebView2). Contract §C.
    /// </summary>
    public async Task<ExtensionConfig> FetchExtensionConfigAsync(string domain, CancellationToken ct = default)
    {
        var url = $"{DomainHelper.OriginFor(domain)}/softphone/extension-config";
        HttpResponseMessage res;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");
            res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ConfigException($"Network error reaching {url}: {e.Message}", ConfigErrorKind.Network);
        }

        var status = (int)res.StatusCode;
        if (status is >= 300 and < 400)
            throw new ConfigException("Not authenticated (redirected to login).", ConfigErrorKind.Unauthenticated, status);
        if (status is 401 or 403)
            throw new ConfigException("Not authenticated (401/403).", ConfigErrorKind.Unauthenticated, status);
        if (status == 404)
            throw new ConfigException("Soft Phone Extension feature is not enabled on this tenant (404).", ConfigErrorKind.NotEnabled, 404);
        if (!res.IsSuccessStatusCode)
            throw new ConfigException($"Unexpected status {status} from {url}.", ConfigErrorKind.BadResponse, status);

        ExtensionConfig? cfg;
        try
        {
            cfg = await res.Content.ReadFromJsonAsync<ExtensionConfig>(ContractHelpers.Json, ct);
        }
        catch
        {
            throw new ConfigException("Config endpoint did not return JSON.", ConfigErrorKind.BadResponse, status);
        }

        if (cfg is null
            || string.IsNullOrEmpty(cfg.HubUrl)
            || string.IsNullOrEmpty(cfg.CurrentIncomingOfferUrl)
            || string.IsNullOrEmpty(cfg.SoftPhoneUrl))
        {
            throw new ConfigException("Config JSON is missing required fields.", ConfigErrorKind.BadResponse, status);
        }

        return cfg;
    }

    /// <summary>
    /// On connect, fetch the current in-flight ring (if any). 404 ⇒ nothing ringing.
    /// Contract §B "Pending offer".
    /// </summary>
    public async Task<PendingIncomingCallOffer?> FetchCurrentOfferAsync(string currentIncomingOfferUrl, CancellationToken ct = default)
    {
        HttpResponseMessage res;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, currentIncomingOfferUrl);
            req.Headers.Accept.ParseAdd("application/json");
            res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ConfigException($"Network error reaching current-incoming-offer: {e.Message}", ConfigErrorKind.Network);
        }

        var status = (int)res.StatusCode;
        if (status == 404) return null;
        if ((status is >= 300 and < 400) || status is 401 or 403)
            throw new ConfigException("Not authenticated fetching current offer.", ConfigErrorKind.Unauthenticated, status);
        if (!res.IsSuccessStatusCode)
            throw new ConfigException($"Unexpected status {status} fetching current offer.", ConfigErrorKind.BadResponse, status);

        return await res.Content.ReadFromJsonAsync<PendingIncomingCallOffer>(ContractHelpers.Json, ct);
    }
}
