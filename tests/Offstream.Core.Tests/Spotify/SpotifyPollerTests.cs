using Moq;
using Offstream.Core.Metadata;
using Offstream.Core.Spotify;
using Xunit;

namespace Offstream.Core.Tests.Spotify;

/// <summary>
/// Ported from the reference suite's <c>SpotifyHandlerTests</c>.
/// </summary>
/// <remarks>
/// The original drove the state machine through <c>System.Timers.Timer</c> and asserted on
/// timer state. These drive <see cref="SpotifyPoller.PollOnceAsync"/> directly, so the tests
/// assert on the state machine rather than on wall-clock timing — the same coverage without
/// the flakiness.
/// </remarks>
public sealed class SpotifyPollerTests
{
    private readonly Mock<ITrackSource> _trackSource = new();

    private void Returns(params Track?[] tracks)
    {
        var queue = new Queue<Track?>(tracks);

        _trackSource
            .Setup(x => x.GetCurrentTrackAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Count > 0 ? queue.Dequeue() : tracks[^1]);
    }

    private readonly TestClock _clock = new();

    private SpotifyPoller Build() => new(_trackSource.Object, _clock);

    /// <summary>
    /// A clock that only moves when a test moves it.
    /// </summary>
    /// <remarks>
    /// The elapsed counter reads a monotonic clock rather than counting ticks — a
    /// <see cref="PeriodicTimer"/> never makes up a late tick, so counting them drifted behind
    /// Spotify and never recovered. Driving the clock explicitly keeps these tests deterministic
    /// and free of any real waiting.
    /// </remarks>
    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => 1_000;

        public void Advance(TimeSpan amount) => _timestamp += (long)(amount.TotalSeconds * TimestampFrequency);
    }

    private static Track Playing(string artist, string title) =>
        new() { Artist = artist, Title = title, Playing = true };

    [Fact]
    public async Task PollOnce_WithNoTrack_RaisesNothing()
    {
        Returns((Track?)null);
        await using var poller = Build();

        var raised = 0;
        poller.TrackChanged += (_, _) => raised++;
        poller.PlayStateChanged += (_, _) => raised++;

        await poller.PollOnceAsync();

        Assert.Equal(0, raised);
        Assert.Null(poller.CurrentTrack);
    }

    [Fact]
    public async Task PollOnce_FirstObservation_SetsTrackWithoutRaisingChange()
    {
        Returns(Playing("Artist", "Title"));
        await using var poller = Build();

        var changes = 0;
        poller.TrackChanged += (_, _) => changes++;

        await poller.PollOnceAsync();

        Assert.NotNull(poller.CurrentTrack);
        Assert.Equal("Artist", poller.CurrentTrack.Artist);
        Assert.Equal(0, changes);
    }

    /// <summary>
    /// The track already playing when a session starts must be recorded, not just the one after
    /// it. Starting seeds an empty previous track so the first observation counts as a change —
    /// the reference implementation did the same when it began listening, and losing it means
    /// silently skipping the first song of every session.
    /// </summary>
    [Fact]
    public async Task Start_MakesTheTrackAlreadyPlayingCountAsAChange()
    {
        Returns(Playing("Artist", "Title"));
        await using var poller = Build();

        TrackChangedEventArgs? observed = null;
        poller.TrackChanged += (_, e) => observed = e;

        poller.Start();
        await poller.PollOnceAsync();

        Assert.NotNull(observed);
        Assert.Equal("Title", observed.NewTrack.Title);
        Assert.Null(observed.OldTrack?.Title);
    }

    [Fact]
    public async Task PollOnce_WhenTrackChanges_RaisesTrackChangedWithBothTracks()
    {
        Returns(Playing("Artist", "First"), Playing("Artist", "Second"));
        await using var poller = Build();

        TrackChangedEventArgs? observed = null;
        poller.TrackChanged += (_, e) => observed = e;

        await poller.PollOnceAsync();
        await poller.PollOnceAsync();

        Assert.NotNull(observed);
        Assert.Equal("First", observed.OldTrack?.Title);
        Assert.Equal("Second", observed.NewTrack.Title);
    }

    [Fact]
    public async Task PollOnce_WhenSameTrack_DoesNotRaiseTrackChanged()
    {
        Returns(Playing("Artist", "Title"), Playing("Artist", "Title"));
        await using var poller = Build();

        var changes = 0;
        poller.TrackChanged += (_, _) => changes++;

        await poller.PollOnceAsync();
        await poller.PollOnceAsync();

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task PollOnce_WhenPlayStateChanges_RaisesPlayStateChanged()
    {
        var paused = new Track { Artist = "Artist", Title = "Title", Playing = false };
        Returns(Playing("Artist", "Title"), paused);
        await using var poller = Build();

        PlayStateChangedEventArgs? observed = null;
        poller.PlayStateChanged += (_, e) => observed = e;

        await poller.PollOnceAsync();
        await poller.PollOnceAsync();

        Assert.NotNull(observed);
        Assert.False(observed.Playing);
    }

    [Fact]
    public async Task PollOnce_ResetsElapsedOnNewTrack()
    {
        Returns(Playing("Artist", "First"), Playing("Artist", "Second"));
        await using var poller = Build();

        await poller.PollOnceAsync();
        _clock.Advance(TimeSpan.FromSeconds(2));
        poller.AdvanceElapsed();
        Assert.Equal(2, poller.CurrentTrack?.CurrentPosition);

        await poller.PollOnceAsync();

        Assert.Null(poller.CurrentTrack?.CurrentPosition);
    }

    [Fact]
    public async Task PollOnce_KeepsElapsedWhileTrackContinues()
    {
        Returns(Playing("Artist", "Title"), Playing("Artist", "Title"));
        await using var poller = Build();

        await poller.PollOnceAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
        poller.AdvanceElapsed();

        await poller.PollOnceAsync();

        Assert.Equal(1, poller.CurrentTrack?.CurrentPosition);
    }

    [Fact]
    public async Task AdvanceElapsed_WhilePaused_DoesNothing()
    {
        Returns(new Track { Artist = "Artist", Title = "Title", Playing = false });
        await using var poller = Build();

        await poller.PollOnceAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
        poller.AdvanceElapsed();

        Assert.Null(poller.CurrentTrack?.CurrentPosition);
    }

    /// <summary>
    /// The drift this counter used to accumulate: it added one second per tick observed, and
    /// <see cref="PeriodicTimer"/> never makes up a tick it delivered late.
    /// </summary>
    /// <remarks>
    /// Ten seconds of real time delivering four ticks used to read as four seconds and stay four
    /// behind for the rest of the track — which is why the counter sat visibly behind Spotify and
    /// looked like it paused whenever the app was busy or in the background.
    /// </remarks>
    [Fact]
    public async Task AdvanceElapsed_AfterLateTicks_ReportsTimeThatPassedNotTicksObserved()
    {
        Returns(Playing("Artist", "Title"));
        await using var poller = Build();

        await poller.PollOnceAsync();

        // Ten seconds pass; the loop only gets to run four times.
        for (var tick = 0; tick < 4; tick++)
        {
            _clock.Advance(TimeSpan.FromSeconds(2.5));
            poller.AdvanceElapsed();
        }

        Assert.Equal(10, poller.CurrentTrack?.CurrentPosition);
    }

    /// <summary>A paused track must not accrue the time it spent paused.</summary>
    [Fact]
    public async Task AdvanceElapsed_AcrossAPause_CountsOnlyThePlayingStretches()
    {
        var paused = new Track { Artist = "Artist", Title = "Title", Playing = false };

        Returns(Playing("Artist", "Title"), paused, Playing("Artist", "Title"));
        await using var poller = Build();

        await poller.PollOnceAsync();
        _clock.Advance(TimeSpan.FromSeconds(5));

        // Paused for a minute.
        await poller.PollOnceAsync();
        _clock.Advance(TimeSpan.FromSeconds(60));

        // Playing again for three more seconds.
        await poller.PollOnceAsync();
        _clock.Advance(TimeSpan.FromSeconds(3));
        poller.AdvanceElapsed();

        Assert.Equal(8, poller.CurrentTrack?.CurrentPosition);
    }

    /// <summary>
    /// Starting from a thread with a synchronization context must not put the poll loop on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the UI freeze.</b> <see cref="SpotifyPoller.Start"/> is called from a button
    /// click, so the WPF dispatcher is the current context — and an <c>await</c> that captures it
    /// resumes on the UI thread. The loop polls every 70&#160;ms and raises every recording
    /// handler inline, including the one that blocks waiting for the outgoing recorder to release
    /// the capture buffer, so all of that ran on the UI thread and the window froze for seconds
    /// at a time at a track change.
    /// </para>
    /// <para>
    /// A counting context stands in for the dispatcher: nothing the loop does may post to it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Start_FromAThreadWithASynchronizationContext_DoesNotRunTheLoopOnIt()
    {
        Returns(Playing("Artist", "Title"));

        var context = new CountingSynchronizationContext();
        var original = SynchronizationContext.Current;
        var poller = Build();

        // Only Start() runs under the context — this test's own awaits would post to it too,
        // which would say nothing about the poller.
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            poller.Start();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        // Long enough for many poll intervals, and for the one-second song tick.
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        await poller.DisposeAsync();

        Assert.Equal(0, context.Posts);
    }

    /// <summary>A stand-in for the dispatcher that only records whether it was posted to.</summary>
    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            base.Post(d, state);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            base.Send(d, state);
        }
    }

    [Fact]
    public async Task Start_IsIdempotentAndDisposeStopsCleanly()
    {
        Returns(Playing("Artist", "Title"));
        await using var poller = Build();

        poller.Start();
        poller.Start();

        Assert.True(poller.IsListening);

        await poller.DisposeAsync();

        Assert.False(poller.IsListening);
    }

    /// <summary>
    /// The reference raised events through fire-and-forget <c>Task.Run</c>, so a throwing
    /// subscriber vanished silently. Raising inline means it surfaces — which is the point.
    /// </summary>
    [Fact]
    public async Task PollOnce_PropagatesSubscriberExceptions()
    {
        Returns(Playing("Artist", "First"), Playing("Artist", "Second"));
        await using var poller = Build();

        poller.TrackChanged += (_, _) => throw new InvalidOperationException("subscriber blew up");

        await poller.PollOnceAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => poller.PollOnceAsync());
    }
}
