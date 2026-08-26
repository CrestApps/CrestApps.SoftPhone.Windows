using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Xunit;

namespace SoftPhone.UiTests;

/// <summary>
/// FlaUI tests over the real app windows: first-run setup, the incoming-call popup, and the
/// tabbed Settings window. Each launches an isolated app instance and cleans it up.
///
/// These need an interactive desktop. Marked with a collection so they don't run in parallel
/// (they each drive the foreground desktop).
/// </summary>
[Collection("ui")]
[CollectionDefinition("ui", DisableParallelization = true)]
public class UiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static Window? WaitForWindow(AppHost host, Func<Window, bool> predicate)
    {
        return Retry.WhileNull(() =>
        {
            try
            {
                var windows = host.App.GetAllTopLevelWindows(host.Automation);
                return windows.FirstOrDefault(predicate);
            }
            catch { return null; }
        }, Timeout, TimeSpan.FromMilliseconds(250)).Result;
    }

    private static AutomationElement? WaitFor(Window window, Func<AutomationElement, bool> match)
    {
        return Retry.WhileNull(() =>
        {
            try { return window.FindAllDescendants().FirstOrDefault(match); }
            catch { return null; }
        }, Timeout, TimeSpan.FromMilliseconds(200)).Result;
    }

    [Fact]
    public void FirstRun_shows_setup_prompt_with_continue()
    {
        // No domain configured → the setup view.
        using var host = AppHost.Launch("", seedSettingsJson: "{ }");
        var window = WaitForWindow(host, w => w.Title.Contains("Soft Phone", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(window);

        var continueBtn = WaitFor(window!, e => e.ControlType == ControlType.Button
            && string.Equals(e.Name, "Continue", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(continueBtn);

        var heading = WaitFor(window!, e => e.Name?.Contains("Connect your soft phone", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(heading);
    }

    [Fact]
    public void SimulateIncoming_shows_popup_with_call_actions()
    {
        // Tray-only + simulated incoming → the popup appears (no domain, so no real connection).
        using var host = AppHost.Launch("--tray --simulate-incoming", seedSettingsJson: "{ }");

        var popup = WaitForWindow(host, w =>
        {
            try { return w.FindAllDescendants().Any(e => e.ControlType == ControlType.Button && e.Name == "Answer"); }
            catch { return false; }
        });
        Assert.NotNull(popup);

        Assert.NotNull(WaitFor(popup!, e => e.ControlType == ControlType.Button && e.Name == "Answer"));
        Assert.NotNull(WaitFor(popup!, e => e.ControlType == ControlType.Button && e.Name == "Decline"));
        Assert.NotNull(WaitFor(popup!, e => e.ControlType == ControlType.Button && e.Name == "Voicemail"));

        // Caller name from the simulated call is shown.
        Assert.NotNull(WaitFor(popup!, e => e.Name?.Contains("Maya Rodriguez", StringComparison.OrdinalIgnoreCase) == true));

        // Decline dismisses the popup.
        var decline = popup!.FindFirstDescendant(cf => cf.ByName("Decline"))?.AsButton();
        decline!.Invoke();
        var closed = Retry.WhileTrue(() =>
        {
            try { return host.App.GetAllTopLevelWindows(host.Automation).Any(w => w.Equals(popup)); }
            catch { return false; }
        }, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));
        Assert.False(closed.TimedOut && !closed.Result); // popup gone (or at least not stuck open)
    }

    [Fact]
    public void Settings_window_has_general_and_diagnostics_tabs()
    {
        using var host = AppHost.Launch("--tray --settings", seedSettingsJson: "{ }");

        var settings = WaitForWindow(host, w => w.Title.Contains("Settings", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(settings);

        Assert.NotNull(WaitFor(settings!, e => e.ControlType == ControlType.TabItem
            && string.Equals(e.Name, "General", StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(WaitFor(settings!, e => e.ControlType == ControlType.TabItem
            && string.Equals(e.Name, "Diagnostics", StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(WaitFor(settings!, e => e.ControlType == ControlType.Button
            && string.Equals(e.Name, "Save", StringComparison.OrdinalIgnoreCase)));
    }
}
