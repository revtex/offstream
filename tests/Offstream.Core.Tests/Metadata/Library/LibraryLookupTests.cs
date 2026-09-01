using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Library;
using Xunit;

namespace Offstream.Core.Tests.Metadata.Library;

/// <summary>
/// A lookup on the Metadata page adds tags, and never takes one away.
/// </summary>
/// <remarks>
/// The rule was fixed once inside the Spotify path alone and the Last.fm fallback still had the
/// hole, which is why the logic is in one place with two call sites rather than written out
/// twice. These tests cover the logic; the call sites are covered where they live.
/// </remarks>
public sealed class LibraryLookupTests
{
    /// <summary>
    /// A provider with nothing to say about a field says nothing, not "empty".
    /// </summary>
    /// <remarks>
    /// Spotify returns an empty genre list for most of its catalogue since late 2024 and Last.fm
    /// returns none for an artist nobody has tagged, so a lookup that trusted the answer blanked
    /// a genre the user had curated. Nothing ever reached the file — the writer skips empty
    /// values — but the row reported a change Save would not make.
    /// </remarks>
    [Fact]
    public void EveryFieldTheLookupLeftEmpty_ComesBack()
    {
        var track = new Track
        {
            Genres = ["shoegaze"],
            Year = 1991,
            AlbumArtists = ["Slowdive"],
            AlbumPosition = 3,
            AlbumTrackCount = 9,
            Disc = 2,
            Copyright = "1991 Creation Records",
            ReleaseDate = "1991-11-04",
        };

        var before = LibraryLookup.Snapshot(track);

        track.Genres = [];
        track.Year = null;
        track.AlbumArtists = null;
        track.AlbumPosition = null;
        track.AlbumTrackCount = null;
        track.Disc = null;
        track.Copyright = null;
        track.ReleaseDate = null;

        LibraryLookup.KeepWhatWasThere(track, before);

        Assert.Equal(["shoegaze"], track.Genres!);
        Assert.Equal(1991, track.Year);
        Assert.Equal(["Slowdive"], track.AlbumArtists!);
        Assert.Equal(3, track.AlbumPosition);
        Assert.Equal(9, track.AlbumTrackCount);
        Assert.Equal(2, track.Disc);
        Assert.Equal("1991 Creation Records", track.Copyright);
        Assert.Equal("1991-11-04", track.ReleaseDate);
    }

    /// <summary>What the provider did answer is left exactly as it answered it.</summary>
    /// <remarks>
    /// The other half of the rule, and the reason this is not simply "keep the file's tags". A
    /// match that corrects a wrong year is the whole point of running one.
    /// </remarks>
    [Fact]
    public void WhatTheLookupAnswered_Wins()
    {
        var track = new Track { Genres = ["rock"], Year = 1990, Disc = 1 };
        var before = LibraryLookup.Snapshot(track);

        track.Genres = ["shoegaze"];
        track.Year = 1991;
        track.Disc = 2;

        LibraryLookup.KeepWhatWasThere(track, before);

        Assert.Equal(["shoegaze"], track.Genres!);
        Assert.Equal(1991, track.Year);
        Assert.Equal(2, track.Disc);
    }

    /// <summary>
    /// Title, artist and album are deliberately not restored.
    /// </summary>
    /// <remarks>
    /// Replacing those is what a match is for, and the manual picker exists precisely to correct
    /// them. Putting them back would make "Use this" unable to change the thing the user chose it
    /// to change.
    /// </remarks>
    [Fact]
    public void AMatchMayStillReplaceTheTitleAndArtist()
    {
        var track = new Track { Title = "Track 03", Artist = "Unknown", Album = "Unknown Album" };
        var before = LibraryLookup.Snapshot(track);

        track.Title = "Alison";
        track.Artist = "Slowdive";
        track.Album = "Souvlaki";

        LibraryLookup.KeepWhatWasThere(track, before);

        Assert.Equal("Alison", track.Title);
        Assert.Equal("Slowdive", track.Artist);
        Assert.Equal("Souvlaki", track.Album);
    }
}
