using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Library;
using Xunit;

namespace Offstream.Core.Tests.Metadata.Library;

/// <summary>The before-and-after pairing a row is built on.</summary>
public sealed class LibraryTrackTests
{
    /// <summary>
    /// Enriching the suggestion does not disturb what the file was found carrying.
    /// </summary>
    /// <remarks>
    /// The load-bearing property of the whole page. If the two shared one <see cref="Track"/>,
    /// every "Currently" cell would silently become the suggestion the moment a provider
    /// answered, and the user would be comparing a value against itself.
    /// </remarks>
    [Fact]
    public void Suggested_IsIndependentOfExisting()
    {
        var track = new LibraryTrack(@"C:\Music\a.mp3", new Track { Artist = "Old", Title = "Old Title" });

        track.Suggested.Artist = "New";

        Assert.Equal("Old", track.Existing.Artist);
        Assert.Equal("New", track.Suggested.Artist);
    }

    /// <summary>All three fields present means there is nothing to ask a provider about.</summary>
    [Fact]
    public void Status_IsMatchedWhenEveryFieldIsPresent()
    {
        var track = new LibraryTrack(
            @"C:\Music\a.mp3",
            new Track { Artist = "A", Title = "T", Album = "Al" });

        Assert.Equal(LibraryTrackStatus.Matched, track.Status);
    }

    /// <summary>
    /// Year, genre and art missing does not make a file incomplete.
    /// </summary>
    /// <remarks>
    /// They are absent on plenty of well-tagged files, and counting them would send the whole
    /// library to the API on every run — which is the cost the skip exists to avoid.
    /// </remarks>
    [Fact]
    public void Status_IgnoresYearGenreAndArt()
    {
        var track = new LibraryTrack(
            @"C:\Music\a.mp3",
            new Track { Artist = "A", Title = "T", Album = "Al", Year = null, Genres = null });

        Assert.Equal(LibraryTrackStatus.Matched, track.Status);
    }

    [Theory]
    [InlineData(null, "T", "Al")]
    [InlineData("A", null, "Al")]
    [InlineData("A", "T", null)]
    public void Status_IsUntaggedWhenAnyFieldIsMissing(string? artist, string? title, string? album)
    {
        var track = new LibraryTrack(
            @"C:\Music\a.mp3",
            new Track { Artist = artist, Title = title, Album = album });

        Assert.Equal(LibraryTrackStatus.Untagged, track.Status);
    }

    /// <summary>A freshly scanned row has nothing to save.</summary>
    [Fact]
    public void HasChanges_IsFalseBeforeAnythingIsEdited()
    {
        var track = new LibraryTrack(
            @"C:\Music\a.mp3",
            new Track { Artist = "A", Title = "T", Album = "Al" });

        Assert.False(track.HasChanges);
    }

    [Fact]
    public void HasChanges_NoticesAnEditedField()
    {
        var track = new LibraryTrack(
            @"C:\Music\a.mp3",
            new Track { Artist = "A", Title = "T", Album = "Al" });

        track.Suggested.Album = "Different";

        Assert.True(track.HasChanges);
    }

    /// <summary>Art that was fetched is a change even when every word is the same.</summary>
    [Fact]
    public void HasChanges_NoticesFetchedCoverArt()
    {
        var track = new LibraryTrack(
            @"C:\Music\a.mp3",
            new Track { Artist = "A", Title = "T", Album = "Al" });

        track.Suggested.AlbumArtImage = [1, 2, 3];

        Assert.True(track.HasChanges);
    }

    /// <summary>
    /// <b>Artwork the file already has is not a change.</b>
    /// </summary>
    /// <remarks>
    /// The clause this pins used to read "the suggestion has a picture", which is true of every
    /// well-tagged file in a library: the scan reads the file's own artwork into
    /// <see cref="LibraryTrack.Existing"/> and the copy constructor carries it into
    /// <see cref="LibraryTrack.Suggested"/>. Every test above passed because none of them gave
    /// the file a picture to begin with. The visible symptom was a "will change" badge on all
    /// hundred and twenty-seven rows; the invisible one was every one of those files going
    /// through a full rewrite on Save to embed the picture it already had.
    /// </remarks>
    [Fact]
    public void HasChanges_IsFalseForAFileThatAlreadyHasArtwork()
    {
        var track = new LibraryTrack(
            @"C:\Music\a.mp3",
            new Track { Artist = "A", Title = "T", Album = "Al", AlbumArtImage = [1, 2, 3] });

        Assert.False(track.HasChanges);
    }

    /// <summary>A different picture is a change.</summary>
    [Fact]
    public void HasChanges_NoticesADifferentPicture()
    {
        var track = new LibraryTrack(
            @"C:\Music\a.mp3",
            new Track { Artist = "A", Title = "T", Album = "Al", AlbumArtImage = [1, 2, 3] });

        track.Suggested.AlbumArtImage = [9, 9, 9];

        Assert.True(track.HasChanges);
    }

    /// <summary>Art a provider offered to a file that has none is a change.</summary>
    [Fact]
    public void HasChanges_NoticesArtOfferedToAFileWithNone()
    {
        var track = new LibraryTrack(
            @"C:\Music\a.mp3",
            new Track { Artist = "A", Title = "T", Album = "Al" });

        track.Suggested.AlbumArtUrl = "https://example.invalid/cover.jpg";

        Assert.True(track.HasChanges);
    }

    /// <summary>
    /// A URL beside artwork the file already has changes nothing.
    /// </summary>
    /// <remarks>
    /// Matching <c>LibraryTagWriter.ResolveCoverArtAsync</c>, which prefers the embedded picture
    /// and only downloads a URL when there is none. Reporting a change the writer would not make
    /// is how a folder gets rewritten for nothing.
    /// </remarks>
    [Fact]
    public void HasChanges_IgnoresAnArtUrlWhenTheFileAlreadyHasAPicture()
    {
        var track = new LibraryTrack(
            @"C:\Music\a.mp3",
            new Track { Artist = "A", Title = "T", Album = "Al", AlbumArtImage = [1, 2, 3] });

        track.Suggested.AlbumArtUrl = "https://example.invalid/cover.jpg";

        Assert.False(track.HasChanges);
    }

    /// <summary>The file's own name is what the grid shows, not the whole path.</summary>
    [Fact]
    public void FileName_IsTheLeafOfThePath()
    {
        var track = new LibraryTrack(@"C:\Music\Album\a.mp3", new Track());

        Assert.Equal("a.mp3", track.FileName);
    }
}
