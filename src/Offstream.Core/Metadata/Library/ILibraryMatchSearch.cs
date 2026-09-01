namespace Offstream.Core.Metadata.Library;

/// <summary>Searching the catalogue by hand, for when the automatic match is wrong.</summary>
/// <remarks>
/// <para>
/// <b>Why this is separate from <see cref="Providers.IMetadataProvider"/>.</b> A provider answers
/// "what is this track", takes its question from the track's own fields, and either matches or
/// does not. That is the right shape for filling gaps and the wrong one for correcting a mistake:
/// the automatic path refuses any result whose artist disagrees with the file, which is exactly
/// the case where the file is wrong and the user knows better. Re-running it returns the same
/// answer however many times it is asked.
/// </para>
/// <para>
/// So this takes the user's words instead of the file's, returns several results rather than a
/// verdict, and leaves the choosing to the person who can see them.
/// </para>
/// </remarks>
public interface ILibraryMatchSearch
{
    /// <summary>Searches the catalogue for what the user typed.</summary>
    /// <param name="query">Free text, exactly as entered.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Up to a handful of candidates, best first, or empty when nothing matched.</returns>
    /// <exception cref="MetadataLookupException">
    /// The lookup failed for a reason the user can act on, carrying the provider's own message.
    /// </exception>
    Task<IReadOnlyList<LibraryMatchCandidate>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>Applies a chosen candidate to a track, filling in everything a tag needs.</summary>
    /// <param name="track">The suggestion to overwrite.</param>
    /// <param name="candidate">The result the user picked.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    Task ApplyAsync(
        Track track,
        LibraryMatchCandidate candidate,
        CancellationToken cancellationToken = default);
}
