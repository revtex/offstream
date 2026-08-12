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

    private SpotifyPoller Build() => new(_trackSource.Object);

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
        poller.AdvanceElapsed();
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
        poller.AdvanceElapsed();

        Assert.Null(poller.CurrentTrack?.CurrentPosition);
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
