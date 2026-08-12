using NAudio.Wave;
using Offstream.Core.Recording;

namespace Offstream.Core.Audio;

/// <summary>Limits the MP3 encoder imposes on an input wave format.</summary>
public static class WaveFormatExtensions
{
    /// <summary>MP3 supports at most stereo.</summary>
    public const int Mp3MaxChannels = 2;

    /// <summary>MP3 supports at most 48 kHz.</summary>
    public const int Mp3MaxSampleRate = 48000;

    /// <summary>
    /// Which MP3 limits <paramref name="waveFormat"/> exceeds; empty when it can be encoded as-is.
    /// </summary>
    public static IReadOnlyList<Mp3Restriction> GetMp3Restrictions(this WaveFormat waveFormat)
    {
        ArgumentNullException.ThrowIfNull(waveFormat);

        var restrictions = new List<Mp3Restriction>();

        if (waveFormat.Channels > Mp3MaxChannels) restrictions.Add(Mp3Restriction.Channel);
        if (waveFormat.SampleRate > Mp3MaxSampleRate) restrictions.Add(Mp3Restriction.SampleRate);

        return restrictions;
    }
}
