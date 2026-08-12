using Offstream.Core.Interop;
using Xunit;

namespace Offstream.Core.Tests.Interop;

/// <summary>
/// Smoke tests for the CsWin32-generated P/Invoke.
/// </summary>
/// <remarks>
/// These call the real Win32 entry points. They assert only that the calls bind and return
/// without throwing — the observable effect (a machine not sleeping) is not something a unit
/// test can verify, and belongs to the Phase 9 manual checklist. What they do catch is a
/// broken <c>NativeMethods.txt</c> entry or a signature the generator changed under us, which
/// would otherwise surface as a <c>DllNotFoundException</c> hours into a recording.
/// </remarks>
public sealed class PowerManagementTests
{
    [Fact]
    public void PreventAndAllowSleep_BindAndReturn()
    {
        var power = new PowerManagement();

        power.PreventSleep();
        try
        {
            // Nothing to assert beyond "the P/Invoke resolved and did not throw".
            Assert.True(true);
        }
        finally
        {
            power.AllowSleep();
        }
    }

    [Fact]
    public void AllowSleep_WithoutPreventSleep_IsHarmless()
    {
        var power = new PowerManagement();

        power.AllowSleep();
    }
}

public sealed class MediaKeysTests
{
    /// <summary>
    /// A zero handle means "Spotify has no window", which happens routinely while it starts
    /// up. It must be ignored rather than posted to the desktop.
    /// </summary>
    [Fact]
    public void SendNextTrack_WithZeroHandle_DoesNothing()
    {
        var keys = new MediaKeys();

        keys.SendNextTrack(0);
    }

    /// <summary>
    /// An invalid handle must not throw. SendMessage returns zero and sets last error, which
    /// is the correct outcome for a window that has closed since it was observed.
    /// </summary>
    [Fact]
    public void SendNextTrack_WithStaleHandle_DoesNotThrow()
    {
        var keys = new MediaKeys();

        keys.SendNextTrack(0x0BAD_F00D);
    }
}
