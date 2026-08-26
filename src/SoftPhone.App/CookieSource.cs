using System.Net;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SoftPhone.Core.Config;
using SoftPhone.Core.Cookies;

namespace SoftPhone.App;

/// <summary>
/// A persistent, hidden WebView2 that shares the same user-data profile as the phone
/// window. It exists so the tenant session cookie is readable for the background SignalR
/// connection and diagnostics <em>even when the phone window is closed</em> (contract §9:
/// "run the background connection with cookies from a hidden WebView2 host"). Cookies are
/// written to the shared profile when the user signs in inside the phone window, and this
/// control reads them back.
/// </summary>
public sealed class CookieSource : IDisposable
{
    private readonly App _app;
    private Window? _host;
    private WebView2? _web;
    private CoreWebView2? _core;
    private Task<CoreWebView2>? _initializing;

    public CookieSource(App app) => _app = app;

    private async Task<CoreWebView2> EnsureAsync()
    {
        if (_core is not null) return _core;
        return await (_initializing ??= InitializeAsync());
    }

    private async Task<CoreWebView2> InitializeAsync()
    {
        _web = new WebView2();
        _host = new Window
        {
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            Title = "CrestApps Soft Phone (background)",
            Content = _web,
        };
        _host.Show(); // WebView2 must be in a shown window to initialize

        var env = await _app.GetWebViewEnvironmentAsync();
        await _web.EnsureCoreWebView2Async(env);
        _core = _web.CoreWebView2;
        Log.Info("CookieSource initialized (hidden WebView2).");
        return _core;
    }

    /// <summary>Read the tenant session cookies from the shared profile.</summary>
    public async Task<CookieContainer> ReadCookiesAsync(string domain)
    {
        var core = await EnsureAsync();
        return await WebViewCookieBridge.ReadCookiesAsync(core, domain);
    }

    /// <summary>True if the shared profile holds any cookie for the tenant origin.</summary>
    public async Task<bool> HasSessionAsync(string domain)
    {
        var core = await EnsureAsync();
        return await WebViewCookieBridge.HasAnyCookieAsync(core, domain);
    }

    public void Dispose()
    {
        try { _web?.Dispose(); } catch { }
        try { _host?.Close(); } catch { }
        _core = null;
    }
}
