using Offstream.App.Resources;
using Offstream.App.Services;
using Offstream.Core.Diagnostics;
using Offstream.Core.Encoding;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// The session's lifetime: what starting refuses, what stopping releases.
/// </summary>
/// <remarks>
/// The controller is the only place that knows a <see cref="Offstream.Core.Recording.RecordingSession"/>
/// cannot be restarted, and the only place that turns a pipeline exception into something a user
/// can act on. Both are invisible until they are wrong.
/// </remarks>
public sealed class RecordingControllerTests
{
    [Fact]
    public async Task StartAsync_BuildsASessionAndRuns()
    {
        var factory = new FakeSessionFactory();
        await using var controller = new RecordingController(factory, RecordingFakes.Store());

        var refusal = await controller.StartAsync();

        Assert.Null(refusal);
        Assert.True(controller.IsRunning);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task StartAsync_ExposesTheSessionsLevelMeter()
    {
        var factory = new FakeSessionFactory();
        await using var controller = new RecordingController(factory, RecordingFakes.Store());

        Assert.Null(controller.Level);

        await controller.StartAsync();

        // The waveform binds to this. A null here is a control that draws a flat line through a
        // recording that is going fine.
        Assert.NotNull(controller.Level);
    }

    [Fact]
    public async Task StartAsync_RaisesStateChanged()
    {
        var factory = new FakeSessionFactory();
        await using var controller = new RecordingController(factory, RecordingFakes.Store());

        var raised = 0;
        controller.StateChanged += (_, _) => raised++;

        await controller.StartAsync();

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task StartAsync_WhileRunning_DoesNotBuildASecondSession()
    {
        var factory = new FakeSessionFactory();
        await using var controller = new RecordingController(factory, RecordingFakes.Store());

        await controller.StartAsync();
        var refusal = await controller.StartAsync();

        // A second session would open the same capture device behind the first one's back. The
        // ViewModel disables the button, but the button is not the only caller.
        Assert.Null(refusal);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task StartAsync_WithUnreadableSettings_RefusesWithoutTouchingTheFactory()
    {
        var factory = new FakeSessionFactory();
        await using var controller = new RecordingController(factory, RecordingFakes.StoreWithBrokenFile());

        var refusal = await controller.StartAsync();

        // Recording on defaults would write to a folder the user never chose, which is worse
        // than not recording.
        Assert.Equal(Strings.RecordCannotStartSettings, refusal);
        Assert.Equal(0, factory.Calls);
        Assert.False(controller.IsRunning);
    }

    [Fact]
    public async Task StartAsync_WithoutFfmpeg_SaysSo()
    {
        var factory = new FakeSessionFactory { Failure = new FFmpegNotFoundException("nowhere") };
        await using var controller = new RecordingController(factory, RecordingFakes.Store());

        Assert.Equal(Strings.RecordCannotStartFfmpeg, await controller.StartAsync());
        Assert.False(controller.IsRunning);
    }

    [Fact]
    public async Task StartAsync_WhenTheEndpointWillNotOpen_SaysSo()
    {
        // What a device unplugged since the settings page listed it looks like from here.
        var factory = new FakeSessionFactory { Failure = new InvalidOperationException("device gone") };
        await using var controller = new RecordingController(factory, RecordingFakes.Store());

        Assert.Equal(Strings.RecordCannotStartAudioDevice, await controller.StartAsync());
        Assert.False(controller.IsRunning);
    }

    [Fact]
    public async Task StartAsync_AfterARefusal_CanStillStart()
    {
        var factory = new FakeSessionFactory { Failure = new FFmpegNotFoundException("nowhere") };
        await using var controller = new RecordingController(factory, RecordingFakes.Store());

        await controller.StartAsync();

        // Installing ffmpeg, or correcting the path, has to be enough - without restarting the
        // app, which is why the locator runs per session rather than once at startup.
        factory.Failure = null;

        Assert.Null(await controller.StartAsync());
        Assert.True(controller.IsRunning);
    }

    [Fact]
    public async Task StopAsync_EndsTheSession()
    {
        var factory = new FakeSessionFactory();
        await using var controller = new RecordingController(factory, RecordingFakes.Store());

        await controller.StartAsync();
        await controller.StopAsync();

        Assert.False(controller.IsRunning);
        Assert.Null(controller.Level);
    }

    [Fact]
    public async Task StopAsync_WithNothingRunning_DoesNothing()
    {
        var factory = new FakeSessionFactory();
        await using var controller = new RecordingController(factory, RecordingFakes.Store());

        var raised = 0;
        controller.StateChanged += (_, _) => raised++;

        await controller.StopAsync();

        Assert.False(controller.IsRunning);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task StartAsync_AfterStop_BuildsAFreshSession()
    {
        var factory = new FakeSessionFactory();
        await using var controller = new RecordingController(factory, RecordingFakes.Store());

        await controller.StartAsync();
        var first = factory.Last;

        await controller.StopAsync();
        await controller.StartAsync();

        // StopAsync disposes the poller the session owns, so reusing one would start a recording
        // that never notices a track change.
        Assert.Equal(2, factory.Calls);
        Assert.NotSame(first, factory.Last);
        Assert.True(controller.IsRunning);
    }

    /// <summary>
    /// Awaited rather than asserted inline: <see cref="Progress{T}"/> posts to the context it was
    /// created on, so even the report <c>Start</c> makes synchronously arrives after the call
    /// returns. That is the same hop that puts these on the UI thread in the running app.
    /// </summary>
    [Fact]
    public async Task Progress_FromTheSession_ReachesSubscribers()
    {
        var factory = new FakeSessionFactory();
        await using var controller = new RecordingController(factory, RecordingFakes.Store());

        var received = new TaskCompletionSource<RecordingProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Progress += (_, report) => received.TrySetResult(report);

        await controller.StartAsync();

        var first = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Starting says so before Spotify has, which is what moves the page out of idle.
        Assert.Equal(RecordingStage.WaitingForTrack, first.Stage);
    }

    [Fact]
    public async Task DisposeAsync_StopsARunningSession()
    {
        var factory = new FakeSessionFactory();
        var controller = new RecordingController(factory, RecordingFakes.Store());

        await controller.StartAsync();
        await controller.DisposeAsync();

        Assert.False(controller.IsRunning);
    }

    [Fact]
    public async Task StartAsync_AfterDispose_Throws()
    {
        var controller = new RecordingController(new FakeSessionFactory(), RecordingFakes.Store());
        await controller.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(controller.StartAsync);
    }

    [Fact]
    public void Constructor_RejectsNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new RecordingController(null!, RecordingFakes.Store()));
        Assert.Throws<ArgumentNullException>(() => new RecordingController(new FakeSessionFactory(), null!));
    }
}
