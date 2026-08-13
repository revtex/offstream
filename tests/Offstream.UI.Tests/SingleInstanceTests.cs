using Offstream.App.Services;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// The single-instance claim and the "show yourself" signal it carries.
/// </summary>
/// <remarks>
/// These use real named kernel objects, because that is the thing under test — a fake would
/// prove the test double works. Every test builds its own name from a fresh GUID so a run
/// cannot collide with a real Offstream on the same desktop, with a parallel test, or with a
/// previous run that left something behind.
/// </remarks>
public sealed class SingleInstanceTests
{
    private static string UniqueName() =>
        $@"Local\Offstream.Tests.{Guid.NewGuid():N}";

    [Fact]
    public void TryAcquire_WithNothingRunning_TakesTheClaim()
    {
        using var instance = SingleInstance.TryAcquire(() => { }, UniqueName());

        Assert.NotNull(instance);
    }

    [Fact]
    public void TryAcquire_WhenAlreadyHeld_StandsDown()
    {
        var name = UniqueName();
        using var first = SingleInstance.TryAcquire(() => { }, name);

        var second = SingleInstance.TryAcquire(() => { }, name);

        // Null is the whole contract: the caller shuts down instead of opening a second window
        // onto the same settings file.
        Assert.Null(second);
    }

    [Fact]
    public void TryAcquire_WhenAlreadyHeld_AsksTheRunningInstanceToShowItself()
    {
        var name = UniqueName();
        using var surfaced = new ManualResetEventSlim(false);
        using var first = SingleInstance.TryAcquire(surfaced.Set, name);

        _ = SingleInstance.TryAcquire(() => { }, name);

        // The callback arrives on a pool thread, so this waits rather than asserting inline.
        // Generous, because a loaded CI agent is slow, not broken.
        Assert.True(surfaced.Wait(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void TryAcquire_AfterTheHolderExits_IsAvailableAgain()
    {
        var name = UniqueName();

        SingleInstance.TryAcquire(() => { }, name)!.Dispose();

        using var next = SingleInstance.TryAcquire(() => { }, name);

        // Otherwise quitting and relaunching would leave the app permanently unable to start.
        Assert.NotNull(next);
    }

    /// <summary>
    /// The wait is registered with <c>executeOnlyOnce: false</c>. Getting that wrong would
    /// surface the window on the second launch and silently ignore every launch after it.
    /// </summary>
    /// <remarks>
    /// <b>Each signal is waited for before the next is sent, and that is not incidental.</b> The
    /// activation handle is <see cref="EventResetMode.AutoReset"/>, so a second <c>Set</c> that
    /// lands before the pool has run the callback for the first one is a no-op on an
    /// already-signalled event — the two collapse into one activation. That is the behaviour
    /// anyone would want (two launches racing each other should surface one window, not two),
    /// but it means "signal twice, expect two callbacks" is only true when the machine happens to
    /// schedule the callback in between. It did locally and did not on CI.
    /// </remarks>
    [Fact]
    public void TryAcquire_SignalsRepeatedly()
    {
        var name = UniqueName();
        using var signalled = new SemaphoreSlim(0);
        using var first = SingleInstance.TryAcquire(() => signalled.Release(), name);

        _ = SingleInstance.TryAcquire(() => { }, name);
        Assert.True(signalled.Wait(TimeSpan.FromSeconds(10)), "the first launch to be signalled");

        _ = SingleInstance.TryAcquire(() => { }, name);
        Assert.True(signalled.Wait(TimeSpan.FromSeconds(10)), "the second launch to be signalled");
    }

    [Fact]
    public void TryAcquire_WithNoCallback_Throws() =>
        Assert.Throws<ArgumentNullException>(() => SingleInstance.TryAcquire(null!, UniqueName()));
}
