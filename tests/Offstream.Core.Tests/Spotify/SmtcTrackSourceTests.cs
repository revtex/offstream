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

    // ---- the metadata floor: what the client knows, kept for when a provider cannot help ----

    /// <summary>
    /// Album artist and track number come across too, because they are what the file is tagged
    /// with when no provider is configured or the configured one cannot match the track.
    /// </summary>
    [Fact]
    public async Task ASession_ReportsAlbumArtistAndTrackNumber()
    {
        var snapshot = new SmtcSnapshot(
            "Artist",
            "Title",
            "Album",
            IsPlaying: true,
            AlbumArtist: "Album Artist",
            TrackNumber: 4,
            AlbumTrackCount: 12);

        var track = await new SmtcTrackSource(new FakeSessions(snapshot)).GetCurrentTrackAsync();

        Assert.NotNull(track);
        Assert.Equal(["Album Artist"], track!.AlbumArtists!);
        Assert.Equal(4, track.AlbumPosition);

        // The half that turns a track tag of "4" into "4/12".
        Assert.Equal(12, track.AlbumTrackCount);
    }

    /// <summary>An unreported count is not a zero-track album, exactly as for the position.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public async Task AnAlbumTrackCountThatIsNotACountIsNotReported(int? reported)
    {
        var snapshot = new SmtcSnapshot(
            "Artist", "Title", "Album", IsPlaying: true, AlbumTrackCount: reported);

        var track = await new SmtcTrackSource(new FakeSessions(snapshot)).GetCurrentTrackAsync();

        Assert.Null(track!.AlbumTrackCount);
    }

    /// <summary>Spotify numbers from 1, so a zero means "not reported" rather than a zeroth track.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public async Task ATrackNumberThatIsNotAPositionIsNotReported(int? reported)
    {
        var snapshot = new SmtcSnapshot("Artist", "Title", "Album", IsPlaying: true, TrackNumber: reported);

        var track = await new SmtcTrackSource(new FakeSessions(snapshot)).GetCurrentTrackAsync();

        Assert.Null(track!.AlbumPosition);
    }

    /// <summary>
    /// A blank album artist is absent, not an album credited to the empty string — which would
    /// then win over a provider that did know, since the mappers fill rather than clear.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankAlbumArtistIsNotReported(string? reported)
    {
        var snapshot = new SmtcSnapshot("Artist", "Title", "Album", IsPlaying: true, AlbumArtist: reported);

        var track = await new SmtcTrackSource(new FakeSessions(snapshot)).GetCurrentTrackAsync();

        Assert.Null(track!.AlbumArtists);
    }

    [Fact]
    public async Task AnAlbumArtistIsTrimmed()
    {
        var snapshot = new SmtcSnapshot("Artist", "Title", "Album", IsPlaying: true, AlbumArtist: "  Album Artist  ");

        var track = await new SmtcTrackSource(new FakeSessions(snapshot)).GetCurrentTrackAsync();

        Assert.Equal(["Album Artist"], track!.AlbumArtists!);
    }

    /// <summary>The window-title path supplies neither, and must keep working unchanged.</summary>
    [Fact]
    public async Task ASnapshotWithoutTheOptionalFieldsReportsNeither()
    {
        var track = await new SmtcTrackSource(new FakeSessions(Playing("Artist", "Title", "Album")))
            .GetCurrentTrackAsync();

        Assert.Null(track!.AlbumArtists);
        Assert.Null(track.AlbumPosition);
    }
}
