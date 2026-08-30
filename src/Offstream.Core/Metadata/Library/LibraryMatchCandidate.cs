namespace Offstream.Core.Metadata.Library;

/// <summary>One result of a manual catalogue search, as the user sees it before choosing.</summary>
/// <remarks>
/// <para>
/// A deliberately flat description rather than the provider's own object. The page needs four
/// strings and a picture to let someone tell two recordings of the same song apart, and keeping
/// the provider's type out of the view models means the second provider — whenever one arrives —
/// is a new implementation rather than a change to the page.
/// </para>
/// <para>
/// <see cref="Id"/> is what applying the choice acts on, because the search response does not
/// carry everything a tag needs: genre lives on the artist and the release date on the album, so
/// the chosen candidate is looked up properly rather than tagged from the search result alone.
/// </para>
/// </remarks>
/// <param name="Id">The provider's identifier for the track.</param>
/// <param name="Title">Track title.</param>
/// <param name="Artist">Artists, joined, as the search returned them.</param>
/// <param name="Album">Album name.</param>
/// <param name="Year">Release year, when the search result carried one.</param>
/// <param name="CoverArtUrl">A thumbnail to show beside the row, when there is one.</param>
public sealed record LibraryMatchCandidate(
    string Id,
    string Title,
    string Artist,
    string Album,
    int? Year,
    string? CoverArtUrl);
