using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Offstream.Core;
using Xunit;

namespace Offstream.UI.Tests.Desktop;

/// <summary>
/// The shell: the window itself, the three tabs, and what minimising does.
/// </summary>
/// <remarks>
/// Tagged <c>Category=Desktop</c> — these drive a real window and need an interactive session,
/// so CI and <c>build.ps1</c> exclude them.
/// </remarks>
[Collection(DesktopSuite.Name)]
[Trait("Category", "Desktop")]
public sealed class ShellWindowTests(OffstreamApp app) : IClassFixture<OffstreamApp>
{
    [Fact]
    public void TheWindowOpensWithTheOffstreamTitle() =>
        Assert.Contains("Offstream", app.Window.Title, StringComparison.Ordinal);

    [Fact]
    public void TheThreeTabsAreAllThere()
    {
        // Three tabs, deliberately (plan §11). The predecessor's "Spy" tab is Record here, and
        // the naming rule is why it is not called anything else.
        Assert.True(app.IsPresent("NavRecord"));
        Assert.True(app.IsPresent("NavSettings"));
        Assert.True(app.IsPresent("NavAdvanced"));
    }

    [Fact]
    public void NavigatingSwapsThePage()
    {
        app.Navigate("NavSettings");
        Assert.True(app.IsPresent("SettingsOutputPath"));

        app.Navigate("NavAdvanced");
        Assert.True(app.IsPresent("AdvancedTemplate"));

        app.Navigate("NavRecord");
        Assert.True(app.IsPresent("RecordStartButton"));
    }

    [Fact]
    public void ThePagesKeepTheirStateAcrossTabs()
    {
        app.Navigate("NavAdvanced");
        app.SetText("AdvancedTemplate", "{artist}/{title}");

        app.Navigate("NavRecord");
        app.Navigate("NavAdvanced");

        // NavigationCacheMode=Enabled on every item: switching away from Record and back must
        // not throw away the activity log, and the same caching keeps this box's text.
        Assert.Equal("{artist}/{title}", app.Find("AdvancedTemplate").AsTextBox().Text);
    }

    [Fact]
    public void AWorkingSettingsFileRaisesNoWarning() =>
        // The InfoBar is collapsed until something goes wrong, so its absence is the assertion.
        // A bar that opened on every launch would train the user to ignore the one that matters.
        Assert.False(app.IsPresent("ShellStartupWarning"));

    /// <summary>
    /// Minimising hides the window, and a second launch brings it back rather than opening
    /// another one.
    /// </summary>
    /// <remarks>
    /// The two halves are one test because neither is worth much alone: hiding the window is
    /// only safe if there is a way back to it, and this is the way back that does not depend on
    /// finding a tray icon in the shell's overflow. It is also the closest this suite gets to
    /// the tray itself — the notification area is not addressable through the app's own UIA
    /// tree, so the icon's tooltip and menu are covered by <see cref="ShellViewModelTests"/>.
    /// </remarks>
    [Fact]
    public void MinimisingHidesTheWindowAndASecondLaunchBringsItBack()
    {
        app.Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Minimized);

        Assert.True(
            Retry.WhileFalse(() => app.Window.IsOffscreen, TimeSpan.FromSeconds(10)).Result,
            "The window was still on screen after minimising with minimise-to-tray on.");

        using var second = StartSecondInstance();

        Assert.True(
            second.WaitForExit(TimeSpan.FromSeconds(20)),
            "The second launch did not stand down; two instances would fight over one settings file.");

        Assert.True(
            Retry.WhileTrue(() => app.Window.IsOffscreen, TimeSpan.FromSeconds(10)).Result,
            "The running instance did not surface, so the second launch looked like nothing happened.");
    }

    private Process StartSecondInstance()
    {
        var startInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Offstream.exe"))
        {
            UseShellExecute = false,
        };

        // The same home, so it lands on the same claim as the instance the fixture started.
        startInfo.Environment[OffstreamPaths.HomeVariable] = app.Home;

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The second Offstream.exe did not start.");
    }
}
