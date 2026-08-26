using System.Net;
using Microsoft.Web.WebView2.Core;
using SoftPhone.Core.Config;

namespace SoftPhone.Core.Cookies;

/// <summary>
/// Reads the tenant session cookies out of WebView2's cookie store and packs them into
/// a <see cref="CookieContainer"/> that the background SignalR connection and the config
/// client attach to their requests. This is the Phase 0 "cookie → background connection"
/// bridge (contract §9): the user signs into {domain} once inside WebView2, and the
/// always-on background connection reuses that same session.
///
/// Kept behind this tiny surface so the rest of Core stays unit-testable without WebView2.
/// </summary>
public static class WebViewCookieBridge
{
    /// <summary>
    /// Build a <see cref="CookieContainer"/> from the cookies WebView2 holds for the
    /// tenant origin (https://{domain}). Includes host-only and domain cookies.
    /// </summary>
    public static async Task<CookieContainer> ReadCookiesAsync(CoreWebView2 webView, string domain)
    {
        var origin = DomainHelper.OriginFor(domain);
        var container = new CookieContainer();

        IReadOnlyList<CoreWebView2Cookie> cookies = await webView.CookieManager.GetCookiesAsync(origin);
        var originUri = new Uri(origin);

        foreach (var c in cookies)
        {
            try
            {
                // Domain cookies come back with a leading dot; host-only cookies use the
                // exact host. CookieContainer needs a concrete domain, so fall back to the
                // request host when WebView2 reports an empty domain.
                var cookieDomain = string.IsNullOrEmpty(c.Domain) ? originUri.Host : c.Domain.TrimStart('.');
                var netCookie = new Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, cookieDomain)
                {
                    Secure = c.IsSecure,
                    HttpOnly = c.IsHttpOnly,
                };
                container.Add(netCookie);
            }
            catch
            {
                // Skip any cookie the CookieContainer rejects (malformed name/value); the
                // session cookie we actually need is well-formed.
            }
        }

        return container;
    }

    /// <summary>True if WebView2 holds at least one cookie for the tenant origin.</summary>
    public static async Task<bool> HasAnyCookieAsync(CoreWebView2 webView, string domain)
    {
        var origin = DomainHelper.OriginFor(domain);
        var cookies = await webView.CookieManager.GetCookiesAsync(origin);
        return cookies.Count > 0;
    }
}
