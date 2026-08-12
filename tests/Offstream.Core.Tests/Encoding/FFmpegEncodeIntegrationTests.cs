using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Xunit;

namespace Offstream.Core.Tests.Encoding;

/// <summary>
/// Regression suite 3 (plan §9.2): encode a synthetic tone for real and verify the result
/// with ffprobe.
/// </summary>
/// <remarks>
/// <para>
/// Tagged <c>Category=Ffmpeg</c>. They need ffmpeg and ffprobe on <c>PATH</c>; CI installs
/// them, and <c>build.ps1 -Test</c> warns when they are missing.
/// </para>
/// <para>
/// This is the suite that catches the two traps in §5.2 — Ogg tags living at the stream level
/// rather than the container level, and cover art behaving differently per container. Both
/// were found the hard way in the predecessor, so they are asserted rather than assumed.
/// </para>
/// </remarks>
[Trait("Category", "Ffmpeg")]
public sealed class FFmpegEncodeIntegrationTests : IDisposable
{
    private readonly EncodeWorkspace _workspace = new();

    private FFmpegRunner Runner => _workspace.Runner;

    public void Dispose() => _workspace.Dispose();

    private static Track SampleTrack() => new()
    {
        Artist = "Test Artist",
        Title = "Test Title",
        Album = "Test Album",
        AlbumArtists = ["Test Album Artist"],
        Year = 1999,
        AlbumPosition = 4,
    };

    [Theory]
    [InlineData(MediaFormat.Mp3, "mp3")]
    [InlineData(MediaFormat.Wav, "pcm_s16le")]
    [InlineData(MediaFormat.Opus, "opus")]
    [InlineData(MediaFormat.Flac, "flac")]
    [InlineData(MediaFormat.Aac, "aac")]
    public async Task Encode_ProducesTheExpectedCodec(MediaFormat format, string expectedCodec)
    {
        var source = await _workspace.CreateSourceWavAsync();
        var output = _workspace.PathFor(format);

        await Runner.RunOrThrowAsync(
            FFmpegArguments.Build(new EncodeRequest(source, output, format, 192, SampleTrack())),
            TimeSpan.FromSeconds(60));

        Assert.True(File.Exists(output), $"{format} produced no file.");
        Assert.True(new FileInfo(output).Length > 0, $"{format} produced an empty file.");

        var codec = await FFprobe.QueryAsync(
            "-v", "error", "-select_streams", "a:0",
            "-show_entries", "stream=codec_name",
            "-of", "default=noprint_wrappers=1:nokey=1", output);

        Assert.Equal(expectedCodec, codec.Trim());
    }

    [Theory]
    [InlineData(MediaFormat.Mp3)]
    [InlineData(MediaFormat.Flac)]
    [InlineData(MediaFormat.Aac)]
    public async Task Encode_WritesContainerLevelTags(MediaFormat format)
    {
        var source = await _workspace.CreateSourceWavAsync();
        var output = _workspace.PathFor(format, "tagged");

        await Runner.RunOrThrowAsync(
            FFmpegArguments.Build(new EncodeRequest(source, output, format, 192, SampleTrack())),
            TimeSpan.FromSeconds(60));

        var tags = await FFprobe.FormatTagsAsync(output);

        Assert.Contains("Test Title", tags, StringComparison.Ordinal);
        Assert.Contains("Test Album", tags, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The §5.2 trap.</b> Ogg/Opus stores tags on the <em>stream</em>, not the container.
    /// Probing <c>format_tags</c> returns nothing and reads as "tagging failed" when it did
    /// not. This test asserts both halves so the distinction cannot be lost again.
    /// </summary>
    [Fact]
    public async Task Encode_Opus_WritesTagsAtStreamLevelNotContainerLevel()
    {
        var source = await _workspace.CreateSourceWavAsync();
        var output = _workspace.PathTo("tagged.opus");

        await Runner.RunOrThrowAsync(
            FFmpegArguments.Build(new EncodeRequest(source, output, MediaFormat.Opus, 160, SampleTrack())),
            TimeSpan.FromSeconds(60));

        var streamTags = await FFprobe.AudioStreamTagsAsync(output);

        Assert.Contains("Test Title", streamTags, StringComparison.Ordinal);
        Assert.Contains("Test Album", streamTags, StringComparison.Ordinal);

        var formatTags = await FFprobe.FormatTagsAsync(output);

        Assert.DoesNotContain("Test Title", formatTags, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encode_PreservesDurationAndSampleRate()
    {
        var source = await _workspace.CreateSourceWavAsync();
        var output = _workspace.PathTo("measured.flac");

        await Runner.RunOrThrowAsync(
            FFmpegArguments.Build(new EncodeRequest(source, output, MediaFormat.Flac, 320)),
            TimeSpan.FromSeconds(60));

        var duration = await FFprobe.QueryAsync(
            "-v", "error", "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1", output);

        Assert.InRange(double.Parse(duration.Trim(), System.Globalization.CultureInfo.InvariantCulture), 1.9, 2.1);
    }

    [Fact]
    public async Task Encode_WithHostileTitle_StillProducesAValidFile()
    {
        var source = await _workspace.CreateSourceWavAsync();
        var output = _workspace.PathTo("hostile.mp3");

        var track = new Track
        {
            Artist = "Artist",
            Title = @"Evil"" & echo pwned & rem",
        };

        await Runner.RunOrThrowAsync(
            FFmpegArguments.Build(new EncodeRequest(source, output, MediaFormat.Mp3, 192, track)),
            TimeSpan.FromSeconds(60));

        Assert.True(new FileInfo(output).Length > 0);

        var tags = await FFprobe.QueryAsync(
            "-v", "error", "-show_entries", "format_tags=title",
            "-of", "default=noprint_wrappers=1:nokey=1", output);

        Assert.Contains("echo pwned", tags, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WithBadArguments_ThrowsCarryingFfmpegDiagnostics()
    {
        var exception = await Assert.ThrowsAsync<FFmpegException>(() =>
            Runner.RunOrThrowAsync(
                ["-hide_banner", "-nostdin", "-y", "-i", _workspace.PathTo("does-not-exist.wav"),
                    _workspace.PathTo("nope.mp3")],
                TimeSpan.FromSeconds(30)));

        Assert.NotEqual(0, exception.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(exception.Diagnostics));
    }

    /// <summary>
    /// The version gate against a real build. Banner parsing is unit-tested against captured
    /// text; this proves the plumbing — that the banner arrives on stdout, and that whatever
    /// CI installed is a build Offstream will actually accept (plan §5.1).
    /// </summary>
    [Fact]
    public async Task Version_FromTheRealFfmpeg_IsParsedAndSupported()
    {
        var version = await FFmpegVersion.RequireSupportedAsync(Runner);

        Assert.True(version.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(version.Raw));
    }

    [Fact]
    public async Task Run_WithMissingExecutable_ThrowsFFmpegException()
    {
        var runner = new FFmpegRunner(_workspace.PathTo("no-such-ffmpeg.exe"));

        await Assert.ThrowsAsync<FFmpegException>(() =>
            runner.RunOrThrowAsync(["-version"], TimeSpan.FromSeconds(10)));
    }
}
