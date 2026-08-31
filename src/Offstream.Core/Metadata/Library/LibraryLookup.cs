namespace Offstream.Core.Metadata.Library;

/// <summary>
/// The rule that a lookup on the Metadata page adds tags and never takes one away.
/// </summary>
/// <remarks>
/// <para>
/// Every provider assigns its fields unconditionally, which is right where they were written: a
/// recording starts as an empty track and the provider is the only source there is. On this page
/// the track starts as the file's own tags, so a provider that knows the song but has nothing to
/// say about one field hands back a blank that overwrites something the user curated. Spotify
/// returns an empty genre list for most of its catalogue since late 2024 and Last.fm returns none
/// for an artist nobody has tagged, so this is the common case rather than the odd one.
/// </para>
/// <para>
/// Nothing ever reached the file — the writer skips empty values — but the row reported a change
/// Save would not make, and once the page started showing before-and-after it read as an offer to
/// erase.
/// </para>
/// <para>
/// <b>This is a helper, not the rule's only home.</b> It has to be applied at both call sites: the
/// automatic lookup in <c>MetadataViewModel.FetchOneAsync</c>, which covers whichever provider in
/// the chain answers and any provider added later, and <see cref="SpotifyCatalogEnricher"/>, which
/// the manual "Use this" path reaches without going through the first. The logic lives here so the
/// two cannot drift — it was once fixed inside the Spotify path alone and the Last.fm fallback
/// still had the hole.
/// </para>
/// <para>
/// Title, artist and album are deliberately not on the list. Replacing those is what a match is
/// <i>for</i>, and the manual picker exists precisely to correct them.
/// </para>
/// <para>
/// <see cref="Track.Performers"/> is off the list for a different reason: it is not independent of
/// the artist, it is the same tag read whole, and <c>TagLibTagStore</c> decides what to write with
/// it by asking whether it still starts with <see cref="Track.Artist"/>. Restoring the old list
/// under a new artist would make those two disagree, and the store would then discard the list it
/// had just been handed back. Leaving it alone gives the right answer either way — a lookup that
/// changed the artist writes the new one, and a lookup that did not keeps every name the file had.
/// </para>
/// </remarks>
public static class LibraryLookup
{
    /// <summary>Takes the "before" a lookup will be measured against.</summary>
    public static Track Snapshot(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        return new Track(track);
    }

    /// <summary>Puts back every field the lookup left empty.</summary>
    /// <param name="track">The enriched track, edited in place.</param>
    /// <param name="before">What <see cref="Snapshot"/> captured before enrichment.</param>
    public static void KeepWhatWasThere(Track track, Track before)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(before);

        track.Year ??= before.Year;
        track.AlbumPosition ??= before.AlbumPosition;
        track.AlbumTrackCount ??= before.AlbumTrackCount;
        track.Disc ??= before.Disc;

        if (track.Genres is not { Length: > 0 }) track.Genres = before.Genres;
        if (track.AlbumArtists is not { Length: > 0 }) track.AlbumArtists = before.AlbumArtists;
        if (string.IsNullOrWhiteSpace(track.Copyright)) track.Copyright = before.Copyright;
        if (string.IsNullOrWhiteSpace(track.ReleaseDate)) track.ReleaseDate = before.ReleaseDate;
    }
}
