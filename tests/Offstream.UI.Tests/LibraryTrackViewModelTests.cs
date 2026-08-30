using Offstream.App.ViewModels;
using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Library;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// What one row of the Metadata page shows before anyone opens it.
/// </summary>
/// <remarks>
/// The collapsed row is the only thing most files will ever show, so what it says has to be
/// enough to approve a save without opening anything. These are the properties that say it.
/// </remarks>
public sealed class LibraryTrackViewModelTests
{
    /// <summary>A row starts closed.</summary>
    /// <remarks>
    /// The layout this replaced kept every row's fields open and fitted three files of a hundred
    /// and twenty-seven on screen. Density is the feature, so the default matters.
    /// </remarks>
    [Fact]
    public void ARow_StartsCollapsed() => Assert.False(Row().IsExpanded);

    /// <summary>The chevron opens and closes it.</summary>
    [Fact]
    public void ToggleExpand_OpensAndCloses()
    {
        var row = Row();

        row.ToggleExpandCommand.Execute(null);
        Assert.True(row.IsExpanded);

        row.ToggleExpandCommand.Execute(null);
        Assert.False(row.IsExpanded);
    }

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

    /// <summary>A row whose match changed nothing outside the three boxes shows no extra block.</summary>
    [Fact]
    public void MatchDetails_AreHiddenWhenTheBoxesSayItAll()
    {
        var row = Row();

        row.Title = "Something Else";

        Assert.False(row.HasMatchDetails);
    }

    /// <summary>A year the file did not have is a change the boxes cannot show.</summary>
    /// <remarks>
    /// This is the complaint the block answers. Before it, a row could say "will change" with
    /// title, artist and album all identical to the file and nothing on screen saying why —
    /// the match had brought a year, a genre or artwork, and Save writes all three.
    /// </remarks>
    [Fact]
    public void MatchDetails_ShowTheYearAMatchBrought()
    {
        var track = new LibraryTrack(
            @"C:\Music\01 Cloudbusting.mp3",
            new Track { Title = "Cloudbusting", Artist = "Kate Bush", Album = "Hounds of Love" });

        var row = new LibraryTrackViewModel(track);

        track.Suggested.Year = 1985;
        row.RefreshFromSuggestion();

        Assert.True(row.HasYearChange);
        Assert.True(row.HasMatchDetails);
        Assert.Equal("1985", row.SuggestedYear);
        Assert.True(row.HasPendingChanges);
    }

    /// <summary>Genres are shown the same way, joined for reading.</summary>
    [Fact]
    public void MatchDetails_ShowTheGenresAMatchBrought()
    {
        var track = new LibraryTrack(
            @"C:\Music\01 Cloudbusting.mp3",
            new Track { Title = "Cloudbusting", Artist = "Kate Bush", Album = "Hounds of Love" });

        var row = new LibraryTrackViewModel(track);

        track.Suggested.Genres = ["art pop", "art rock"];
        row.RefreshFromSuggestion();

        Assert.True(row.HasGenreChange);
        Assert.Equal("art pop, art rock", row.SuggestedGenres);
    }

    /// <summary>Artwork the file already has is not a change, so no before-and-after appears.</summary>
    [Fact]
    public void MatchDetails_IgnoreArtworkTheFileAlreadyHas()
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
        Assert.False(row.HasMatchDetails);
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
    /// The seeding used to hang off the toggle command, which only the title runs. A row opened
    /// by its chevron — the control that looks like the way to open one — got an empty box, and
    /// searching from there asked Spotify for nothing at all.
    /// </remarks>
    [Fact]
    public void Expanding_SeedsTheSearchBoxFromWhatTheRowNowSays()
    {
        var row = Row();

        row.IsExpanded = true;

        Assert.Equal("Kate Bush Running Up That Hill", row.MatchQuery);
    }

    /// <summary>Reopening a row does not overwrite a query the user typed.</summary>
    [Fact]
    public void Expanding_LeavesAQueryTheUserAlreadyTyped()
    {
        var row = Row();

        row.MatchQuery = "cloudbusting";
        row.IsExpanded = true;

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
