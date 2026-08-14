using Offstream.Core.Metadata;
using Offstream.Core.Spotify;
using Xunit;

namespace Offstream.Core.Tests.Spotify;

/// <summary>
/// The handover between the two detectors. Getting this wrong does not fail loudly — it records
/// the wrong thing, or stops recording — so each rule is pinned separately.
/// </summary>
public sealed class PreferredTrackSourceTests
{
    private sealed class FakeSource(Track? track, Exception? fault = null) : ITrackSource
    {
        public int Calls { get; private set; }

        public Task<Track?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
        {
            Calls++;

            return fault is not null ? Task.FromException<Track?>(fault) : Task.FromResult(track);
        }
    }

    private static Track Song(string artist, string title, bool playing = true) =>
        new() { Artist = artist, Title = title, Playing = playing };

    /// <summary>SMTC answering at all is what makes it win. Nothing else is consulted.</summary>
    [Fact]
    public async Task WhenThePreferredSourceAnswers_TheFallbackIsNotAsked()
    {
        var fallback = new FakeSource(Song("Wrong", "Wrong"));
        var source = new PreferredTrackSource(new FakeSource(Song("Right", "Right")), fallback);

        var track = await source.GetCurrentTrackAsync();

        Assert.Equal("Right", track!.Artist);
        Assert.Equal(0, fallback.Calls);
    }

    /// <summary>
    /// The case Phase 7 exists for: Spotify minimised to the tray has no window title, and
    /// detection used to stop dead.
    /// </summary>
    [Fact]
    public async Task WhenThePreferredSourceIsSilent_TheFallbackAnswers()
    {
        var source = new PreferredTrackSource(new FakeSource(null), new FakeSource(Song("Artist", "Title")));

        Assert.Equal("Artist", (await source.GetCurrentTrackAsync())!.Artist);
    }

    /// <summary>
    /// An idle answer is still an answer. Second-guessing it with the other source is how two
    /// detectors start disagreeing mid-track.
    /// </summary>
    [Fact]
    public async Task AnIdleAnswerFromThePreferredSource_IsNotOverriddenByTheFallback()
    {
        var fallback = new FakeSource(Song("Stale", "Stale"));
        var idle = Song("Artist", "Title", playing: false);

        var source = new PreferredTrackSource(new FakeSource(idle), fallback);

        var track = await source.GetCurrentTrackAsync();

        Assert.False(track!.Playing);
        Assert.Equal("Artist", track.Artist);
        Assert.Equal(0, fallback.Calls);
    }

    /// <summary>
    /// SMTC is a system service Offstream does not control. Its being unavailable must cost the
    /// better metadata and never the recording.
    /// </summary>
    [Fact]
    public async Task WhenThePreferredSourceThrows_TheFallbackStillAnswers()
    {
        var source = new PreferredTrackSource(
            new FakeSource(null, new InvalidOperationException("no session manager")),
            new FakeSource(Song("Artist", "Title")));

        Assert.Equal("Artist", (await source.GetCurrentTrackAsync())!.Artist);
    }

    /// <summary>A source that throws is retried, not written off for the rest of the session.</summary>
    [Fact]
    public async Task AFailingPreferredSource_IsAskedAgainOnTheNextPoll()
    {
        var preferred = new FakeSource(null, new InvalidOperationException("transient"));
        var source = new PreferredTrackSource(preferred, new FakeSource(Song("Artist", "Title")));

        await source.GetCurrentTrackAsync();
        await source.GetCurrentTrackAsync();
        await source.GetCurrentTrackAsync();

        Assert.Equal(3, preferred.Calls);
    }

    /// <summary>
    /// Cancellation is the session stopping, not a source failing, so it propagates instead of
    /// being swallowed into a fallback lookup.
    /// </summary>
    [Fact]
    public async Task Cancellation_PropagatesRatherThanFallingBack()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var fallback = new FakeSource(Song("Artist", "Title"));
        var source = new PreferredTrackSource(
            new FakeSource(null, new OperationCanceledException(cancellation.Token)),
            fallback);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GetCurrentTrackAsync(cancellation.Token));

        Assert.Equal(0, fallback.Calls);
    }

    /// <summary>Neither source seeing Spotify is simply "not running".</summary>
    [Fact]
    public async Task WhenNeitherSourceAnswers_TheResultIsNothing()
    {
        var source = new PreferredTrackSource(new FakeSource(null), new FakeSource(null));

        Assert.Null(await source.GetCurrentTrackAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Constructor_RejectsAMissingSource(bool preferredIsNull, bool fallbackIsNull)
    {
        var preferred = preferredIsNull ? null : new FakeSource(null);
        var fallback = fallbackIsNull ? null : new FakeSource(null);

        Assert.Throws<ArgumentNullException>(() => new PreferredTrackSource(preferred!, fallback!));
    }
}
