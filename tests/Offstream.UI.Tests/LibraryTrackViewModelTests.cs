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

    private static LibraryTrackViewModel Row(
        string? title = "Running Up That Hill",
        string? artist = "Kate Bush",
        string? album = "Hounds of Love") =>
        new(new LibraryTrack(
            @"C:\Music\01 Running Up That Hill (A Deal With God) - 2018 Remaster.mp3",
            new Track { Title = title, Artist = artist, Album = album }));
}
