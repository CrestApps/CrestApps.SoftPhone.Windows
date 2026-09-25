using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Media;
using SoftPhone.Core.Config;
using SoftPhone.Core.Diagnostics;
using SoftPhone.Core.Hub;
using SoftPhone.Core.Settings;

namespace SoftPhone.App;

public partial class SettingsWindow : Window
{
    private readonly App _app;
    private EffectiveSettings _effective;
    private readonly ObservableCollection<StepVm> _steps = new();

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
            new Uri("pack://application:,,,/Assets/app-32.png"));
        StepsList.ItemsSource = _steps;

        var user = _app.SettingsStore.Load();
        _effective = _app.Policy.Resolve(user);

        DomainBox.Text = _effective.Domain.Value;
        StartupCheck.IsChecked = _effective.StartWithWindows.IsManaged ? _effective.StartWithWindows.Value : StartupManager.IsEnabled();
        RingtoneCheck.IsChecked = _effective.RingtoneEnabled.Value;
        AlwaysOnTopCheck.IsChecked = user.AlwaysOnTop;
        DiagnosticsCheck.IsChecked = user.Diagnostics;

        ApplyManagedLocks();
        BuildStamp.Text = BuildInfo.Summary;
        UpdateLiveStatus(_app.ConnectionStatus);
    }

    /// <summary>Select the Diagnostics tab and immediately run the connection test.</summary>
    public void ShowDiagnosticsAndRun()
    {
        Tabs.SelectedIndex = 1;
        _ = RunConnectionTestAsync();
    }

    private void ApplyManagedLocks()
    {
        ManagedBanner.Visibility = _effective.AnyManaged ? Visibility.Visible : Visibility.Collapsed;
        if (_effective.Domain.IsManaged)
        {
            DomainBox.IsReadOnly = true;
            DomainBox.IsEnabled = false;
            DomainManaged.Visibility = Visibility.Visible;
        }
        if (_effective.StartWithWindows.IsManaged) StartupCheck.IsEnabled = false;
        if (_effective.RingtoneEnabled.IsManaged) RingtoneCheck.IsEnabled = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DomainError.Visibility = Visibility.Collapsed;
        var domain = DomainHelper.NormalizeDomain(DomainBox.Text);
        if (!_effective.Domain.IsManaged && !string.IsNullOrEmpty(domain) && !DomainHelper.IsValidDomain(domain))
        {
            DomainError.Text = "Enter a valid tenant domain, e.g. example.com.";
            DomainError.Visibility = Visibility.Visible;
            Tabs.SelectedIndex = 0;
            return;
        }

        var user = _app.SettingsStore.Load();
        if (!_effective.Domain.IsManaged) user.Domain = domain;
        if (!_effective.RingtoneEnabled.IsManaged) user.RingtoneEnabled = RingtoneCheck.IsChecked == true;
        user.AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true;
        user.Diagnostics = DiagnosticsCheck.IsChecked == true;
        if (!_effective.StartWithWindows.IsManaged)
        {
            var wantStartup = StartupCheck.IsChecked == true;
            user.StartWithWindows = wantStartup;
            StartupManager.SetEnabled(wantStartup);
        }

        _app.SettingsStore.Save(user);
        _app.OnSettingsChanged();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void ReloadPhone_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = AppDialog.Confirm(
            this,
            "Reload the soft phone?",
            "This reloads the phone at your tenant domain. If you are on a call, reloading disconnects it. " +
            "Make sure you are not on a call before you continue.",
            confirmText: "Reload phone");

        if (!confirmed) return;

        _app.ReloadPhone();
        Close();
    }

    // -------------------------------------------------------------- diagnostics

    private void RunTestButton_Click(object sender, RoutedEventArgs e) => _ = RunConnectionTestAsync();

    private async Task RunConnectionTestAsync()
    {
        var domain = DomainHelper.NormalizeDomain(DomainBox.Text);
        if (!DomainHelper.IsValidDomain(domain))
        {
            DomainError.Text = "Configure a valid domain on the General tab first.";
            DomainError.Visibility = Visibility.Visible;
            Tabs.SelectedIndex = 0;
            return;
        }

        RunTestButton.IsEnabled = false;
        _steps.Clear();
        SummaryBox.Visibility = Visibility.Collapsed;

        try
        {
            CookieContainer cookies = await _app.CookieSource.ReadCookiesAsync(domain);
            void Emit(DiagnosticsReport report) => Dispatcher.Invoke(() => RenderReport(report));
            var final = await DiagnosticsRunner.RunAsync(domain, cookies, Emit);
            RenderReport(final);
            RenderSummary(final);
        }
        catch (Exception ex)
        {
            Log.Error("Connection test failed", ex);
            AppDialog.Show(this, "The connection test could not run", ex.Message, AppDialogKind.Error);
        }
        finally
        {
            RunTestButton.IsEnabled = true;
        }
    }

    private void RenderReport(DiagnosticsReport report)
    {
        _steps.Clear();
        foreach (var s in report.Steps) _steps.Add(StepVm.From(s));
    }

    private void RenderSummary(DiagnosticsReport report)
    {
        SummaryBox.Visibility = Visibility.Visible;
        if (report.Authenticated && !report.UsedFallback)
        {
            SummaryBox.Background = (Brush)FindResource("Ok");
            SummaryText.Text = "PASS (cookie) — the background connection authenticates with the WebView2 cookie.";
        }
        else if (report.Authenticated && report.UsedFallback)
        {
            SummaryBox.Background = (Brush)FindResource("Warn");
            SummaryText.Text = "PASS (token) — cookie path didn't authenticate; the token fallback worked.";
        }
        else
        {
            SummaryBox.Background = (Brush)FindResource("Fail");
            SummaryText.Text = "FAIL — could not authenticate. Sign in inside the phone window, then re-run.";
        }
    }

    public void UpdateLiveStatus(ConnectionStatus status)
    {
        (string label, string color) = status switch
        {
            ConnectionStatus.Connected => ("Background: connected", "#22C55E"),
            ConnectionStatus.Connecting => ("Background: connecting…", "#FACC15"),
            ConnectionStatus.Reconnecting => ("Background: reconnecting…", "#FACC15"),
            ConnectionStatus.SignedOut => ("Background: signed out", "#F87171"),
            ConnectionStatus.Error => ("Background: error", "#F87171"),
            _ => ("Background: idle", "#94A3B8"),
        };
        LiveStatus.Text = label;
        LiveDot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
    }

    private sealed class StepVm
    {
        public string Glyph { get; init; } = "";
        public Brush Color { get; init; } = Brushes.Gray;
        public string Label { get; init; } = "";
        public string? Detail { get; init; }
        public Visibility DetailVisibility => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;

        public static StepVm From(DiagnosticStep s) => new()
        {
            Label = s.Label,
            Detail = s.Detail,
            Glyph = s.Status switch
            {
                DiagnosticStatus.Ok => "✔",
                DiagnosticStatus.Warn => "▲",
                DiagnosticStatus.Fail => "✖",
                _ => "○",
            },
            Color = s.Status switch
            {
                DiagnosticStatus.Ok => (Brush)System.Windows.Application.Current.FindResource("Ok"),
                DiagnosticStatus.Warn => (Brush)System.Windows.Application.Current.FindResource("Warn"),
                DiagnosticStatus.Fail => (Brush)System.Windows.Application.Current.FindResource("Fail"),
                _ => Brushes.Gray,
            },
        };
    }
}
