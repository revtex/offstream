using System.IO.Abstractions.TestingHelpers;
using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Library;
using Xunit;

namespace Offstream.Core.Tests.Metadata.Library;

/// <summary>
/// Finding the files, and deciding what each one already knows about itself.
/// </summary>
/// <remarks>
/// The walk is tested against <see cref="MockFileSystem"/> and the tag read against a stub, so
/// every interesting case here — a nested folder, a WAV, a file with no tags, one that cannot be
/// opened — costs no real audio file. Reading an actual tag is covered by
/// <see cref="TagLibTagStoreTests"/>, which needs one.
/// </remarks>
public sealed class LibraryScannerTests
{
    private const string Root = @"C:\Music";

    /// <summary>Every container with a tag format worth editing is picked up, at any depth.</summary>
    [Fact]
    public async Task Scan_FindsTaggableFilesInSubfolders()
    {
        var scanner = Build(new Dictionary<string, MockFileData>
        {
            [@"C:\Music\a.mp3"] = new(string.Empty),
            [@"C:\Music\Album\b.flac"] = new(string.Empty),
            [@"C:\Music\Album\Disc 2\c.m4a"] = new(string.Empty),
            [@"C:\Music\d.opus"] = new(string.Empty),
        });

        var scan = await scanner.ScanAsync(Root);

        Assert.Equal(4, scan.Tracks.Count);
        Assert.Empty(scan.Failures);
    }

    /// <summary>Files that are not audio are not rows.</summary>
    [Fact]
    public async Task Scan_IgnoresUnrelatedFiles()
    {
        var scanner = Build(new Dictionary<string, MockFileData>
        {
            [@"C:\Music\a.mp3"] = new(string.Empty),
            [@"C:\Music\cover.jpg"] = new(string.Empty),
            [@"C:\Music\notes.txt"] = new(string.Empty),
        });

        var scan = await scanner.ScanAsync(Root);

        Assert.Single(scan.Tracks);
    }

    /// <summary>
    /// WAV is counted rather than listed.
    /// </summary>
    /// <remarks>
    /// Offstream records WAV, so these turn up in the very folder this page scans. They are
    /// skipped because WAV has no tag container worth offering to edit — but they are *counted*,
    /// because a user who records WAV would otherwise open the page onto an empty list with
    /// nothing to distinguish that from a broken scan.
    /// </remarks>
    [Fact]
    public async Task Scan_SkipsWaveFilesButReportsHowMany()
    {
        var scanner = Build(new Dictionary<string, MockFileData>
        {
            [@"C:\Music\a.mp3"] = new(string.Empty),
            [@"C:\Music\b.wav"] = new(string.Empty),
            [@"C:\Music\c.WAV"] = new(string.Empty),
        });

        var scan = await scanner.ScanAsync(Root);

        Assert.Single(scan.Tracks);
        Assert.Equal(2, scan.SkippedWaveFiles);
    }

    /// <summary>A folder that is not there is an empty result, not an exception.</summary>
    [Fact]
    public async Task Scan_ReturnsEmptyForAMissingFolder()
    {
        var scan = await Build([]).ScanAsync(@"C:\Nowhere");

        Assert.Empty(scan.Tracks);
        Assert.Equal(0, scan.SkippedWaveFiles);
    }

    /// <summary>
    /// One unreadable file costs that file and nothing else.
    /// </summary>
    /// <remarks>
    /// The case that matters most: a single damaged download in a folder of two hundred must not
    /// make the whole folder unusable.
    /// </remarks>
    [Fact]
    public async Task Scan_ReportsAnUnreadableFileAndKeepsGoing()
    {
        var store = new StubTagStore
        {
            Failing = { @"C:\Music\broken.mp3" },
        };

        var scanner = new LibraryScanner(
            new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [@"C:\Music\good.mp3"] = new(string.Empty),
                [@"C:\Music\broken.mp3"] = new(string.Empty),
            }),
            store,
            new StubQualityReader());

        var scan = await scanner.ScanAsync(Root);

        Assert.Single(scan.Tracks);
        Assert.Single(scan.Failures);
    }

    /// <summary>
    /// A file with no tags falls back to its name, which is the shape Offstream writes.
    /// </summary>
    /// <remarks>
    /// The default template is <c>{artist} - {title}</c>, so an untagged recording is usually
    /// carrying exactly the two fields a lookup needs. Without this the page would be useless for
    /// the files it exists to fix.
    /// </remarks>
    [Fact]
    public async Task Scan_InfersArtistAndTitleFromTheFileName()
    {
        var scanner = Build(new Dictionary<string, MockFileData>
        {
            [@"C:\Music\Chvrches - The Mother We Share.mp3"] = new(string.Empty),
        });

        var track = (await scanner.ScanAsync(Root)).Tracks.Single();

        Assert.Equal("Chvrches", track.Existing.Artist);
        Assert.Equal("The Mother We Share", track.Existing.Title);
    }

    /// <summary>
    /// A name with no separator is a title, and the artist stays empty.
    /// </summary>
    /// <remarks>
    /// Putting the whole name in both fields would be worse than leaving one blank: the row would
    /// claim an artist the file never supplied, and a search for that artist and that title
    /// matches nothing anyway.
    /// </remarks>
    [Fact]
    public async Task Scan_LeavesArtistEmptyWhenTheNameHasNoSeparator()
    {
        var scanner = Build(new Dictionary<string, MockFileData>
        {
            [@"C:\Music\Track 03.mp3"] = new(string.Empty),
        });

        var track = (await scanner.ScanAsync(Root)).Tracks.Single();

        Assert.Null(track.Existing.Artist);
        Assert.Equal("Track 03", track.Existing.Title);
    }

    /// <summary>
    /// A hyphen inside a name is not a separator — only <c>" - "</c> with spaces is.
    /// </summary>
    /// <remarks>
    /// This is why the filename goes through <c>SpotifyTitleParser</c> rather than a fresh
    /// <c>Split('-')</c>: the distinction took the predecessor a long time to get right, and
    /// hyphenated artist names are common.
    /// </remarks>
    [Fact]
    public async Task Scan_DoesNotSplitAHyphenInsideAName()
    {
        var scanner = Build(new Dictionary<string, MockFileData>
        {
            [@"C:\Music\Jean-Michel Jarre - Oxygene.mp3"] = new(string.Empty),
        });

        var track = (await scanner.ScanAsync(Root)).Tracks.Single();

        Assert.Equal("Jean-Michel Jarre", track.Existing.Artist);
        Assert.Equal("Oxygene", track.Existing.Title);
    }

    /// <summary>Real tags win over the file name.</summary>
    [Fact]
    public async Task Scan_PrefersEmbeddedTagsOverTheFileName()
    {
        var store = new StubTagStore
        {
            Tags =
            {
                [@"C:\Music\Wrong - Name.mp3"] = new Track { Artist = "Real Artist", Title = "Real Title" },
            },
        };

        var scanner = new LibraryScanner(
            new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [@"C:\Music\Wrong - Name.mp3"] = new(string.Empty),
            }),
            store,
            new StubQualityReader());

        var track = (await scanner.ScanAsync(Root)).Tracks.Single();

        Assert.Equal("Real Artist", track.Existing.Artist);
        Assert.Equal("Real Title", track.Existing.Title);
    }

    /// <summary>A file carrying all three fields is complete, and auto-fetch will skip it.</summary>
    [Fact]
    public async Task Scan_MarksAFullyTaggedFileAsMatched()
    {
        var store = new StubTagStore
        {
            Tags =
            {
                [@"C:\Music\a.mp3"] = new Track { Artist = "A", Title = "T", Album = "Al" },
            },
        };

        var scanner = new LibraryScanner(
            new MockFileSystem(new Dictionary<string, MockFileData> { [@"C:\Music\a.mp3"] = new(string.Empty) }),
            store,
            new StubQualityReader());

        var track = (await scanner.ScanAsync(Root)).Tracks.Single();

        Assert.Equal(LibraryTrackStatus.Matched, track.Status);
    }

    /// <summary>A file missing an album is not complete, however good its other tags are.</summary>
    [Fact]
    public async Task Scan_MarksAFileWithNoAlbumAsUntagged()
    {
        var store = new StubTagStore
        {
            Tags = { [@"C:\Music\a.mp3"] = new Track { Artist = "A", Title = "T" } },
        };

        var scanner = new LibraryScanner(
            new MockFileSystem(new Dictionary<string, MockFileData> { [@"C:\Music\a.mp3"] = new(string.Empty) }),
            store,
            new StubQualityReader());

        Assert.Equal(LibraryTrackStatus.Untagged, (await scanner.ScanAsync(Root)).Tracks.Single().Status);
    }

    /// <summary>
    /// A file's own audio properties reach the row Save never touches them for. Covered here
    /// rather than with a dedicated fixture because the interesting behaviour is entirely in the
    /// wiring — the reader itself is <see cref="TagLibAudioQualityReaderTests"/>'s job.
    /// </summary>
    [Fact]
    public async Task Scan_CarriesTheQualityReaderResultOntoTheRow()
    {
        var reader = new StubQualityReader { Result = new AudioQuality(320, 44100, 0) };
        var scanner = new LibraryScanner(
            new MockFileSystem(new Dictionary<string, MockFileData> { [@"C:\Music\a.mp3"] = new(string.Empty) }),
            new StubTagStore(),
            reader);

        var track = (await scanner.ScanAsync(Root)).Tracks.Single();

        Assert.Equal(320, track.Quality.BitrateKbps);
    }

    private static LibraryScanner Build(Dictionary<string, MockFileData> files) =>
        new(new MockFileSystem(files), new StubTagStore(), new StubQualityReader());

    /// <summary>An <see cref="ILibraryTagStore"/> that answers from a dictionary.</summary>
    private sealed class StubTagStore : ILibraryTagStore
    {
        public Dictionary<string, Track> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Track Read(string path)
        {
            if (Failing.Contains(path)) throw new LibraryTagException($"'{path}' is damaged.");

            return Tags.TryGetValue(path, out var track) ? track : new Track();
        }

        public void Write(string path, Track track, byte[]? coverArt)
        {
            if (Failing.Contains(path)) throw new LibraryTagException($"'{path}' is in use.");

            Tags[path] = track;
        }
    }

    /// <summary>An <see cref="IAudioQualityReader"/> that answers with a fixed result.</summary>
    private sealed class StubQualityReader : IAudioQualityReader
    {
        public AudioQuality Result { get; init; } = AudioQuality.Unknown;

        public AudioQuality Read(string path) => Result;
    }
}
