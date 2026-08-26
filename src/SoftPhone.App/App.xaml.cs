using System.Windows;
using Microsoft.Web.WebView2.Core;
using SoftPhone.Core.Config;
using SoftPhone.Core.Contract;
using SoftPhone.Core.Hub;
using SoftPhone.Core.Settings;

namespace SoftPhone.App;

public partial class App : System.Windows.Application
{
    public SettingsStore SettingsStore { get; } = SettingsStore.Default();
    public PolicyProvider Policy { get; } = PolicyProviderFactory.CreateDefault();
    public CookieSource CookieSource { get; }

    public ConnectionStatus ConnectionStatus { get; private set; } = ConnectionStatus.Idle;

    /// <summary>True once the user chose Quit — lets the phone window really close (not hide).</summary>
    public bool IsQuitting { get; private set; }

    private SingleInstance? _instance;
    private TrayIcon? _tray;
    private PhoneWindow? _phoneWindow;
    private SettingsWindow? _settingsWindow;
    private CoreWebView2Environment? _webViewEnvironment;

    private ConnectionHost? _connection;
    private IncomingCallCoordinator? _coordinator;

    public App()
    {
        CookieSource = new CookieSource(this);
    }

    public async Task<CoreWebView2Environment> GetWebViewEnvironmentAsync()
    {
        return _webViewEnvironment ??= await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: AppPaths.WebViewUserData);
    }

    public EffectiveSettings ResolveSettings() => Policy.Resolve(SettingsStore.Load());
    public bool DiagnosticsEnabled => SettingsStore.Load().Diagnostics;
    public bool RingtoneEnabled => ResolveSettings().RingtoneEnabled.Value;
    public bool IsPhoneWindowFocused => _phoneWindow?.IsActive == true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled dispatcher exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled domain exception", args.ExceptionObject as Exception);

        _instance = SingleInstance.Acquire();
        if (!_instance.IsPrimary)
        {
            _instance.SignalPrimary();
            Shutdown();
            return;
        }
        _instance.ListenForActivation(() => Dispatcher.Invoke(OpenPhone));

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Log.Info($"Startup {BuildInfo.Summary}");

        StartupManager.SetEnabled(ResolveSettings().StartWithWindows.Value);

        _connection = new ConnectionHost(domain => CookieSource.ReadCookiesAsync(domain));
        _connection.StatusChanged += (s, d) => SetConnectionStatus(s, d);
        _connection.IncomingCall += (call, ctx) => Dispatcher.Invoke(() => _coordinator?.HandleIncoming(call, ctx));
        _connection.CallStateChanged += call => Dispatcher.Invoke(() => _coordinator?.HandleStateChanged(call));

        _coordinator = new IncomingCallCoordinator(this, new IncomingCallActions(
            Answer: AnswerCall,
            Decline: DeclineCall,
            Voicemail: VoicemailCall));

        BuildTray();

        var startHidden = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        var simulate = e.Args.Any(a => a.Equals("--simulate-incoming", StringComparison.OrdinalIgnoreCase));
        var openSettings = e.Args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));

        if (openSettings)
            OpenSettings();
        else if (!startHidden)
            OpenPhone();

        MaybeConnect();

        if (simulate)
            SimulateIncoming();
    }

    private void BuildTray()
    {
        _tray?.Dispose();
        _tray = new TrayIcon(
            new TrayActions(
                OpenPhone: OpenPhone,
                OpenSettings: OpenSettings,
                RunConnectionTest: RunConnectionTest,
                SimulateIncoming: SimulateIncoming,
                Quit: Quit),
            diagnosticsEnabled: DiagnosticsEnabled);
        _tray.UpdateStatus(ConnectionStatus);
    }

    public void OpenPhone()
    {
        if (_phoneWindow is null)
        {
            _phoneWindow = new PhoneWindow(this);
            _phoneWindow.DomainConfigured += _ => { OnSettingsChanged(); MaybeConnect(); };
            _phoneWindow.SignedIn += () => MaybeConnect();
            _phoneWindow.Closed += (_, _) => _phoneWindow = null;
            _phoneWindow.Show();
        }
        else
        {
            if (!_phoneWindow.IsVisible)
                _phoneWindow.Show(); // restore from tray (WebView2/call preserved)
            if (_phoneWindow.WindowState == WindowState.Minimized)
                _phoneWindow.WindowState = WindowState.Normal;
            _phoneWindow.Activate();
        }
    }

    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void RunConnectionTest()
    {
        OpenSettings();
        _settingsWindow?.ShowDiagnosticsAndRun();
    }

    // ------------------------------------------------------------- connection

    /// <summary>Connect the background hub if a valid domain is configured and we're not already up.</summary>
    private void MaybeConnect()
    {
        var effective = ResolveSettings();
        if (!DomainHelper.IsValidDomain(effective.Domain.Value)) return;
        if (ConnectionStatus is ConnectionStatus.Connected or ConnectionStatus.Connecting) return;
        var domain = effective.Domain.Value;
        // Must run on the UI/STA thread: reading cookies initializes the hidden WebView2.
        // ConnectAsync awaits network I/O, so it won't block the UI meaningfully.
        _ = ConnectSafeAsync(domain);
    }

    private async Task ConnectSafeAsync(string domain)
    {
        try { await _connection!.ConnectAsync(domain); }
        catch (Exception ex) { Log.Error("Background connect failed", ex); }
    }

    private void AnswerCall(string callId)
    {
        Dispatcher.Invoke(() =>
        {
            OpenPhone();
            _phoneWindow?.NavigateAnswer(callId);
        });
    }

    private void DeclineCall(string callId) => _ = SafeInvoke(() => _connection!.RejectAsync(callId), "Reject");
    private void VoicemailCall(string callId) => _ = SafeInvoke(() => _connection!.VoicemailAsync(callId), "Voicemail");

    private static async Task SafeInvoke(Func<Task> action, string name)
    {
        try { await action(); }
        catch (Exception ex) { Log.Error($"{name} failed", ex); }
    }

    private void SimulateIncoming()
    {
        var call = new Call
        {
            CallId = "SIM-" + Guid.NewGuid().ToString("N")[..8],
            From = "+1 555 111 2222",
            To = "+1 555 000 0000",
            State = "Ringing",
            Direction = "Inbound",
            ProviderName = "Simulated",
        };
        var context = new CallContext
        {
            Heading = "Maya Rodriguez",
            Cards = new() { new ContextCard { Title = "Maya Rodriguez", Subtitle = "VIP customer", Badges = new[] { "Support" } } },
            Properties = new() { ["queueId"] = "Support" },
        };
        _coordinator?.HandleIncoming(call, context);
    }

    /// <summary>
    /// Called when the phone window is closed to the tray. Shows a one-time balloon so the
    /// user learns the app is still running (near the clock) and will still ring.
    /// </summary>
    public void NotifyMinimizedToTray()
    {
        var settings = SettingsStore.Load();
        if (settings.TrayHintShown) return;
        _tray?.ShowBalloon("Soft Phone is still running",
            "It stays here by the clock so incoming calls still ring. Right-click for Open, Settings, or Quit.");
        settings.TrayHintShown = true;
        SettingsStore.Save(settings);
    }

    public void OnSettingsChanged()
    {
        BuildTray();
        _phoneWindow?.RefreshConfiguration();
        MaybeConnect();
    }

    public void SetConnectionStatus(ConnectionStatus status, string? detail = null)
    {
        ConnectionStatus = status;
        Log.Info($"Connection status: {status}{(detail is null ? "" : " — " + detail)}");
        Dispatcher.Invoke(() =>
        {
            _tray?.UpdateStatus(status, detail);
            _settingsWindow?.UpdateLiveStatus(status);
        });
    }

    private void Quit()
    {
        Log.Info("Quit");
        IsQuitting = true;
        _phoneWindow?.Close();
        _coordinator?.Dispose();
        _ = _connection?.DisposeAsync();
        _tray?.Dispose();
        CookieSource.Dispose();
        _instance?.Dispose();
        Shutdown();
    }
}
