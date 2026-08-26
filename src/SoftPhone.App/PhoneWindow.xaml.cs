using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using SoftPhone.Core.Config;
using SoftPhone.Core.Settings;

namespace SoftPhone.App;

public partial class PhoneWindow : Window
{
    private readonly App _app;
    private bool _webViewReady;
    private string _domain = "";

    /// <summary>Raised the first time a valid domain is configured via the setup view.</summary>
    public event Action<string>? DomainConfigured;

    /// <summary>Raised when the softphone page loads successfully (session is valid → background can connect).</summary>
    public event Action? SignedIn;

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
                    SignedIn?.Invoke();
                }
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
            Web.CoreWebView2.Navigate($"{DomainHelper.OriginFor(_domain)}/softphone?host=extension");
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
            var url = $"{DomainHelper.OriginFor(_domain)}/softphone?host=extension&answerCallId={Uri.EscapeDataString(callId)}";
            Web.CoreWebView2.Navigate(url);
        }
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
            ShowSetupMessage("Enter a valid domain, e.g. dialpad-dev.crestapps.online.", isError: true);
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
                    ShowSetupMessage("Reached the site, but the Soft Phone Extension feature isn't enabled on this tenant. Ask your administrator to enable it, then try again.", isError: true);
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
