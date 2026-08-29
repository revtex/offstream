namespace Offstream.Core.Recording;

/// <summary>What to do when the output file already exists.</summary>
/// <remarks>
/// <para>
/// <see cref="SkipAndMoveOn"/> folds in what used to be a separate boolean beside this choice,
/// "also tell Spotify to move on". That switch did nothing under <see cref="Overwrite"/> or
/// <see cref="Duplicate"/> — both write the file again, so there is nothing to move past — so
/// six combinations only ever produced these four answers.
/// </para>
/// <para>
/// <see cref="Skip"/> keeps its name and its zero value: the member names are what land in
/// <c>settings.json</c>, and renaming one turns every existing file into a load failure.
/// </para>
/// </remarks>
public enum ExistingFilePolicy
{
    /// <summary>Keep the file on disk, record nothing, and let the track play out.</summary>
    Skip = 0,

    /// <summary>Keep it, and ask Spotify to move to the next track.</summary>
    SkipAndMoveOn,

    /// <summary>Record over it.</summary>
    Overwrite,

    /// <summary>Record alongside it, under a numbered name.</summary>
    Duplicate,
}

/// <summary>How much of what Spotify plays is worth saving.</summary>
/// <remarks>
/// <para>
/// One choice with three values, replacing three booleans — "only record known tracks",
/// "record everything" and "record ads" — that between them encoded exactly these three
/// outcomes, plus a fourth combination behaviourally identical to the first. Two of the
/// three switches did nothing unless a third allowed it, which is a hierarchy the UI has to
/// keep explaining and a user has to keep rediscovering.
/// </para>
/// <para>
/// Ordered by how much gets through, so the default is the first member and a reader can see
/// that the list only ever widens.
/// </para>
/// </remarks>
public enum RecordSelection
{
    /// <summary>Only what parses as an "artist - title" track. Everything else is discarded.</summary>
    KnownTracksOnly = 0,

    /// <summary>Also podcasts and anything else with no artist, but never advertisements.</summary>
    EverythingExceptAds,

    /// <summary>Everything, advertisements included.</summary>
    Everything,
}

/// <summary>Which end of a recording silence trimming applies to.</summary>
public enum SilenceTrim
{
    None,
    TrimEnd,
    TrimStart,
}

/// <summary>Why a wave format cannot be encoded to MP3 without conversion.</summary>
public enum Mp3Restriction
{
    Channel,
    SampleRate,
}

/// <summary>UI language.</summary>
public enum LanguageType
{
    En = 0,
    Fr,
}
