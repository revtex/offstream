using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Offstream.Core;
using Xunit;

namespace Offstream.UI.Tests.Desktop;

/// <summary>
/// A running Offstream, its own settings directory, and the automation session driving it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every fixture gets its own <c>OFFSTREAM_HOME</c>.</b> Without it these tests would read
/// and rewrite the developer's real <c>settings.json</c> — a suite that leaves the output format
/// on FLAC and the language on French. It also decouples the run from whatever was there before,
/// so a test asserting a default is asserting a default rather than a leftover.
/// </para>
/// <para>
/// A relocated home also gets its own single-instance claim (see
/// <see cref="OffstreamPaths.InstanceMutex"/>), which is what lets the suite run while the
/// developer's own Offstream is open in the tray. Without that, the launched process would
/// signal the real instance and exit, and every test here would fail waiting for a window that
/// belongs to someone else.
/// </para>
/// <para>
/// The process is started here and handed to <see cref="Application.Attach(Process)"/> rather
/// than using <c>Application.Launch</c>: FlaUI's launch wrapper loses its process association,
/// so teardown throws "No process is associated with this object" and turns a passing assertion
/// into a failure. Owning the id means no orphaned window survives a failed run.
/// </para>
/// </remarks>
public sealed class OffstreamApp : IDisposable
{
    private readonly int _processId;
    private readonly UIA3Automation _automation;

    public OffstreamApp()
    {
        Home = Path.Combine(Path.GetTempPath(), "offstream-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Home);

        var exe = Path.Combine(AppContext.BaseDirectory, "Offstream.exe");

        if (!File.Exists(exe))
        {
            throw new InvalidOperationException($"Expected the app next to the test binaries: {exe}");
        }

        var startInfo = new ProcessStartInfo(exe) { UseShellExecute = false };
        startInfo.Environment[OffstreamPaths.HomeVariable] = Home;

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Offstream.exe did not start.");

        _processId = process.Id;
        _automation = new UIA3Automation();

        // Thirty seconds because a cold first run on a loaded machine is slow, not because the
        // window normally takes anywhere near that.
        Window = Application.Attach(process).GetMainWindow(_automation, TimeSpan.FromSeconds(30))
            ?? throw new InvalidOperationException("Offstream started but never showed a window.");
    }

    /// <summary>The settings directory this instance was pointed at.</summary>
    public string Home { get; }

    /// <summary>The shell window.</summary>
    public Window Window { get; }

    /// <summary>Finds a control by its <c>AutomationProperties.AutomationId</c>.</summary>
    /// <remarks>
    /// Retried rather than searched once: a page that has just been navigated to is still being
    /// realised, and the pages themselves show and hide controls as settings change.
    /// </remarks>
    public AutomationElement Find(string automationId) =>
        Retry.WhileNull(
                () => Window.FindFirstDescendant(condition => condition.ByAutomationId(automationId)),
                TimeSpan.FromSeconds(10),
                throwOnTimeout: true,
                timeoutMessage: $"No control with AutomationId '{automationId}'.")
            .Result!;

    /// <summary>Whether a control with this id is currently on the page.</summary>
    /// <remarks>
    /// Distinct from <see cref="Find"/> failing: several controls are deliberately absent rather
    /// than disabled, so their absence is an assertion and not a timeout.
    /// </remarks>
    public bool IsPresent(string automationId, TimeSpan? within = null) =>
        Retry.WhileNull(
            () => Window.FindFirstDescendant(condition => condition.ByAutomationId(automationId)),
            within ?? TimeSpan.FromSeconds(2)).Success;

    /// <summary>Switches to a page by clicking its navigation item.</summary>
    public void Navigate(string navigationId)
    {
        Find(navigationId).Click();

        // The frame swaps content asynchronously; without this the first Find on the new page
        // races the navigation and finds the previous page's tree.
        Wait.UntilInputIsProcessed();
    }

    /// <summary>Types <paramref name="text"/> into a box and commits it.</summary>
    /// <remarks>
    /// <para>
    /// Both halves matter. Setting the value through the pattern puts text in the box; every
    /// field except the template box binds with the default <c>LostFocus</c> trigger, so nothing
    /// reaches the view model until focus moves on — which is the behaviour, not a workaround:
    /// saving "1" on the way to "120" would write a setting nobody chose.
    /// </para>
    /// <para>
    /// Focus moves with Tab because nothing else actually moves it. Focusing the window brings it
    /// to the front and leaves the caret exactly where it was, so the box never raises
    /// <c>LostFocus</c>, the binding never writes back, and a test would report the field as
    /// broken when it is not. Tab lands on whatever control is next, which is harmless as long as
    /// nothing types blind afterwards.
    /// </para>
    /// </remarks>
    public void SetText(string automationId, string text)
    {
        var box = Find(automationId).AsTextBox();
        box.Focus();
        box.Text = text;

        // Keystrokes go to whatever is in front, so the window is put there first - without
        // disturbing which control inside it holds the caret.
        Window.SetForeground();
        Keyboard.Type(VirtualKeyShort.TAB);
        Wait.UntilInputIsProcessed();
    }

    public void Dispose()
    {
        _automation.Dispose();

        KillIfRunning(_processId);

        try
        {
            Directory.Delete(Home, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not worth failing a run over; Windows cleans it.
        }
        catch (UnauthorizedAccessException)
        {
            // As above - most likely the process has not finished letting go of the log file.
        }
    }

    private static void KillIfRunning(int processId)
    {
        try
        {
            using var running = Process.GetProcessById(processId);
            running.Kill(entireProcessTree: true);
            running.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (ArgumentException)
        {
            // Already gone, which is the outcome we wanted anyway.
        }
        catch (InvalidOperationException)
        {
            // Exited between the lookup and the kill.
        }
    }
}

/// <summary>
/// Puts every desktop fixture in one collection, so only one window is being driven at a time.
/// </summary>
/// <remarks>
/// Each class still launches its own app — the collection carries no fixture. What it buys is
/// serialisation: keyboard input goes to whatever is in the foreground, so two suites typing at
/// once would put half of each test's text into the other's window.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopSuite
{
    public const string Name = "Offstream desktop";
}
