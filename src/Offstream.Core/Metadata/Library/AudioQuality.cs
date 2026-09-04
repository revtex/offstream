namespace Offstream.Core.Metadata.Library;

/// <summary>A rough, at-a-glance read on how good a file's audio actually is.</summary>
public enum AudioQualityTier
{
    /// <summary>The file could not be opened, or reported no audio properties at all.</summary>
    Unknown,

    /// <summary>Lossy, under 128 kbps — audibly compressed on most material.</summary>
    Low,

    /// <summary>Lossy, 128–255 kbps — the range most lossy libraries live in.</summary>
    Medium,

    /// <summary>Lossy, 256 kbps or higher — as good as a lossy codec gets.</summary>
    High,

    /// <summary>A lossless codec (FLAC, ALAC, WAV, …).</summary>
    Lossless,
}

/// <summary>
/// What a file's own container reports about its audio, read once at scan time.
/// </summary>
/// <remarks>
/// Deliberately shallow: this is a container-property read, not a spectral analysis of the decoded
/// samples. It answers "what does the file claim" — which is enough to flag an obviously low
/// bitrate or confirm a lossless codec — and not "is that claim actually true", which would need
/// decoding the audio and is a different, heavier feature.
/// </remarks>
public readonly record struct AudioQuality(int? BitrateKbps, int SampleRateHz, int BitsPerSample)
{
    /// <summary>No audio properties available, e.g. because the file could not be opened.</summary>
    public static AudioQuality Unknown => default;

    /// <summary>Whether the file reported anything at all.</summary>
    public bool IsKnown => SampleRateHz > 0;

    /// <summary>
    /// Whether the container reports a bit depth. TagLib# only populates this for codecs that
    /// have a fixed sample bit depth — FLAC, ALAC, WAV, and the like — and reports zero for MP3,
    /// AAC, Opus and Vorbis, none of which have one. That makes it a reliable, free lossless flag
    /// with no need to inspect the codec name.
    /// </summary>
    public bool IsLossless => BitsPerSample > 0;

    /// <summary>The tier a quality badge would show.</summary>
    public AudioQualityTier Tier =>
        !IsKnown ? AudioQualityTier.Unknown
        : IsLossless ? AudioQualityTier.Lossless
        : BitrateKbps switch
        {
            >= 256 => AudioQualityTier.High,
            >= 128 => AudioQualityTier.Medium,
            _ => AudioQualityTier.Low,
        };
}

/// <summary>Reads a file's own audio properties — bitrate, sample rate, bit depth.</summary>
public interface IAudioQualityReader
{
    /// <summary>
    /// Reads <paramref name="path"/>'s audio properties, or <see cref="AudioQuality.Unknown"/> if
    /// the file could not be opened.
    /// </summary>
    AudioQuality Read(string path);
}

/// <summary>
/// The TagLib# implementation. Opens the file a second time, separately from
/// <see cref="ILibraryTagStore"/> — the two answer different questions from the same file, and
/// keeping them apart is what lets each be tested without the other.
/// </summary>
public sealed class TagLibAudioQualityReader : IAudioQualityReader
{
    /// <inheritdoc />
    public AudioQuality Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var file = TagLib.File.Create(path);
            var properties = file.Properties;

            return new AudioQuality(
                properties.AudioBitrate > 0 ? properties.AudioBitrate : null,
                properties.AudioSampleRate,
                properties.BitsPerSample);
        }
        catch (Exception ex) when (
            ex is TagLib.CorruptFileException
                or TagLib.UnsupportedFormatException
                or IOException
                or UnauthorizedAccessException)
        {
            return AudioQuality.Unknown;
        }
    }
}
