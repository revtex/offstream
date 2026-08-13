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

    [Fact]
    public void TryAcquire_SignalsRepeatedly()
    {
        var name = UniqueName();
        var count = 0;
        using var twice = new CountdownEvent(2);
        using var first = SingleInstance.TryAcquire(
            () => { Interlocked.Increment(ref count); twice.Signal(); },
            name);

        _ = SingleInstance.TryAcquire(() => { }, name);
        _ = SingleInstance.TryAcquire(() => { }, name);

        // The wait is registered with executeOnlyOnce: false. Getting that wrong would surface
        // the window on the second launch and silently ignore every launch after it.
        Assert.True(twice.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, count);
    }

    [Fact]
    public void TryAcquire_WithNoCallback_Throws() =>
        Assert.Throws<ArgumentNullException>(() => SingleInstance.TryAcquire(null!, UniqueName()));
}
