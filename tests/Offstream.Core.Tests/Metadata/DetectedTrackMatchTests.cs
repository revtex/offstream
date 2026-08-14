using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Providers;
using Xunit;

namespace Offstream.Core.Tests.Metadata;

/// <summary>
/// Whether a provider's answer is the track that was detected.
/// </summary>
/// <remarks>
/// The regression these exist for: the media session reports a title verbatim, the window-title
/// parser reports one it has already split, and a provider reports a bare name with the artists
/// beside it. Comparing any two of those directly fails, and the failure is silent — four
/// retries, several seconds, then an untagged recording.
/// </remarks>
public sealed class DetectedTrackMatchTests
{
    private static Track Detected(string? artist, string? title) =>
        new() { Artist = artist, Title = title, Playing = true };

    /// <summary>
    /// The exact case from the bug report. The media session reported the whole
    /// "artist - title" string as the title, and Spotify answered with the name alone.
    /// </summary>
    [Fact]
    public void ABareNameMatchesADetectedTitleThatStillCarriesTheArtist()
    {
        var detected = Detected(artist: null, title: "ATB - 9Pm (Till I Come)");

        Assert.True(DetectedTrackMatch.Matches(detected, "9Pm (Till I Come)", ["ATB"]));
    }

    /// <summary>The window-title path, where artist and title were already separated.</summary>
    [Fact]
    public void ABareNameMatchesAnAlreadySplitTitle()
    {
        var detected = Detected("ATB", "9Pm (Till I Come)");

        Assert.True(DetectedTrackMatch.Matches(detected, "9Pm (Till I Come)", ["ATB"]));
    }

    /// <summary>
    /// The same qualifier arrives bracketed, dashed or squared depending on who is asked, and
    /// the words inside it are what identify the recording.
    /// </summary>
    [Theory]
    [InlineData("Song (Live)", "Song - Live")]
    [InlineData("Song - Live", "Song [Live]")]
    [InlineData("Song (Remastered 2011)", "Song - Remastered 2011")]
    public void PunctuationAroundAQualifierIsForgiven(string reported, string detectedTitle)
    {
        var detected = Detected("Artist", detectedTitle);

        Assert.True(DetectedTrackMatch.Matches(detected, reported, ["Artist"]));
    }

    [Theory]
    [InlineData("BEYONCÉ", "Beyonce")]
    [InlineData("Song   Name", "song name")]
    [InlineData("  Song Name  ", "Song Name")]
    public void CaseAccentsAndSpacingAreForgiven(string reported, string detectedTitle)
    {
        var detected = Detected("Artist", detectedTitle);

        Assert.True(DetectedTrackMatch.Matches(detected, reported, ["Artist"]));
    }

    /// <summary>
    /// The line this must not cross. Forgiving the brackets must not forgive the words inside
    /// them — a remix tagged as its original is a worse outcome than no tags at all.
    /// </summary>
    [Theory]
    [InlineData("Song", "Song (Live)")]
    [InlineData("Song (Radio Edit)", "Song (Extended Mix)")]
    [InlineData("9Pm (Till I Come)", "9Am (Till I Come)")]
    public void ADifferentRecordingDoesNotMatch(string reported, string detectedTitle)
    {
        var detected = Detected("Artist", detectedTitle);

        Assert.False(DetectedTrackMatch.Matches(detected, reported, ["Artist"]));
    }

    /// <summary>
    /// A collaboration credits every artist, and each source shows a different subset. Matching
    /// on the lead artist is what they agree on.
    /// </summary>
    [Fact]
    public void OnlyTheLeadArtistHasToAgree()
    {
        var detected = Detected(artist: null, title: "Calvin Harris - Feel So Close");

        Assert.True(DetectedTrackMatch.Matches(
            detected, "Feel So Close", ["Calvin Harris", "Someone Else", "A Third Name"]));
    }

    [Fact]
    public void AnArtistThatDisagreesDoesNotMatch()
    {
        var detected = Detected(artist: null, title: "Someone Else - 9Pm (Till I Come)");

        Assert.False(DetectedTrackMatch.Matches(detected, "9Pm (Till I Come)", ["ATB"]));
    }

    /// <summary>Nothing playing, or a provider with no name to give, is never a match.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyReportedTitleNeverMatches(string? reported)
    {
        var detected = Detected("Artist", "Title");

        Assert.False(DetectedTrackMatch.Matches(detected, reported, ["Artist"]));
    }

    /// <summary>
    /// A detected track with nothing in it must not match everything. Normalising both sides to
    /// the empty string is the trap this closes.
    /// </summary>
    [Fact]
    public void AnEmptyDetectedTitleNeverMatches()
    {
        var detected = Detected(artist: null, title: null);

        Assert.False(DetectedTrackMatch.Matches(detected, "Any Song", ["Any Artist"]));
    }

    [Fact]
    public void MissingArtistsAreTolerated()
    {
        var detected = Detected(artist: null, title: "9Pm (Till I Come)");

        Assert.True(DetectedTrackMatch.Matches(detected, "9Pm (Till I Come)", reportedArtists: null));
    }

    /// <summary>Punctuation-only titles reduce to nothing, and nothing matches nothing.</summary>
    [Fact]
    public void TitlesThatReduceToNothingNeverMatch()
    {
        var detected = Detected(artist: null, title: "---");

        Assert.False(DetectedTrackMatch.Matches(detected, "***", reportedArtists: null));
    }

    /// <summary>
    /// The case this guard was written for: Last.fm attributed ATB's "9Pm (Till I Come)" to a
    /// radio-show tracklist while the media session was reporting the album it actually plays from.
    /// </summary>
    [Fact]
    public void AlbumAgrees_IsFalse_ForADifferentRelease()
    {
        Assert.False(DetectedTrackMatch.AlbumAgrees(
            "Movin' Melodies",
            "A Cutie Who Hates Blu Ray (W/Séverine) - N10.AS - 09 - 23 - 2020"));
    }

    [Theory]
    [InlineData("Movin' Melodies", "Movin Melodies")]
    [InlineData("Movin' Melodies", "MOVIN' MELODIES")]
    // An edition suffix is more said about the same record, from either side.
    [InlineData("Movin' Melodies", "Movin' Melodies (Deluxe Edition)")]
    [InlineData("Movin' Melodies [Remastered]", "Movin' Melodies")]
    public void AlbumAgrees_IsTrue_ForTheSameRelease(string detected, string reported) =>
        Assert.True(DetectedTrackMatch.AlbumAgrees(detected, reported));

    /// <summary>
    /// A prefix only extends a name at a word boundary — otherwise every album whose title began
    /// with another's would swallow it.
    /// </summary>
    [Fact]
    public void AlbumAgrees_IsFalse_WhenTheNameOnlyStartsWithTheSameLetters() =>
        Assert.False(DetectedTrackMatch.AlbumAgrees("Ray", "Rayman"));

    /// <summary>
    /// Nothing to disagree with: the window-title parser never reports an album, and a provider
    /// that has none is not contradicting the one already on the track.
    /// </summary>
    [Theory]
    [InlineData(null, "Movin' Melodies")]
    [InlineData("Movin' Melodies", null)]
    [InlineData("   ", "Movin' Melodies")]
    [InlineData(null, null)]
    public void AlbumAgrees_IsTrue_WhenEitherSideIsUnknown(string? detected, string? reported) =>
        Assert.True(DetectedTrackMatch.AlbumAgrees(detected, reported));
}
