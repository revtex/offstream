using Offstream.Core.Spotify;

namespace Offstream.Core.Naming;

/// <summary>
/// The destination of one recording, assembled from base path, folders, name and extension.
/// </summary>
/// <remarks>
/// Ported from the reference implementation's <c>OutputFile</c>. The counter suffix is
/// deliberately absent for the first file and " 2", " 3"… thereafter, which is what the
/// duplicate policy expects and what existing user libraries already look like.
/// </remarks>
public sealed class OutputFile
{
    private const int FirstCount = 1;

    private string? _mediaFile;

    /// <summary>File name without extension. Diacritics and invalid characters are stripped on set.</summary>
    public string? MediaFile
    {
        get => _mediaFile;
        set => _mediaFile = PathText.RemoveDiacritics(value);
    }

    public string? BasePath { get; set; }
    public string? FoldersPath { get; set; }
    public string? Extension { get; set; }

    private int Count { get; set; } = FirstCount;

    /// <summary>Moves to the next candidate name when the current one is taken.</summary>
    public void Increment() => Count++;

    /// <summary>Full path to write to, or null when the name is an idle/advertisement placeholder.</summary>
    public string? ToMediaFilePath() =>
        _mediaFile.IsNullOrAdOrIdle()
            ? null
            : OutputPaths.ConcatPaths(BasePath, FoldersPath, $"{_mediaFile}{CountSuffix()}.{Extension}");

    /// <summary>Display form, relative to the output root.</summary>
    public override string ToString() =>
        _mediaFile.IsNullOrAdOrIdle()
            ? string.Empty
            : OutputPaths.ConcatPaths("..", FoldersPath, $"{_mediaFile}{CountSuffix()}.{Extension}");

    private string CountSuffix() => Count > FirstCount ? $" {Count}" : string.Empty;
}
