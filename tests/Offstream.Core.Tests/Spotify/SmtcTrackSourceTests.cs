using Offstream.Core.Spotify.Smtc;
using Xunit;

namespace Offstream.Core.Tests.Spotify;

/// <summary>
/// The SMTC mapping. Every rule here is a pure function of the snapshot, which is the whole
/// reason the WinRT call sits behind <see cref="ISmtcSessions"/> — none of this needs a media
/// session, an audio endpoint, or Spotify installed.
/// </summary>
public sealed class SmtcTrackSourceTests
{
    private sealed class FakeSessions(SmtcSnapshot? snapshot, Exception? fault = null) : ISmtcSessions
    {
        public int Calls { get; private set; }

        public Task<SmtcSnapshot?> GetSpotifySnapshotAsync(CancellationToken cancellationToken = default)
        {
            Calls++;

            return fault is not null ? Task.FromException<SmtcSnapshot?>(fault) : Task.FromResult(snapshot);
        }
    }

    private static SmtcSnapshot Playing(string? artist, string? title, string? album = null) =>
        new(artist, title, album, IsPlaying: true);

    [Fact]
    public async Task NoSession_ReportsNothing()
    {
        var source = new SmtcTrackSource(new FakeSessions(null));

        Assert.Null(await source.GetCurrentTrackAsync());
    }

    /// <summary>
    /// The reason SMTC is preferred: artist and title arrive as separate fields, so nothing has to
    /// be split on a separator that can legitimately appear inside either of them.
    /// </summary>
    [Fact]
    public async Task ASession_ReportsArtistAndTitleWithoutParsing()
    {
        var source = new SmtcTrackSource(new FakeSessions(Playing("Lionel Richie", "Hello", "Can't Slow Down")));

        var track = await source.GetCurrentTrackAsync();

        Assert.NotNull(track);
        Assert.Equal("Lionel Richie", track.Artist);
        Assert.Equal("Hello", track.Title);
        Assert.Equal("Can't Slow Down", track.Album);
        Assert.True(track.Playing);
        Assert.False(track.Ad);
    }

    /// <summary>
    /// A title containing " - " is exactly the case the window-title parser has to guess at and
    /// this one does not.
    /// </summary>
    [Fact]
    public async Task ATitleContainingTheSeparator_SurvivesIntact()
    {
        var source = new SmtcTrackSource(new FakeSessions(Playing("Artist", "Song - Live at Wembley")));

        var track = await source.GetCurrentTrackAsync();

        Assert.Equal("Artist", track!.Artist);
        Assert.Equal("Song - Live at Wembley", track.Title);
    }

    /// <summary>
    /// An undetected advertisement is written to the library as a song, so both of Spotify's
    /// tells are honoured: the placeholder title, and playing with no artist attached.
    /// </summary>
    [Theory]
    [InlineData("Advertisement", null)]
    [InlineData("Advertisement", "")]
    [InlineData("advertisement", "Artist")]
    [InlineData("Some sponsor", "")]
    [InlineData("Some sponsor", "   ")]
    public async Task AnAdvertisement_IsRecognised(string title, string? artist)
    {
        var source = new SmtcTrackSource(new FakeSessions(Playing(artist, title)));

        Assert.True((await source.GetCurrentTrackAsync())!.Ad);
    }

    /// <summary>
    /// The placeholder lingers after playback stops. Calling that an ad would suppress the next
    /// real track, so nothing paused is ever an ad.
    /// </summary>
    [Theory]
    [InlineData("Advertisement", null)]
    [InlineData("Some sponsor", "")]
    public async Task APausedSession_IsNeverAnAdvertisement(string title, string? artist)
    {
        var source = new SmtcTrackSource(new FakeSessions(new SmtcSnapshot(artist, title, null, IsPlaying: false)));

        var track = await source.GetCurrentTrackAsync();

        Assert.False(track!.Ad);
        Assert.False(track.Playing);
    }

    /// <summary>Blank fields become null rather than empty strings, so tagging skips them.</summary>
    [Fact]
    public async Task BlankFields_BecomeNull()
    {
        var source = new SmtcTrackSource(new FakeSessions(new SmtcSnapshot("  ", "  ", "  ", IsPlaying: false)));

        var track = await source.GetCurrentTrackAsync();

        Assert.Null(track!.Artist);
        Assert.Null(track.Title);
        Assert.Null(track.Album);
    }

    [Fact]
    public async Task SurroundingWhitespace_IsTrimmed()
    {
        var source = new SmtcTrackSource(new FakeSessions(Playing(" Artist ", " Title ", " Album ")));

        var track = await source.GetCurrentTrackAsync();

        Assert.Equal("Artist", track!.Artist);
        Assert.Equal("Title", track.Title);
        Assert.Equal("Album", track.Album);
    }

    [Fact]
    public void Constructor_RejectsAMissingSessionReader() =>
        Assert.Throws<ArgumentNullException>(() => new SmtcTrackSource(null!));
}
