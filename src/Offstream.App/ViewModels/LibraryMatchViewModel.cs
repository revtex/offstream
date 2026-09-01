using Offstream.Core.Metadata.Library;

namespace Offstream.App.ViewModels;

/// <summary>One result of a manual search, offered to replace the row's current suggestion.</summary>
/// <remarks>
/// Carries its own row, because the command that applies it lives on the page rather than on the
/// row — a candidate needs both to be useful, and a WPF <c>CommandParameter</c> is one object.
/// </remarks>
public sealed class LibraryMatchViewModel(LibraryTrackViewModel row, LibraryMatchCandidate candidate)
{
    /// <summary>The row this result would be applied to.</summary>
    public LibraryTrackViewModel Row { get; } =
        row ?? throw new ArgumentNullException(nameof(row));

    /// <summary>The result itself.</summary>
    public LibraryMatchCandidate Candidate { get; } =
        candidate ?? throw new ArgumentNullException(nameof(candidate));

    /// <summary>The track title.</summary>
    public string Title => Candidate.Title;

    /// <summary>
    /// Artist, album and year on one line — what actually tells two results apart.
    /// </summary>
    /// <remarks>
    /// The year is the discriminating field and is why it is here. A search for a song people
    /// have recorded twice returns the original and the remaster with identical titles and
    /// identical artists, and the release year is the only thing on the row that separates them.
    /// </remarks>
    public string Summary => Candidate.Year is { } year
        ? $"{Candidate.Artist} · {Candidate.Album} · {year}"
        : $"{Candidate.Artist} · {Candidate.Album}";
}
