namespace Offstream.Core.Metadata.Library;

/// <summary>One audio file on disk, as the metadata manager sees it.</summary>
/// <remarks>
/// <para>
/// The pairing is the point: <see cref="Existing"/> is what the file carries now and never
/// changes, <see cref="Suggested"/> is what a provider proposed and what the user may edit. Both
/// are <see cref="Track"/> so the whole existing provider stack applies unchanged — enrichment,
/// cover-art fetching and genre fallback all take a <see cref="Track"/> and know nothing about
/// where it came from.
/// </para>
/// <para>
/// <b>Nothing here writes to disk.</b> A scanned track is a description of a file, so a run that
/// is cancelled, closed or abandoned halfway leaves every file exactly as it was found.
/// </para>
/// </remarks>
public sealed class LibraryTrack
{
    /// <summary>Creates a scanned track from what the file already carries.</summary>
    /// <param name="path">Full path to the audio file.</param>
    /// <param name="existing">Tags read from the file, or parsed from its name when it has none.</param>
    public LibraryTrack(string path, Track existing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(existing);

        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Existing = existing;

        // A copy, not the same instance: the suggestion is enriched in place and the row needs
        // the original beside it to show what would change. Sharing one Track would make every
        // "before" column silently become the "after" the moment a provider answered.
        Suggested = new Track(existing);

        Status = IsComplete(existing) ? LibraryTrackStatus.Matched : LibraryTrackStatus.Untagged;
    }

    /// <summary>Full path to the file.</summary>
    public string Path { get; }

    /// <summary>The file's own name, which is all the grid shows.</summary>
    public string FileName { get; }

    /// <summary>What the file carries today. Never mutated.</summary>
    public Track Existing { get; }

    /// <summary>What would be written, once a provider or the user has had a say.</summary>
    public Track Suggested { get; }

    /// <summary>Where this row has got to.</summary>
    public LibraryTrackStatus Status { get; set; }

    /// <summary>Why <see cref="Status"/> is <see cref="LibraryTrackStatus.Failed"/>, in the user's language.</summary>
    /// <remarks>
    /// Carries the provider's own words wherever there are any. Spotify's error bodies say things
    /// a status code cannot — which account is not on the allowlist, which quota was passed — and
    /// replacing that with a phrase of our own throws away the only part the user can act on.
    /// </remarks>
    public string? FailureReason { get; set; }

    /// <summary>Whether anything about <see cref="Suggested"/> differs from <see cref="Existing"/>.</summary>
    /// <remarks>
    /// What the Save button acts on. A row that came back from a provider saying exactly what the
    /// file already said is not worth rewriting the file for — and rewriting it would re-encode
    /// nothing but would still touch the modified time, which is enough to disturb anything
    /// syncing the folder.
    /// </remarks>
    public bool HasChanges =>
        !SameText(Existing.Title, Suggested.Title)
        || !SameText(Existing.Artist, Suggested.Artist)
        || !SameText(Existing.Album, Suggested.Album)
        || Existing.Year != Suggested.Year
        || !SameGenres(Existing.Genres, Suggested.Genres)
        || CoverArtWouldChange;

    /// <summary>Whether saving would put a different picture in the file than the one it has.</summary>
    /// <remarks>
    /// <para>
    /// This asked "does the suggestion have a picture at all", which is true of every file that
    /// was already tagged: the scan reads the file's own artwork into <see cref="Existing"/> and
    /// the copy constructor carries it into <see cref="Suggested"/>. So every well-tagged file in
    /// the library reported a change it did not have, which lit the Metadata page's "will change"
    /// badge on every row and — worse, because nothing showed it — sent every one of those files
    /// through a full rewrite on Save to embed the picture it already had.
    /// </para>
    /// <para>
    /// The second clause matches what the writer will actually do: it prefers the embedded picture
    /// and only downloads a provider's URL when there is none, so a URL beside artwork the file
    /// already has changes nothing.
    /// </para>
    /// </remarks>
    private bool CoverArtWouldChange =>
        !SameImage(Existing.AlbumArtImage, Suggested.AlbumArtImage)
        || (Suggested.AlbumArtImage is null or { Length: 0 }
            && !string.IsNullOrWhiteSpace(Suggested.AlbumArtUrl));

    /// <summary>Whether a track carries all three fields auto-fetch would go looking for.</summary>
    /// <remarks>
    /// Deliberately three fields and not more. Year, genre and cover art are frequently missing on
    /// files that are otherwise perfectly well tagged, and treating those as "incomplete" would
    /// send the whole library to the API on every run — which is the behaviour the skip exists to
    /// avoid.
    /// </remarks>
    internal static bool IsComplete(Track track) =>
        !string.IsNullOrWhiteSpace(track.Title)
        && !string.IsNullOrWhiteSpace(track.Artist)
        && !string.IsNullOrWhiteSpace(track.Album);

    private static bool SameText(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static bool SameGenres(string[]? left, string[]? right) =>
        (left ?? []).SequenceEqual(right ?? [], StringComparer.Ordinal);

    /// <summary>Whether two pictures are the same one.</summary>
    /// <remarks>
    /// The reference check is not an optimisation for the rare case — it is the common one. The
    /// copy constructor hands <see cref="Suggested"/> the very array <see cref="Existing"/> holds,
    /// so an untouched row settles here without reading a megabyte of image per row per redraw.
    /// </remarks>
    private static bool SameImage(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return (left?.Length ?? 0) == (right?.Length ?? 0);

        return left.AsSpan().SequenceEqual(right);
    }
}
