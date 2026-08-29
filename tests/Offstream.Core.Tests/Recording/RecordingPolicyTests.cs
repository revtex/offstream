using Offstream.Core.Metadata;
using Offstream.Core.Recording;
using Offstream.Core.Settings;
using Offstream.Core.Spotify;
using Xunit;

namespace Offstream.Core.Tests.Recording;

/// <summary>
/// Ported from the reference suite's <c>WatcherTests</c>, assertions unchanged.
/// </summary>
/// <remarks>
/// The original constructed the whole <c>Watcher</c> — form mock, audio session mock, file
/// system and a recorder-task list — to assert on a single boolean. These call the rules
/// directly, which is the point of splitting them out.
/// </remarks>
public sealed class RecordingPolicyTests
{
    private readonly RecordingSettings _settings = new()
    {
        OutputTemplate = FileNameTemplateDefault,
        RecordSelection = RecordSelection.KnownTracksOnly,
    };

    private const string FileNameTemplateDefault = "{artist} - {title}";

    private RecordingPolicy Policy => new(_settings);

    private static Track NormalPlaying() =>
        new() { Artist = "A", Title = "B", Ad = false, Playing = true };

    private static Track UnknownPlaying(string title) =>
        new() { Artist = title, Title = null, Ad = false, Playing = true };

    // ---- IsRecordUnknownActive --------------------------------------------

    [Theory]
    [InlineData(SpotifyWindowTitles.Spotify)]
    [InlineData(SpotifyWindowTitles.SpotifyFree)]
    [InlineData(SpotifyWindowTitles.SpotifyPremium)]
    public void IsRecordUnknownActive_FalsyWhenSpotifyInactive(string title)
    {
        _settings.RecordSelection = RecordSelection.EverythingExceptAds;

        Assert.False(Policy.IsRecordUnknownActive(UnknownPlaying(title)));
    }

    [Fact]
    public void IsRecordUnknownActive_FalsyWhenSpotifyAdPlaying()
    {
        _settings.RecordSelection = RecordSelection.EverythingExceptAds;
        var ad = new Track { Artist = SpotifyWindowTitles.Advertisement, Ad = true, Playing = true };

        Assert.False(Policy.IsRecordUnknownActive(ad));
    }

    [Fact]
    public void IsRecordUnknownActive_FalsyWhenDisabledAndAnyTitlePlaying()
    {
        _settings.RecordSelection = RecordSelection.KnownTracksOnly;

        Assert.False(Policy.IsRecordUnknownActive(UnknownPlaying("Podcast Episode 12")));
    }

    [Fact]
    public void IsRecordUnknownActive_TruthyWhenAnyTitlePlaying()
    {
        _settings.RecordSelection = RecordSelection.EverythingExceptAds;

        Assert.True(Policy.IsRecordUnknownActive(UnknownPlaying("Podcast Episode 12")));
    }

    [Fact]
    public void IsRecordUnknownActive_TruthyWhenAnyTitlePlayingAsAd()
    {
        _settings.RecordSelection = RecordSelection.Everything;
        var ad = new Track { Artist = SpotifyWindowTitles.Advertisement, Ad = true, Playing = true };

        Assert.True(Policy.IsRecordUnknownActive(ad));
    }

    [Fact]
    public void IsRecordUnknownActive_FalsyWhenNormalTrackIsPlaying()
    {
        _settings.RecordSelection = RecordSelection.EverythingExceptAds;

        Assert.False(Policy.IsRecordUnknownActive(NormalPlaying()));
    }

    /// <summary>
    /// The strictest selection discards an advertisement, which the three booleans this
    /// replaced could only express by one of them vetoing the other two.
    /// </summary>
    [Fact]
    public void IsRecordUnknownActive_FalsyForAnAdWhenOnlyKnownTracksWanted()
    {
        _settings.RecordSelection = RecordSelection.KnownTracksOnly;

        var ad = new Track { Artist = SpotifyWindowTitles.Advertisement, Ad = true, Playing = true };

        Assert.False(Policy.IsRecordUnknownActive(ad));
    }

    /// <summary>
    /// And a podcast, not only an advertisement. The setting is named for the whole of what it
    /// does, which is the half its predecessor ("mute advertisements") never admitted to.
    /// </summary>
    [Fact]
    public void IsRecordUnknownActive_FalsyForAnyUnknownWhenOnlyKnownTracksWanted()
    {
        _settings.RecordSelection = RecordSelection.KnownTracksOnly;

        Assert.False(Policy.IsRecordUnknownActive(UnknownPlaying("Podcast Episode 12")));
    }

    /// <summary>
    /// The widest selection takes an advertisement as well, which is the one thing the middle
    /// value holds back.
    /// </summary>
    [Fact]
    public void IsRecordUnknownActive_TruthyForAnAdOnlyAtTheWidestSelection()
    {
        var ad = new Track { Artist = SpotifyWindowTitles.Advertisement, Ad = true, Playing = true };

        _settings.RecordSelection = RecordSelection.EverythingExceptAds;
        Assert.False(Policy.IsRecordUnknownActive(ad));

        _settings.RecordSelection = RecordSelection.Everything;
        Assert.True(Policy.IsRecordUnknownActive(ad));
    }

    // ---- IsTypeAllowed -----------------------------------------------------

    [Theory]
    [InlineData(RecordSelection.KnownTracksOnly, false, true)]
    [InlineData(RecordSelection.EverythingExceptAds, false, true)]
    [InlineData(RecordSelection.KnownTracksOnly, true, false)]
    [InlineData(RecordSelection.EverythingExceptAds, true, false)]
    public void IsTypeAllowed_ReturnsExpectedResults(RecordSelection selection, bool isIdleSpotify, bool expected)
    {
        var track = NormalPlaying();

        if (isIdleSpotify)
        {
            track.Playing = false;
            track.Artist = SpotifyWindowTitles.Spotify;
            track.Title = null;
        }

        _settings.RecordSelection = selection;

        Assert.Equal(expected, Policy.IsTypeAllowed(track));
    }

    // ---- IsNewTrack --------------------------------------------------------

    [Fact]
    public void IsNewTrack_ReturnsExpectedResults()
    {
        var current = new Track { Artist = SpotifyWindowTitles.SpotifyFree };

        Assert.False(RecordingPolicy.IsNewTrack(current, null));
        Assert.False(RecordingPolicy.IsNewTrack(current, new Track()));
        Assert.False(RecordingPolicy.IsNewTrack(current, new Track { Artist = SpotifyWindowTitles.SpotifyFree }));
        Assert.True(RecordingPolicy.IsNewTrack(current, new Track { Artist = "Artist", Title = "Title" }));

        // A title with no artist is the media session part-way through a read, not a track
        // change. Treating it as one ended the take mid-song and started a second recording of
        // the same track when the artist arrived a moment later.
        Assert.False(RecordingPolicy.IsNewTrack(current, new Track { Title = "Title" }));
    }

    [Fact]
    public void IsNewTrack_WithNoCurrentTrack_TreatsAnyRealTrackAsNew() =>
        Assert.True(RecordingPolicy.IsNewTrack(null, new Track { Artist = "Artist", Title = "Title" }));

    // ---- IsMaxOrderNumberAsFileExceeded -----------------------------------

    [Theory]
    [InlineData(true, 10000, true)]
    [InlineData(true, 9999, true)]
    [InlineData(false, 9999, false)]
    [InlineData(true, 9998, false)]
    public void IsMaxOrderNumberAsFileExceeded_ReturnsExpectedResults(bool enabled, int orderNumber, bool expected)
    {
        _settings.OutputTemplate = enabled ? "{count:0000} {title}" : "{title}";
        _settings.OrderNumberInMediaTagEnabled = true;
        _settings.InternalOrderNumber = orderNumber;

        Assert.Equal(expected, Policy.IsMaxOrderNumberAsFileExceeded);
    }
}
