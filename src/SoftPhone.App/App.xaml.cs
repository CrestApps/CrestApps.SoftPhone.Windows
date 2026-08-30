using System.Windows;
using System.Windows.Threading;
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
    private DispatcherTimer? _offerPoll;

    // How often to poll the tenant's current-incoming-offer endpoint. The hub does not send
    // IncomingCall to this secondary (background) connection, so polling is how we detect a
    // ringing call reliably — independent of the (often flaky) WebSocket.
    private static readonly TimeSpan OfferPollInterval = TimeSpan.FromSeconds(2.5);

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

        _connection = new ConnectionHost(domain => CookieSource.ReadCookiesAsync(domain), Log.Info);
        _connection.StatusChanged += (s, d) => SetConnectionStatus(s, d);
        _connection.IncomingCall += (call, ctx) => Dispatcher.Invoke(() => _coordinator?.HandleIncoming(call, ctx));
        _connection.CallStateChanged += call => Dispatcher.Invoke(() => _coordinator?.HandleStateChanged(call));
        _connection.DialRequested += req => Dispatcher.Invoke(() => HandleDialRequested(req));
        _connection.NoActiveOffer += () => Dispatcher.Invoke(() => _coordinator?.OnNoActiveOffer());

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
        StartOfferPolling();

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
            // Show/hide the incoming popup as the phone window gains/loses focus or is minimized,
            // so a ringing call surfaces the moment the user looks away from the phone.
            _phoneWindow.Activated += (_, _) => _coordinator?.OnPhoneForegrounded();
            _phoneWindow.Deactivated += (_, _) => PhoneWentBackground();
            _phoneWindow.StateChanged += (_, _) =>
            {
                if (_phoneWindow?.WindowState == WindowState.Minimized)
                    PhoneWentBackground();
            };
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

    /// <summary>
    /// The phone window was minimized or lost focus: show the popup for any call we already
    /// know about, and actively re-check the current-incoming-offer endpoint so a ringing call
    /// surfaces even if the real-time hub event never reached our background connection.
    /// </summary>
    private void PhoneWentBackground()
    {
        _coordinator?.OnPhoneBackgrounded();
        _ = CheckOfferSafeAsync();
    }

    private async Task CheckOfferSafeAsync()
    {
        try { if (_connection is not null) await _connection.CheckCurrentOfferAsync(quiet: false); }
        catch (Exception ex) { Log.Error("current-offer check failed", ex); }
    }

    /// <summary>
    /// Poll the tenant's current-incoming-offer endpoint on a timer so a ringing call surfaces
    /// our popup even though the hub never sends IncomingCall to this background connection.
    /// </summary>
    private void StartOfferPolling()
    {
        _offerPoll?.Stop();
        _offerPoll = new DispatcherTimer { Interval = OfferPollInterval };
        _offerPoll.Tick += (_, _) =>
        {
            if (_connection is null) return;
            if (!DomainHelper.IsValidDomain(ResolveSettings().Domain.Value)) return;
            _ = _connection.CheckCurrentOfferAsync(quiet: true);
        };
        _offerPoll.Start();
    }

    private void AnswerCall(string callId)
    {
        Dispatcher.Invoke(() =>
        {
            OpenPhone();
            _phoneWindow?.NavigateAnswer(callId);
        });
    }

    /// <summary>
    /// Server-initiated outbound dial (an operator started a call from outside the phone). Handled
    /// like IncomingCall on the background connection: this runs on the UI thread.
    ///
    /// When the phone window is already live on the soft phone page, that page's own hub connection
    /// receives DialRequested and dials itself — we do nothing, so the call isn't placed twice.
    /// Otherwise (window closed/minimized/backgrounded, so no page connection is alive) we open and
    /// focus the phone, then relay the number to the page over the WebView2 message channel — never
    /// by navigating or reloading, which would drop a live call. The page then dials on its normal
    /// outbound path (registering first if needed, holding any active call) without touching presence.
    /// </summary>
    private void HandleDialRequested(TelephonyDialRequest request)
    {
        var number = request?.Number?.Trim();
        if (string.IsNullOrEmpty(number))
        {
            Log.Info("DialRequested ignored: empty number.");
            return;
        }

        if (_phoneWindow?.IsSoftPhoneLive == true)
        {
            Log.Info($"DialRequested {number}: phone page is live; leaving the dial to its own hub handler.");
            return;
        }

        Log.Info($"DialRequested {number}: opening the phone and relaying the number.");
        OpenPhone();
        _phoneWindow?.RelayDial(number);
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

    /// <summary>
    /// Reload the soft phone page at the configured domain. Invoked from Settings behind an explicit
    /// confirmation because it drops any active call. Opens the phone first if it isn't already up so the
    /// reloaded page is visible to the user.
    /// </summary>
    public void ReloadPhone()
    {
        OpenPhone();
        _phoneWindow?.ReloadPhonePage();
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
        _offerPoll?.Stop();
        _phoneWindow?.Close();
        _coordinator?.Dispose();
        _ = _connection?.DisposeAsync();
        _tray?.Dispose();
        CookieSource.Dispose();
        _instance?.Dispose();
        Shutdown();
    }
}
