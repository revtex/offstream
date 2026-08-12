namespace Offstream.Core.Recording;

/// <summary>What to do when the output file already exists.</summary>
public enum ExistingFilePolicy
{
    Skip = 0,
    Overwrite,
    Duplicate,
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
