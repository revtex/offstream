using System.Diagnostics;
using FlaUI.Core;
using FlaUI.UIA3;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// End-to-end smoke test for the shell window.
/// </summary>
/// <remarks>
/// <para>
/// Tagged <c>Category=Desktop</c>: these drive a real window and need an interactive
/// session, so CI and <c>build.ps1</c> exclude them by default. Phase 6 grows this to cover
/// every control.
/// </para>
/// <para>
/// The test starts the process itself and hands it to <see cref="Application.Attach(Process)"/>
/// rather than using <c>Application.Launch</c>. FlaUI's launch wrapper loses its process
/// association here, so teardown throws "No process is associated with this object" and turns
/// a passing assertion into a failure. Owning the <see cref="Process"/> keeps cleanup
/// deterministic and guarantees no orphaned window survives a failed run.
/// </para>
/// </remarks>
public sealed class ShellWindowTests
{
    [Trait("Category", "Desktop")]
    [Fact]
    public void ShellWindowOpensWithTheOffstreamTitle()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Offstream.exe");
        Assert.True(File.Exists(exe), $"Expected the app next to the test binaries: {exe}");

        var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        Assert.NotNull(process);

        // Hold the id, not the object: FlaUI's Application takes ownership of the Process
        // and disposes it, after which every member throws "No process is associated with
        // this object" - including from a finally block, which would mask the real result.
        var processId = process.Id;

        try
        {
            var app = Application.Attach(process);
            using var automation = new UIA3Automation();

            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(30));

            Assert.NotNull(window);
            Assert.Contains("Offstream", window.Title, StringComparison.Ordinal);
        }
        finally
        {
            KillIfRunning(processId);
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
