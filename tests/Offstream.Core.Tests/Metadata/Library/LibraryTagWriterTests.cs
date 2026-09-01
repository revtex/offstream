using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Library;
using Xunit;

namespace Offstream.Core.Tests.Metadata.Library;

/// <summary>Committing a reviewed row, and refusing to be derailed by one bad file.</summary>
public sealed class LibraryTagWriterTests
{
    /// <summary>
    /// A row nothing changed is not rewritten.
    /// </summary>
    /// <remarks>
    /// Rewriting it would produce a byte-identical file with a new modified time, which is enough
    /// to make a sync client re-upload a folder that did not actually change.
    /// </remarks>
    [Fact]
    public async Task Save_SkipsARowWithNoChanges()
    {
        var store = new RecordingTagStore();
        var track = Scanned("A", "T", "Al");

        var result = await new LibraryTagWriter(store, new NoCoverArt()).SaveAsync(track);

        Assert.True(result.Saved);
        Assert.Empty(store.Written);
    }

    /// <summary>
    /// A file that already has cover art is not rewritten to give it the same cover art.
    /// </summary>
    /// <remarks>
    /// The skip above was defeated for every well-tagged file in a real library, because the
    /// scan reads the file's own picture into the row and the change check treated the presence
    /// of a picture as a change. The fake track in that test has none, so it never saw it.
    /// </remarks>
    [Fact]
    public async Task Save_SkipsAFileWhoseOnlyArtIsTheArtItAlreadyHad()
    {
        var store = new RecordingTagStore();
        var track = new LibraryTrack(
            @"C:\Music\a.mp3",
            new Track { Artist = "A", Title = "T", Album = "Al", AlbumArtImage = [1, 2, 3] });

        var result = await new LibraryTagWriter(store, new NoCoverArt()).SaveAsync(track);

        Assert.True(result.Saved);
        Assert.Empty(store.Written);
    }

    /// <summary>An edited row reaches the file.</summary>
    [Fact]
    public async Task Save_WritesAChangedRow()
    {
        var store = new RecordingTagStore();
        var track = Scanned("A", "T", "Al");

        track.Suggested.Album = "A Better Album";

        var result = await new LibraryTagWriter(store, new NoCoverArt()).SaveAsync(track);

        Assert.True(result.Saved);
        Assert.Single(store.Written);
    }

    /// <summary>
    /// A locked file becomes a result carrying the reason, never an exception.
    /// </summary>
    /// <remarks>
    /// One file the user happens to be playing must not end a run over the other two hundred.
    /// </remarks>
    [Fact]
    public async Task Save_ReportsAFailureInsteadOfThrowing()
    {
        var store = new RecordingTagStore { Failure = "'a.mp3' is in use or read-only." };
        var track = Scanned("A", "T", "Al");

        track.Suggested.Title = "Something New";

        var result = await new LibraryTagWriter(store, new NoCoverArt()).SaveAsync(track);

        Assert.False(result.Saved);
        Assert.Contains("in use", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Losing the cover art does not lose the tags.
    /// </summary>
    /// <remarks>
    /// The text is what the user asked for and is worth having on its own. Failing the row
    /// because an image host was briefly unreachable would report a failure for a file whose
    /// tags were written perfectly well.
    /// </remarks>
    [Fact]
    public async Task Save_StillWritesTagsWhenTheArtFetchFails()
    {
        var store = new RecordingTagStore();
        var track = Scanned("A", "T", "Al");

        track.Suggested.Title = "New";
        track.Suggested.AlbumArtUrl = "https://example.invalid/cover.jpg";

        var result = await new LibraryTagWriter(store, new FailingCoverArt()).SaveAsync(track);

        Assert.True(result.Saved);
        Assert.Single(store.Written);
    }

    private static LibraryTrack Scanned(string artist, string title, string album) =>
        new(@"C:\Music\a.mp3", new Track { Artist = artist, Title = title, Album = album });

    private sealed class RecordingTagStore : ILibraryTagStore
    {
        public List<string> Written { get; } = [];

        public string? Failure { get; init; }

        public Track Read(string path) => new();

        public void Write(string path, Track track, byte[]? coverArt)
        {
            if (Failure is not null) throw new LibraryTagException(Failure);

            Written.Add(path);
        }
    }

    private sealed class NoCoverArt : ICoverArtFetcher
    {
        public Task<string?> FetchAsync(Track track, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FailingCoverArt : ICoverArtFetcher
    {
        public Task<string?> FetchAsync(Track track, CancellationToken cancellationToken = default) =>
            Task.FromException<string?>(new HttpRequestException("unreachable"));
    }
}
