using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using SoftPhone.Core.Config;
using SoftPhone.Core.Contract;
using SoftPhone.Core.Settings;

namespace SoftPhone.App;

public partial class PhoneWindow : Window
{
    private readonly App _app;
    private bool _webViewReady;
    private string _domain = "";

    // True once the /softphone page (not the login page) has loaded, i.e. its in-page hub
    // connection and WebView2 message listener are live. A dial relayed while this is false is
    // held in _pendingDial and flushed the moment the page finishes loading (cold-open case).
    private bool _softPhoneLive;
    private string? _pendingDial;

    // True while the loaded page takes part in the incoming-call handoff (it sent softphone-ready and
    // we answered host-ready). The page then hides its own incoming modal while our popup confirms.
    private bool _pageBridgeReady;

    /// <summary>Raised the first time a valid domain is configured via the setup view.</summary>
    public event Action<string>? DomainConfigured;

    /// <summary>Raised when the softphone page loads successfully (session is valid → background can connect).</summary>
    public event Action? SignedIn;

    /// <summary>The page joined (true) or left (false) the incoming-call handoff.</summary>
    public event Action<bool>? PageBridgeChanged;

    /// <summary>A host-bridge message from the page (after the handshake).</summary>
    public event Action<PageMessage>? PageMessageReceived;

    public bool IsPageBridgeReady => _pageBridgeReady;

    public PhoneWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
            new Uri("pack://application:,,,/Assets/app-32.png"));

        RestoreSavedBounds();
        ApplyAlwaysOnTop();
        Loaded += async (_, _) => await InitializeAsync();
    }

    /// <summary>Apply the "keep on top" preference (user setting).</summary>
    public void ApplyAlwaysOnTop() => Topmost = _app.SettingsStore.Load().AlwaysOnTop;

    // The X button hides the window to the tray instead of destroying it, so a live WebRTC
    // call (hosted in the WebView2) survives "closing" it. Real exit is via tray → Quit.
    protected override void OnClosing(CancelEventArgs e)
    {
        SaveBounds();
        if (!_app.IsQuitting)
        {
            e.Cancel = true;
            Hide();
            _app.NotifyMinimizedToTray();
        }
        base.OnClosing(e);
    }

    private void SaveBounds()
    {
        if (WindowState != WindowState.Normal) return;
        try
        {
            var s = _app.SettingsStore.Load();
            s.WindowBounds = new WindowBounds { Top = Top, Left = Left, Width = Width, Height = Height };
            _app.SettingsStore.Save(s);
        }
        catch (Exception ex) { Log.Error("SaveBounds failed", ex); }
    }

    private void RestoreSavedBounds()
    {
        var b = _app.SettingsStore.Load().WindowBounds;
        if (b is null || b.Width < 320 || b.Height < 360) return;

        // Keep the restored rectangle within the virtual screen.
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        if (b.Left < -50 || b.Top < -50 || b.Left > vw - 100 || b.Top > vh - 100) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Top = b.Top; Left = b.Left; Width = b.Width; Height = b.Height;
    }

    private async Task InitializeAsync()
    {
        try
        {
            var env = await _app.GetWebViewEnvironmentAsync();
            await Web.EnsureCoreWebView2Async(env);
            _webViewReady = true;

            // Lock the phone surface down so the user cannot reload or navigate away from a live call.
            // A reload tears down the page's WebRTC session and drops the call, so we remove every built-in
            // way to trigger one: the right-click context menu (Reload/Back/Inspect), the browser accelerator
            // keys (F5, Ctrl+R, Ctrl+Shift+R, Alt+Left/Right, Ctrl+P/F, zoom), and the dev tools. Standard
            // text-editing shortcuts (Ctrl+C/V/X) are not accelerator keys, so typing in the page still works.
            var settings = Web.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.AreDevToolsEnabled = false;

            // Grant microphone to the tenant origin (contract §9 "two layers").
            Web.CoreWebView2.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                    e.State = CoreWebView2PermissionState.Allow;
            };

            // When the softphone page loads (not the login page), the session is valid →
            // the background connection can (re)connect using the freshly written cookie.
            Web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                var url = Web.Source?.ToString() ?? "";
                if (e.IsSuccess
                    && url.Contains("/softphone", StringComparison.OrdinalIgnoreCase)
                    && !url.Contains("/Login", StringComparison.OrdinalIgnoreCase))
                {
                    _softPhoneLive = true;
                    SignedIn?.Invoke();
                    FlushPendingDial();
                }
                else
                {
                    _softPhoneLive = false;
                }
            };

            // Incoming-call handoff with the page (see HostBridge). ContentLoading fires only once a new
            // document replaces the page — not for a navigation the page's "leave site?" prompt cancels —
            // so the handshake is dropped exactly when the page that made it is gone.
            Web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            Web.CoreWebView2.ContentLoading += (_, _) => SetPageBridge(false);
            Web.CoreWebView2.ProcessFailed += (_, e) =>
            {
                Log.Warn($"WebView2 process failed: {e.ProcessFailedKind}");
                if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                    SetPageBridge(false);
            };

            var effective = _app.ResolveSettings();
            if (DomainHelper.IsValidDomain(effective.Domain.Value))
                ShowPhone(effective.Domain.Value);
            else
                ShowSetup();
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 init failed", ex);
            MessageBox.Show(this, $"WebView2 failed to initialize:\n{ex.Message}",
                "Soft Phone", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowSetup()
    {
        _softPhoneLive = false;
        SetupView.Visibility = Visibility.Visible;
        Web.Visibility = Visibility.Collapsed;
        SetupDomainBox.Text = _app.SettingsStore.Load().Domain ?? "";
        SetupDomainBox.Focus();
    }

    private void ShowPhone(string domain)
    {
        _domain = DomainHelper.NormalizeDomain(domain);
        SetupView.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        if (_webViewReady)
        {
            _softPhoneLive = false;
            Web.CoreWebView2.Navigate($"{DomainHelper.OriginFor(_domain)}/softphone?host=extension");
        }
    }

    /// <summary>
    /// Reload the soft phone page. Reloading tears down the page's WebRTC session, so this is only ever
    /// invoked behind an explicit user confirmation (Settings → Reload phone). Re-navigates to the canonical
    /// soft phone URL so any transient query (e.g. answerCallId) is dropped and the phone comes back clean.
    /// </summary>
    public void ReloadPhonePage()
    {
        if (!_webViewReady) return;

        _softPhoneLive = false;
        if (DomainHelper.IsValidDomain(_domain))
        {
            SetupView.Visibility = Visibility.Collapsed;
            Web.Visibility = Visibility.Visible;
            Web.CoreWebView2.Navigate($"{DomainHelper.OriginFor(_domain)}/softphone?host=extension");
        }
        else
        {
            Web.CoreWebView2.Reload();
        }
    }

    /// <summary>Open/navigate the phone to auto-answer a pending inbound offer (contract §A).</summary>
    public void NavigateAnswer(string callId)
    {
        if (!DomainHelper.IsValidDomain(_domain))
        {
            var effective = _app.ResolveSettings();
            if (DomainHelper.IsValidDomain(effective.Domain.Value))
                _domain = DomainHelper.NormalizeDomain(effective.Domain.Value);
            else return;
        }
        SetupView.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        if (_webViewReady)
        {
            _softPhoneLive = false;
            var url = $"{DomainHelper.OriginFor(_domain)}/softphone?host=extension&answerCallId={Uri.EscapeDataString(callId)}";
            Web.CoreWebView2.Navigate(url);
        }
    }

    /// <summary>
    /// True when the phone window is visible and the <c>/softphone</c> page is loaded — i.e. the
    /// page's own hub connection is live and will receive <c>DialRequested</c> itself. When this is
    /// true the background handler leaves dialing to the page (no relay), avoiding a double dial.
    /// </summary>
    public bool IsSoftPhoneLive => IsVisible && WindowState != WindowState.Minimized && _softPhoneLive;

    /// <summary>
    /// Relay a server-initiated dial to the loaded soft phone page over the WebView2 message
    /// channel (never a navigation, query string, or reload — that would tear down a live call).
    /// If the page isn't loaded yet (cold open from the tray), the number is held and posted the
    /// moment it finishes loading. The <c>/softphone</c> page dials on receipt using its normal
    /// outbound path — registering first if needed and holding any active call, without changing
    /// the agent's presence.
    /// </summary>
    public void RelayDial(string number)
    {
        if (string.IsNullOrWhiteSpace(number)) return;
        _pendingDial = number.Trim();
        if (_webViewReady && _softPhoneLive)
            FlushPendingDial();
    }

    private void FlushPendingDial()
    {
        var number = _pendingDial;
        if (string.IsNullOrEmpty(number) || !_webViewReady) return;
        _pendingDial = null;
        try
        {
            // Delivered to the page as window.chrome.webview 'message' with { type: "dial", number }.
            var json = JsonSerializer.Serialize(new { type = "dial", number }, ContractHelpers.Json);
            Web.CoreWebView2.PostWebMessageAsJson(json);
            Log.Info($"Relayed DialRequested to soft phone page: {number}");
        }
        catch (Exception ex) { Log.Error("RelayDial post failed", ex); }
    }

    // ---- incoming-call handoff (HostBridge) ----

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only the tenant's own page may drive the notification: the messages carry customer data and
        // decide whether the page shows its incoming modal.
        if (!IsTenantOrigin(e.Source))
        {
            Log.Warn($"Ignored a web message from {e.Source}.");
            return;
        }

        string json;
        try { json = e.WebMessageAsJson; }
        catch (Exception ex) { Log.Error("Reading a web message failed", ex); return; }

        switch (HostBridge.ParsePageMessage(json))
        {
            case PageReadyMessage ready:
                Log.Info($"Soft phone page ready (host bridge protocol {ready.Protocol}).");
                PostToPage(HostBridge.HostReady());
                SetPageBridge(true);
                break;

            case { } message when _pageBridgeReady:
                PageMessageReceived?.Invoke(message);
                break;
        }
    }

    /// <summary>Post a host-bridge message to the loaded page (delivered to window.chrome.webview).</summary>
    public void PostToPage(string json)
    {
        if (!_webViewReady) return;
        try { Web.CoreWebView2.PostWebMessageAsJson(json); }
        catch (Exception ex) { Log.Error("Posting to the soft phone page failed", ex); }
    }

    private void SetPageBridge(bool ready)
    {
        if (_pageBridgeReady == ready) return;
        _pageBridgeReady = ready;
        PageBridgeChanged?.Invoke(ready);
    }

    private bool IsTenantOrigin(string? source)
    {
        if (!DomainHelper.IsValidDomain(_domain)
            || !Uri.TryCreate(source, UriKind.Absolute, out var sourceUri)
            || !Uri.TryCreate(DomainHelper.OriginFor(_domain), UriKind.Absolute, out var tenant))
            return false;

        return Uri.Compare(sourceUri, tenant, UriComponents.SchemeAndServer, UriFormat.Unescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }

    // ---- custom title-bar caption buttons ----
    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : ""; // restore / maximize
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SetupDomainBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = ValidateAndContinueAsync();
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e) => _ = ValidateAndContinueAsync();

    private async Task ValidateAndContinueAsync()
    {
        var domain = DomainHelper.NormalizeDomain(SetupDomainBox.Text);
        if (!DomainHelper.IsValidDomain(domain))
        {
            ShowSetupMessage("Enter a valid domain, e.g. example.com.", isError: true);
            return;
        }

        ContinueButton.IsEnabled = false;
        SetupDomainBox.IsEnabled = false;
        ShowSetupMessage("Checking…", isError: false);

        try
        {
            var result = await DomainValidator.ValidateAsync(domain);
            switch (result)
            {
                case DomainValidationResult.Valid:
                    SaveDomain(domain);
                    ShowPhone(domain);
                    DomainConfigured?.Invoke(domain);
                    break;
                case DomainValidationResult.FeatureNotEnabled:
                    // 404: reached a site, but there's no soft phone here — almost always the
                    // wrong domain. (If it really is the right one, the feature may be off.)
                    ShowSetupMessage("This doesn't look like the right domain — we couldn't find a soft phone here. Double-check the domain you entered. If it's correct, ask your administrator to enable the Soft Phone Extension feature.", isError: true);
                    break;
                default:
                    ShowSetupMessage("Couldn't reach that domain. Check the spelling and your connection, then try again.", isError: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Domain validation failed", ex);
            ShowSetupMessage($"Validation error: {ex.Message}", isError: true);
        }
        finally
        {
            ContinueButton.IsEnabled = true;
            SetupDomainBox.IsEnabled = true;
        }
    }

    private void SaveDomain(string domain)
    {
        var effective = _app.ResolveSettings();
        if (effective.Domain.IsManaged) return;
        var s = _app.SettingsStore.Load();
        s.Domain = domain;
        _app.SettingsStore.Save(s);
    }

    private void ShowSetupMessage(string text, bool isError)
    {
        SetupMessage.Visibility = Visibility.Visible;
        SetupMessage.Background = (Brush)FindResource(isError ? "Fail" : "Muted");
        SetupMessageText.Text = text;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => _app.OpenSettings();

    /// <summary>Re-evaluate configuration (e.g. after Settings changed the domain).</summary>
    public void RefreshConfiguration()
    {
        ApplyAlwaysOnTop();
        var effective = _app.ResolveSettings();
        if (DomainHelper.IsValidDomain(effective.Domain.Value))
        {
            if (!string.Equals(_domain, DomainHelper.NormalizeDomain(effective.Domain.Value), StringComparison.OrdinalIgnoreCase))
                ShowPhone(effective.Domain.Value);
        }
        else
        {
            ShowSetup();
        }
    }
}
