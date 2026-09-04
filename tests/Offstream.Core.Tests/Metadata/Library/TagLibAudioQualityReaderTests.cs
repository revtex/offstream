using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Library;
using Offstream.Core.Tests.Encoding;
using Xunit;

namespace Offstream.Core.Tests.Metadata.Library;

/// <summary>
/// Reading a file's own audio properties, against files ffmpeg actually encoded.
/// </summary>
/// <remarks>
/// Tagged <c>Ffmpeg</c> for the same reason as <see cref="TagLibTagStoreTests"/>: the only honest
/// way to test a container-property read is against a real encoded file, not a mock of one.
/// </remarks>
[Trait("Category", "Ffmpeg")]
public sealed class TagLibAudioQualityReaderTests : IDisposable
{
    private readonly EncodeWorkspace _workspace = new();
    private readonly TagLibAudioQualityReader _reader = new();

    public void Dispose() => _workspace.Dispose();

    /// <summary>A file requested well under 128 kbps reads back in the low tier.</summary>
    [Fact]
    public async Task Read_ReportsALowBitrateMp3AsLow()
    {
        var path = await EncodeAsync(MediaFormat.Mp3, 64);

        var quality = _reader.Read(path);

        Assert.True(quality.BitrateKbps is > 0);
        Assert.Equal(AudioQualityTier.Low, quality.Tier);
        Assert.False(quality.IsLossless);
    }

    /// <summary>A file requested well over 256 kbps reads back in the high tier.</summary>
    [Fact]
    public async Task Read_ReportsAHighBitrateMp3AsHigh()
    {
        var path = await EncodeAsync(MediaFormat.Mp3, 320);

        Assert.Equal(AudioQualityTier.High, _reader.Read(path).Tier);
    }

    /// <summary>
    /// FLAC is lossless regardless of what its reported bitrate happens to be — the bit depth is
    /// what decides it, not a threshold on kbps.
    /// </summary>
    [Fact]
    public async Task Read_ReportsFlacAsLossless()
    {
        var path = await EncodeAsync(MediaFormat.Flac, 192);

        var quality = _reader.Read(path);

        Assert.Equal(AudioQualityTier.Lossless, quality.Tier);
        Assert.True(quality.BitsPerSample > 0);
    }

    /// <summary>A file that is not audio is a known-nothing result, not an exception.</summary>
    [Fact]
    public void Read_OfSomethingThatIsNotAudioReturnsUnknown()
    {
        var path = _workspace.PathTo("notes.mp3");

        File.WriteAllText(path, "this is not an MP3");

        Assert.Equal(AudioQualityTier.Unknown, _reader.Read(path).Tier);
    }

    private async Task<string> EncodeAsync(MediaFormat format, int bitrateKbps)
    {
        var source = await _workspace.CreateSourceWavAsync();
        var output = _workspace.PathFor(format, "quality");

        // Constant rather than the average-bitrate default: the source is a two-second sine
        // tone, trivially compressible, and an averaging encoder spends far fewer bits on it
        // than the requested rate — which is correct ABR behaviour but would make the actual
        // bitrate this test reads back unpredictable. CBR pins every frame to the request.
        await new AudioEncoder(_workspace.Runner).EncodeAsync(
            new EncodeRequest(source, output, format, bitrateKbps, new Track
            {
                Artist = "Seed Artist",
                Title = "Seed Title",
            }, BitrateMode: BitrateMode.Constant));

        return output;
    }
}
