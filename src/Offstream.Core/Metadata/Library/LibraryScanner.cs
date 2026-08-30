using System.IO.Abstractions;
using Offstream.Core.Spotify;
using Serilog;

namespace Offstream.Core.Metadata.Library;

/// <summary>What one scan of a folder found.</summary>
/// <param name="Tracks">Every taggable file, in the order they were found.</param>
/// <param name="SkippedWaveFiles">
/// How many <c>.wav</c> files were passed over. Reported rather than hidden: Offstream records
/// WAV, so a user who chose that format would otherwise open this page onto an empty list and
/// have no way to tell a broken scan from an unsupported one.
/// </param>
/// <param name="Failures">Files that could not be read at all, each with its reason.</param>
public readonly record struct LibraryScan(
    IReadOnlyList<LibraryTrack> Tracks,
    int SkippedWaveFiles,
    IReadOnlyList<string> Failures)
{
    /// <summary>An empty result, for a folder that does not exist.</summary>
    public static LibraryScan Empty => new([], 0, []);
}

/// <summary>Finds taggable audio files in a folder and reads what they already carry.</summary>
public interface ILibraryScanner
{
    /// <summary>Scans <paramref name="directory"/> and everything under it.</summary>
    /// <remarks>Never throws for a bad file: an unreadable one is reported in the result.</remarks>
    Task<LibraryScan> ScanAsync(string directory, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ILibraryScanner" />
/// <remarks>
/// Enumeration goes through <see cref="IFileSystem"/> so the walk is unit-testable, while reading
/// a tag goes through <see cref="ILibraryTagStore"/>, which needs a real file. Splitting them that
/// way is what lets the interesting cases — a nested folder, a file with no tags, a file that
/// cannot be opened — be tested without writing a valid MP3 for each one.
/// </remarks>
public sealed class LibraryScanner(IFileSystem fileSystem, ILibraryTagStore tagStore) : ILibraryScanner
{
    /// <summary>
    /// Containers with a tag format worth offering to edit.
    /// </summary>
    /// <remarks>
    /// <b><c>.wav</c> is missing on purpose.</b> Offstream can record it, so these files turn up
    /// in the very folder this page scans, but WAV has no tag container anyone agrees on and
    /// TagLib#'s support for what does exist is the weak case. Listing them would put rows on the
    /// page that look editable, accept a fetch, and then fail at the moment of saving — after the
    /// user has spent the API requests. Skipping them and saying so is the honest version.
    /// </remarks>
    private static readonly string[] TaggableExtensions = [".mp3", ".flac", ".m4a", ".opus", ".ogg"];

    /// <inheritdoc />
    public Task<LibraryScan> ScanAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        // The walk is synchronous file IO; running it on a worker keeps a slow or networked
        // folder off the UI thread, which is the only reason this method is async at all.
        return Task.Run(() => Scan(directory, cancellationToken), cancellationToken);
    }

    private LibraryScan Scan(string directory, CancellationToken cancellationToken)
    {
        if (!fileSystem.Directory.Exists(directory))
        {
            Log.Information("Metadata scan found no folder at {Directory}.", directory);

            return LibraryScan.Empty;
        }

        var tracks = new List<LibraryTrack>();
        var failures = new List<string>();
        var skippedWaveFiles = 0;

        foreach (var path in EnumerateFiles(directory, failures))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = fileSystem.Path.GetExtension(path);

            if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
            {
                skippedWaveFiles++;

                continue;
            }

            if (!TaggableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

            try
            {
                tracks.Add(new LibraryTrack(path, ReadOrInferTags(path)));
            }
            catch (LibraryTagException ex)
            {
                // One unreadable file must not end the scan — a single damaged download would
                // otherwise make the whole folder unusable.
                failures.Add(ex.Message);
                Log.Warning(ex, "Metadata scan could not read {Path}.", path);
            }
        }

        Log.Information(
            "Metadata scan of {Directory} found {Count} taggable file(s), skipped {Wave} WAV, {Failed} unreadable.",
            directory,
            tracks.Count,
            skippedWaveFiles,
            failures.Count);

        return new LibraryScan(tracks, skippedWaveFiles, failures);
    }

    /// <summary>Enumerates the tree, tolerating a folder the user cannot open.</summary>
    /// <remarks>
    /// Materialised rather than streamed because the enumerator itself throws partway through on
    /// an inaccessible subfolder, and there is no way to resume one. A music folder is thousands
    /// of entries, not millions, so holding the list costs nothing worth optimising.
    /// </remarks>
    private List<string> EnumerateFiles(string directory, List<string> failures)
    {
        try
        {
            return [.. fileSystem.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add($"'{directory}' could not be read completely: {ex.Message}");
            Log.Warning(ex, "Metadata scan could not enumerate {Directory}.", directory);

            return [];
        }
    }

    /// <summary>Reads the file's tags, falling back to its name for the two that matter.</summary>
    /// <remarks>
    /// The fallback is what makes the page useful at all: a file with no tags is exactly the file
    /// the user came here to fix, and a lookup needs an artist and a title to search on. Offstream's
    /// own default template is <c>{artist} - {title}</c>, so the name is usually carrying precisely
    /// the two fields the tags are missing. <see cref="SpotifyTitleParser"/> does the splitting
    /// rather than a fresh <c>Split('-')</c> here, because it already knows that the separator is
    /// <c>" - "</c> with spaces — a hyphen inside an artist's name is not a separator, and that
    /// distinction took the predecessor a long time to get right.
    /// </remarks>
    private Track ReadOrInferTags(string path)
    {
        var track = tagStore.Read(path);

        if (!string.IsNullOrWhiteSpace(track.Artist) && !string.IsNullOrWhiteSpace(track.Title))
        {
            return track;
        }

        var name = fileSystem.Path.GetFileNameWithoutExtension(path);
        var parts = SpotifyTitleParser.SplitOnDash(name, 2);

        // Assigned through the ordinary setters, so a provider can still override either.
        if (parts.Length >= 2)
        {
            track.Artist ??= SpotifyTitleParser.TagAt(parts, 1);
            track.Title ??= SpotifyTitleParser.TagAt(parts, 2);
        }
        else
        {
            // No " - " to split on, so the name is a title and nothing else. Putting it in both
            // fields would be worse than leaving one empty: a search for artist "Track 03" and
            // title "Track 03" matches nothing, and the row would claim an artist the file never
            // supplied.
            track.Title ??= name;
        }

        return track;
    }
}
