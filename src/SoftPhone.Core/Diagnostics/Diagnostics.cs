using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;
using SoftPhone.Core.Config;
using SoftPhone.Core.Contract;

namespace SoftPhone.Core.Diagnostics;

public enum DiagnosticStatus { Pending, Ok, Warn, Fail }

public sealed class DiagnosticStep
{
    public string Label { get; set; } = "";
    public DiagnosticStatus Status { get; set; } = DiagnosticStatus.Pending;
    public string? Detail { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DiagnosticsReport
{
    public string Domain { get; set; } = "";
    public bool Authenticated { get; set; }
    public bool UsedFallback { get; set; }
    public List<DiagnosticStep> Steps { get; set; } = new();
    public ExtensionConfig? Config { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public DiagnosticsReport Snapshot(bool finished = false) => new()
    {
        Domain = Domain,
        Authenticated = Authenticated,
        UsedFallback = UsedFallback,
        Steps = Steps.Select(s => new DiagnosticStep
        {
            Label = s.Label,
            Status = s.Status,
            Detail = s.Detail,
            At = s.At,
        }).ToList(),
        Config = Config,
        FinishedAt = finished ? DateTimeOffset.UtcNow : null,
    };
}

/// <summary>
/// Phase 0 spike core, also the diagnostics "Run connection test": prove the background
/// SignalR connection authenticates using the cookie read from WebView2 (config-fetch +
/// hub connect + current-offer). Mirrors src/connection/diagnostics.ts and reports a
/// step-by-step, streamable result. Decide cookie vs token fallback from the outcome.
/// </summary>
public static class DiagnosticsRunner
{
    private static DiagnosticStep Begin(DiagnosticsReport r, string label, Action<DiagnosticsReport>? emit)
    {
        var step = new DiagnosticStep { Label = label, Status = DiagnosticStatus.Pending, At = DateTimeOffset.UtcNow };
        r.Steps.Add(step);
        emit?.Invoke(r.Snapshot());
        return step;
    }

    private static void Set(DiagnosticsReport r, DiagnosticStep step, DiagnosticStatus status, string? detail, Action<DiagnosticsReport>? emit)
    {
        step.Status = status;
        step.Detail = detail;
        step.At = DateTimeOffset.UtcNow;
        emit?.Invoke(r.Snapshot());
    }

    /// <summary>
    /// Attempt a bare WebSocket connect to the hub to verify the transport handshake
    /// authenticates. Returns null on success or an error string on failure.
    /// </summary>
    private static async Task<string?> TryHubConnectAsync(
        string hubUrl, CookieContainer? cookies, Func<Task<string?>>? accessTokenProvider, CancellationToken ct)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl(hubUrl, o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                if (cookies is not null) o.Cookies = cookies;
                if (accessTokenProvider is not null)
                    o.AccessTokenProvider = async () => await accessTokenProvider() ?? "";
            })
            .Build();
        try
        {
            await conn.StartAsync(ct);
            await conn.StopAsync(ct);
            return null;
        }
        catch (Exception e)
        {
            try { await conn.StopAsync(ct); } catch { /* ignore */ }
            return e.Message;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Run the full Phase 0 spike against <paramref name="domain"/> using cookies read
    /// from WebView2. Reports incremental progress via <paramref name="emit"/> and
    /// resolves to the final report.
    /// </summary>
    public static async Task<DiagnosticsReport> RunAsync(
        string domain,
        CookieContainer? cookies,
        Action<DiagnosticsReport>? emit = null,
        Func<Task<string?>>? mintToken = null,
        CancellationToken ct = default)
    {
        var r = new DiagnosticsReport { Domain = domain };
        var config = ConfigClient.WithCookies(cookies ?? new CookieContainer());

        // Step 0: context banner.
        var ctx = Begin(r, "Running from desktop tray background context", emit);
        Set(r, ctx, DiagnosticStatus.Ok, cookies is null ? "no cookies supplied" : "cookies read from WebView2", emit);

        // Step 1: config fetch (cookie path).
        var s1 = Begin(r, "GET /softphone/extension-config (cookie)", emit);
        ExtensionConfig? cfg = null;
        try
        {
            cfg = await config.FetchExtensionConfigAsync(domain, ct);
            r.Config = cfg;
            r.Authenticated = true;
            Set(r, s1, DiagnosticStatus.Ok, $"hubUrl={cfg.HubUrl}; user={(string.IsNullOrEmpty(cfg.DisplayName) ? cfg.UserId : cfg.DisplayName)}", emit);
        }
        catch (ConfigException ce) when (ce.Kind == ConfigErrorKind.NotEnabled)
        {
            Set(r, s1, DiagnosticStatus.Fail, "No soft phone found at this domain (404) — likely the wrong domain, or the Soft Phone Extension feature isn't enabled on the tenant.", emit);
            return r.Snapshot(true);
        }
        catch (ConfigException ce) when (ce.Kind == ConfigErrorKind.Unauthenticated)
        {
            Set(r, s1, DiagnosticStatus.Warn, "Cookie not attached / no session. Will try the token fallback next.", emit);
        }
        catch (ConfigException ce)
        {
            Set(r, s1, DiagnosticStatus.Fail, ce.Message, emit);
            return r.Snapshot(true);
        }

        // Step 2 (fallback): if cookie path failed auth, try minting a token.
        if (cfg is null && mintToken is not null)
        {
            var s2 = Begin(r, "Token fallback: mint short-lived access token", emit);
            try
            {
                var token = await mintToken();
                if (!string.IsNullOrEmpty(token))
                {
                    r.UsedFallback = true;
                    Set(r, s2, DiagnosticStatus.Ok, "Token acquired; retrying config.", emit);
                    try { cfg = await config.FetchExtensionConfigAsync(domain, ct); r.Config = cfg; }
                    catch { /* config still cookie-gated; hub connect below uses the token */ }
                }
                else
                {
                    Set(r, s2, DiagnosticStatus.Fail, "Token fallback returned null — open the phone and sign in first.", emit);
                }
            }
            catch (Exception e)
            {
                Set(r, s2, DiagnosticStatus.Fail, $"Token fallback error: {e.Message}", emit);
            }
        }

        if (cfg is null)
            return r.Snapshot(true); // without config we cannot resolve the hub URL

        // Step 3: SignalR WebSocket connect (the actual auth proof).
        var s3 = Begin(r, "SignalR WebSocket connect to hub", emit);
        Func<Task<string?>>? tokenFactory = r.UsedFallback && mintToken is not null ? mintToken : null;
        var wsErr = await TryHubConnectAsync(cfg.HubUrl, cookies, tokenFactory, ct);
        if (wsErr is not null)
        {
            Set(r, s3, DiagnosticStatus.Fail, $"Hub connect failed: {wsErr}", emit);
            r.Authenticated = false;
            return r.Snapshot(true);
        }
        Set(r, s3, DiagnosticStatus.Ok,
            r.UsedFallback ? "Connected via token fallback." : "Connected via cookie — no token needed. ✅", emit);
        r.Authenticated = true;

        // Step 4: current-incoming-offer (also cookie/token authenticated).
        var s4 = Begin(r, "GET current-incoming-offer (catch in-flight ring)", emit);
        try
        {
            var offer = await config.FetchCurrentOfferAsync(cfg.CurrentIncomingOfferUrl, ct);
            Set(r, s4, DiagnosticStatus.Ok,
                offer is not null ? $"Live ring: {offer.Call?.From} -> {offer.Call?.To}" : "No call ringing (404).", emit);
        }
        catch (Exception e)
        {
            Set(r, s4, DiagnosticStatus.Warn, $"Offer fetch failed: {e.Message}", emit);
        }

        return r.Snapshot(true);
    }
}
