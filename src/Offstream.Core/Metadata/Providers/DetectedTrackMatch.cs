using System.Globalization;
using System.Text;

namespace Offstream.Core.Metadata.Providers;

/// <summary>
/// Decides whether the track a provider says is playing is the one that was detected.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two sides are not the same shape, and that is the whole problem.</b> A provider
/// answers with a bare track name — <c>9Pm (Till I Come)</c> — and separate artists. What was
/// detected depends on which source saw it: the Windows media session reports the title verbatim
/// and untouched, while the window-title parser has already split <c>ATB - 9Pm (Till I Come)</c>
/// into an artist and a title of its own. An earlier version compared the provider's name against
/// <see cref="Track.Title"/> after running only the provider's side through the window-title
/// splitter, which meant it was comparing a parsed string against an unparsed one for every track
/// the media session found — the common case since the media session became the primary source.
/// Every such track failed all four attempts and was recorded untagged, several seconds later.
/// </para>
/// <para>
/// So both sides are reduced to a common form and compared in both shapes: the title alone, and
/// the artist and title joined. A provider's bare name matches a detected <c>Artist - Title</c>
/// because joining the provider's own artist onto its name produces the same thing.
/// </para>
/// <para>
/// <b>Normalisation stops well short of fuzzy.</b> This runs at a track boundary, where the
/// wrong answer is not "no metadata" but a file tagged as a different song — so it forgives only
/// what the two sources genuinely disagree about: case, spacing, and the punctuation each one
/// chooses to wrap a qualifier in. Two different recordings never collapse onto one another.
/// </para>
/// </remarks>
public static class DetectedTrackMatch
{
    /// <summary>Whether <paramref name="reportedTitle"/> is the track that was detected.</summary>
    /// <param name="detected">The track as the poller saw it, before any enrichment.</param>
    /// <param name="reportedTitle">The provider's track name, on its own.</param>
    /// <param name="reportedArtists">The provider's artists, in its own order.</param>
    public static bool Matches(Track detected, string? reportedTitle, IEnumerable<string?>? reportedArtists)
    {
        ArgumentNullException.ThrowIfNull(detected);

        if (string.IsNullOrWhiteSpace(reportedTitle)) return false;

        var reportedArtist = FirstArtist(reportedArtists);

        // Four comparisons rather than one, because either side may or may not carry the artist.
        // Any agreement is agreement: the shapes differ by source, not by track.
        return Same(reportedTitle, detected.Title)
               || Same(reportedTitle, Join(detected.Artist, detected.Title))
               || Same(Join(reportedArtist, reportedTitle), detected.Title)
               || Same(Join(reportedArtist, reportedTitle), Join(detected.Artist, detected.Title));
    }

    /// <summary>Whether a provider's album is the release the track was detected on.</summary>
    /// <param name="detectedAlbum">The album the poller saw, before any enrichment.</param>
    /// <param name="reportedAlbum">The album the provider attributes the track to.</param>
    /// <remarks>
    /// <para>
    /// <b>An unknown album on either side agrees with everything.</b> The window-title parser
    /// never sees one, and a provider that reports no album is not contradicting anybody. This
    /// only ever rejects two names that are both present and name different records.
    /// </para>
    /// <para>
    /// <b>An edition suffix is not a disagreement.</b> The same release arrives as
    /// <c>Movin' Melodies</c> from one source and <c>Movin' Melodies (Deluxe Edition)</c> from
    /// another, so a name that begins with the whole of the other name, at a word boundary, is
    /// taken as the same record with more said about it.
    /// </para>
    /// </remarks>
    public static bool AlbumAgrees(string? detectedAlbum, string? reportedAlbum)
    {
        var detected = Normalise(detectedAlbum);
        var reported = Normalise(reportedAlbum);

        if (detected.Length == 0 || reported.Length == 0) return true;

        return string.Equals(detected, reported, StringComparison.Ordinal)
               || ExtendsWholeWords(detected, reported)
               || ExtendsWholeWords(reported, detected);
    }

    /// <summary>
    /// Reduces a title to what two sources can be expected to agree on.
    /// </summary>
    /// <remarks>
    /// Case and spacing go, because neither is information here. Bracketing and dashes go with
    /// them: the same qualifier arrives as <c>Song (Live)</c>, <c>Song - Live</c> and
    /// <c>Song [Live]</c> depending on who is asked, and the words inside it are what identify
    /// the recording. The words themselves are kept — dropping the qualifier entirely is what
    /// would let a remix match its original.
    /// </remarks>
    public static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(character));

                continue;
            }

            // Everything else — spaces, brackets, dashes, punctuation — collapses to at most one
            // separator, so "Song (Live)" and "Song - Live" reduce to the same words.
            pendingSpace = true;
        }

        return builder.ToString();
    }

    private static bool Same(string? left, string? right)
    {
        var normalisedLeft = Normalise(left);

        return normalisedLeft.Length > 0
               && string.Equals(normalisedLeft, Normalise(right), StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="longer"/> begins with all of <paramref name="shorter"/> and then
    /// says more, rather than merely starting with the same letters.
    /// </summary>
    /// <remarks>
    /// Both sides are already normalised to lower-case words separated by single spaces, so the
    /// character after the prefix being a space is the word boundary — which is what keeps
    /// <c>Ray</c> from extending into <c>Rayman</c>.
    /// </remarks>
    private static bool ExtendsWholeWords(string longer, string shorter) =>
        longer.Length > shorter.Length
        && longer.StartsWith(shorter, StringComparison.Ordinal)
        && longer[shorter.Length] == ' ';

    /// <summary>Joins an artist onto a title, or returns the title when there is no artist.</summary>
    private static string? Join(string? artist, string? title) =>
        string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}";

    /// <summary>
    /// The lead artist only.
    /// </summary>
    /// <remarks>
    /// A collaboration lists every credited artist, and the detected string carries whichever
    /// subset the source chose to show. Comparing against the first is what both sources agree
    /// on; comparing against all of them joined would fail whenever they disagree about the rest.
    /// </remarks>
    private static string? FirstArtist(IEnumerable<string?>? artists) =>
        artists?.FirstOrDefault(artist => !string.IsNullOrWhiteSpace(artist));
}
