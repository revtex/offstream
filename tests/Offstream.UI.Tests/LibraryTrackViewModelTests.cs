using Offstream.App.ViewModels;
using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Library;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// What one row of the Metadata page shows before anyone opens it.
/// </summary>
/// <remarks>
/// A row in the list is the only thing most files will ever show, so what it says has to be
/// enough to approve a save without picking it. These are the properties that say it.
/// </remarks>
public sealed class LibraryTrackViewModelTests
{
    /// <summary>The second line reads "artist · album".</summary>
    [Fact]
    public void Summary_JoinsTheArtistAndAlbum() =>
        Assert.Equal("Kate Bush · Hounds of Love", Row(album: "Hounds of Love").Summary);

    /// <summary>A file with no album gets no dangling separator.</summary>
    [Fact]
    public void Summary_IsJustTheArtistWithoutAnAlbum() =>
        Assert.Equal("Kate Bush", Row(album: null).Summary);

    /// <summary>Editing the album rewrites the line under the title.</summary>
    [Fact]
    public void Summary_FollowsAnEdit()
    {
        var row = Row(album: "Wrong");

        row.Album = "Hounds of Love";

        Assert.Equal("Kate Bush · Hounds of Love", row.Summary);
    }

    /// <summary>
    /// A row nothing has changed does not claim it will change anything.
    /// </summary>
    /// <remarks>
    /// The badge is how the rows Save would actually touch are picked out of a folder of a
    /// hundred. One that lights up on every row tells the user nothing.
    /// </remarks>
    [Fact]
    public void WillChange_IsFalseForAnUntouchedRow() => Assert.False(Row().HasPendingChanges);

    /// <summary>An edit lights it up.</summary>
    [Fact]
    public void WillChange_IsTrueAfterAnEdit()
    {
        var row = Row();

        row.Album = "Something Else";

        Assert.True(row.HasPendingChanges);
    }

    /// <summary>A saved row stops advertising it.</summary>
    /// <remarks>
    /// <see cref="LibraryTrack.Existing"/> is deliberately never updated — the row keeps showing
    /// what it changed *from* — so the underlying comparison stays true forever and the status is
    /// what has to carry the news.
    /// </remarks>
    [Fact]
    public void WillChange_IsFalseOnceTheRowIsSaved()
    {
        var row = Row();

        row.Album = "Something Else";
        row.Status = LibraryTrackStatus.Saved;

        Assert.False(row.HasPendingChanges);
    }

    /// <summary>Correcting a row after saving it lights the badge again.</summary>
    /// <remarks>
    /// The order is the whole test. Saving and then spotting a typo is the same two properties
    /// as the case above in the opposite sequence, and the badge has to answer differently: the
    /// file on disk now disagrees with the box again, so there is something for Save to do.
    /// </remarks>
    [Fact]
    public void WillChange_ComesBackWhenASavedRowIsEditedAgain()
    {
        var row = Row();

        row.Status = LibraryTrackStatus.Saved;
        row.Album = "Something Else";

        Assert.True(row.HasPendingChanges);
        Assert.Equal(LibraryTrackStatus.Fetched, row.Status);
    }

    /// <summary>A field that matches the file gets no "was" line.</summary>
    [Fact]
    public void WasLine_IsHiddenWhenTheFieldMatchesTheFile()
    {
        var row = Row();

        Assert.False(row.HasTitleChange);
        Assert.False(row.HasArtistChange);
        Assert.False(row.HasAlbumChange);
    }

    /// <summary>A field that differs gets one.</summary>
    [Fact]
    public void WasLine_ShowsForTheFieldThatChanged()
    {
        var row = Row();

        row.Artist = "Kate Bush & Big Boi";

        Assert.True(row.HasArtistChange);
        Assert.False(row.HasTitleChange);
        Assert.False(row.HasAlbumChange);
    }

    /// <summary>
    /// An empty box against an empty field is not a change.
    /// </summary>
    /// <remarks>
    /// A file with no album shows an em dash in the "was" position. Comparing the box against
    /// that placeholder rather than against nothing would mark every untagged file as edited
    /// before the user had touched it.
    /// </remarks>
    [Fact]
    public void WasLine_IsHiddenWhenTheFileAndTheBoxAreBothEmpty() =>
        Assert.False(Row(album: null).HasAlbumChange);

    /// <summary>A year a match brought lands in the year box, not in a read-only line.</summary>
    /// <remarks>
    /// Year and genre were shown beside the three editable boxes and could not be corrected,
    /// which made a wrong match's year unfixable without an outside tag editor. Every tag the
    /// recorder writes now has a box, so the check is that a fetched value reaches it.
    /// </remarks>
    [Fact]
    public void AFetchedYear_LandsInTheYearBox()
    {
        var track = new LibraryTrack(
            @"C:\Music\01 Cloudbusting.mp3",
            new Track { Title = "Cloudbusting", Artist = "Kate Bush", Album = "Hounds of Love" });

        var row = new LibraryTrackViewModel(track);

        track.Suggested.Year = 1985;
        row.RefreshFromSuggestion();

        Assert.Equal("1985", row.Year);
        Assert.True(row.HasYearChange);
        Assert.True(row.HasPendingChanges);
    }

    /// <summary>Genres come back as one line, because that is what the box holds.</summary>
    [Fact]
    public void FetchedGenres_ArriveCommaSeparated()
    {
        var track = new LibraryTrack(
            @"C:\Music\01 Cloudbusting.mp3",
            new Track { Title = "Cloudbusting", Artist = "Kate Bush", Album = "Hounds of Love" });

        var row = new LibraryTrackViewModel(track);

        track.Suggested.Genres = ["art pop", "art rock"];
        row.RefreshFromSuggestion();

        Assert.Equal("art pop, art rock", row.Genres);
        Assert.True(row.HasGenreChange);
    }

    /// <summary>Artwork the file already has is not a change, so no before-and-after appears.</summary>
    [Fact]
    public void Artwork_TheFileAlreadyHasIsNotAChange()
    {
        var picture = new byte[] { 1, 2, 3, 4 };

        var row = new LibraryTrackViewModel(new LibraryTrack(
            @"C:\Music\01 Cloudbusting.mp3",
            new Track
            {
                Title = "Cloudbusting",
                Artist = "Kate Bush",
                Album = "Hounds of Love",
                AlbumArtImage = picture,
            }));

        Assert.False(row.HasCoverArtChange);
    }

    /// <summary>An edit to a number the page never used to show still counts as a change.</summary>
    /// <remarks>
    /// The one that would go wrong quietly. <c>LibraryTrack.HasChanges</c> compared five fields,
    /// so a row where the user corrected only the disc number reported nothing to save and Save
    /// skipped it — the edit was accepted by the box and then dropped without a word.
    /// </remarks>
    [Fact]
    public void EditingOnlyTheDiscNumber_StillNeedsSaving()
    {
        var row = Row();

        Assert.False(row.HasPendingChanges);

        row.Disc = "2";

        Assert.True(row.HasDiscChange);
        Assert.True(row.HasPendingChanges);
    }

    /// <summary>Numbers are validated in the box rather than thrown away at the point of writing.</summary>
    [Theory]
    [InlineData("1985", false)]
    [InlineData("", false)]
    [InlineData("85", true)]
    [InlineData("-1", true)]
    [InlineData("nineteen", true)]
    public void AYear_MustBeFourDigitsOrNothing(string typed, bool expectedError)
    {
        var row = Row();

        row.Year = typed;

        Assert.Equal(expectedError, row.GetErrors(nameof(row.Year)).Cast<object>().Any());
    }

    /// <summary>
    /// A half-typed number leaves the tag alone rather than clearing it.
    /// </summary>
    /// <remarks>
    /// The boxes update on every keystroke, so backspacing over "1985" to retype it passes
    /// through "198", "19", "1" — and then through states that do not parse at all. Nulling the
    /// tag on those would erase a year while the user was in the middle of correcting it.
    /// </remarks>
    [Fact]
    public void AnUnparseableYear_LeavesTheTagWhereItWas()
    {
        var track = new LibraryTrack(
            @"C:\Music\01 Cloudbusting.mp3",
            new Track { Title = "Cloudbusting", Artist = "Kate Bush", Year = 1985 });

        var row = new LibraryTrackViewModel(track) { Year = "nineteen" };

        Assert.Equal(1985, track.Suggested.Year);
    }

    /// <summary>Clearing a box asks for nothing, which is not the same as asking for a blank.</summary>
    /// <remarks>
    /// The writer never writes an empty value over a real one, so a cleared box that reported a
    /// change would light the "will change" badge and then save nothing at all.
    /// </remarks>
    [Fact]
    public void ClearingABox_IsNotAChange()
    {
        var track = new LibraryTrack(
            @"C:\Music\01 Cloudbusting.mp3",
            new Track { Title = "Cloudbusting", Artist = "Kate Bush", Album = "Hounds of Love" });

        var row = new LibraryTrackViewModel(track) { Album = string.Empty };

        Assert.False(row.HasAlbumChange);
        Assert.False(row.HasPendingChanges);
    }

    /// <summary>A comma inside a name is part of the name, not a second name.</summary>
    /// <remarks>
    /// The genre box splits on commas because a genre list really is a list. Doing the same to
    /// the album artist box files "Earth, Wind &amp; Fire" under two acts — a worse corruption
    /// than the multi-value tag the box was added to expose.
    /// </remarks>
    [Fact]
    public void AnAlbumArtistWithACommaInIt_StaysOneName()
    {
        var track = new LibraryTrack(
            @"C:\Music\01 September.mp3",
            new Track { Title = "September", Artist = "Earth, Wind & Fire" });

        _ = new LibraryTrackViewModel(track) { AlbumArtist = "Earth, Wind & Fire" };

        Assert.Equal(["Earth, Wind & Fire"], track.Suggested.AlbumArtists!);
    }

    /// <summary>An album artist list nobody touched goes back the way it came.</summary>
    [Fact]
    public void AnUneditedAlbumArtistList_KeepsEveryName()
    {
        var track = new LibraryTrack(
            @"C:\Music\01 Under Pressure.mp3",
            new Track { Title = "Under Pressure", Artist = "Queen", AlbumArtists = ["Queen", "David Bowie"] });

        var row = new LibraryTrackViewModel(track);

        Assert.Equal("Queen, David Bowie", row.AlbumArtist);

        row.RefreshFromSuggestion();

        Assert.Equal(["Queen", "David Bowie"], track.Suggested.AlbumArtists!);

        // And the row stays quiet: a list that came back the way it went in is not a change, or
        // every multi-value file in the library would wear the "will change" badge.
        Assert.False(row.HasAlbumArtistChange);
        Assert.False(row.HasPendingChanges);
    }

    /// <summary>The thumbnail follows a match that brought a URL rather than bytes.</summary>
    /// <remarks>
    /// A lookup normally returns a link and no picture — the writer downloads it at save time —
    /// so a thumbnail bound to the decoded image alone showed the artwork the file already had
    /// while claiming to show what saving would do.
    /// </remarks>
    [Fact]
    public void CoverPreview_FollowsAUrlWhenTheMatchBroughtNoBytes()
    {
        var track = new LibraryTrack(
            @"C:\Music\01 Who Made Who.mp3",
            new Track { Title = "Who Made Who", Artist = "AC", Album = "Who Made Who" });

        var row = new LibraryTrackViewModel(track);

        track.Suggested.AlbumArtImage = null;
        track.Suggested.AlbumArtUrl = "https://example.invalid/discovery.jpg";
        row.RefreshFromSuggestion();

        var source = Assert.IsType<Uri>(row.SuggestedCoverArtSource);

        Assert.Equal("https://example.invalid/discovery.jpg", source.ToString());
    }

    /// <summary>Opening a row fills its search box, however the row was opened.</summary>
    /// <remarks>
    /// The query is built from the row as it stands now, not as the file was scanned, so a fetch
    /// or an edit in between is what gets searched for.
    /// </remarks>
    [Fact]
    public void Seeding_FillsTheSearchBoxFromWhatTheRowNowSays()
    {
        var row = Row();

        row.SeedMatchQuery();

        Assert.Equal("Kate Bush Running Up That Hill", row.MatchQuery);
    }

    /// <summary>Seeding again does not overwrite a query the user typed.</summary>
    [Fact]
    public void Seeding_LeavesAQueryTheUserAlreadyTyped()
    {
        var row = Row();

        row.MatchQuery = "cloudbusting";
        row.SeedMatchQuery();

        Assert.Equal("cloudbusting", row.MatchQuery);
    }

    private static LibraryTrackViewModel Row(
        string? title = "Running Up That Hill",
        string? artist = "Kate Bush",
        string? album = "Hounds of Love") =>
        new(new LibraryTrack(
            @"C:\Music\01 Running Up That Hill (A Deal With God) - 2018 Remaster.mp3",
            new Track { Title = title, Artist = artist, Album = album }));
}
