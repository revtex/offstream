namespace Offstream.Core.Metadata.Library;

/// <summary>Where one scanned file has got to.</summary>
/// <remarks>
/// The states a row moves through, and every one of them is reachable from the UI. There is no
/// "error" state that swallows the reason: <see cref="Failed"/> always travels with the text that
/// explains it, because "failed" on its own tells the user nothing they can act on — a locked
/// file, a rate limit and a track that simply is not in the catalogue want three different
/// responses from them.
/// </remarks>
public enum LibraryTrackStatus
{
    /// <summary>Scanned, and missing at least one of title, artist or album.</summary>
    Untagged,

    /// <summary>
    /// Scanned and already complete, so auto-fetch leaves it alone.
    /// </summary>
    /// <remarks>
    /// Not a success state — nothing has been done to this file. It means "there is nothing to
    /// ask about", which is why a fetch skips it and costs no request. The user can still force
    /// one on a single row when the tags are complete but wrong.
    /// </remarks>
    Matched,

    /// <summary>A lookup is in flight.</summary>
    Fetching,

    /// <summary>A lookup returned something, held in memory and awaiting review.</summary>
    Fetched,

    /// <summary>The suggested tags have been written into the file.</summary>
    Saved,

    /// <summary>Something went wrong, and the reason travels with it.</summary>
    Failed,
}
