using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Xunit;

namespace Offstream.Core.Tests.Encoding;

/// <summary>
/// Cover art end to end, per container (plan §5.2 — the second of the two traps).
/// </summary>
/// <remarks>
/// <para>
/// The point of this suite is that <em>the picture is read back out of the finished file</em>.
/// The predecessor's Opus support looked correct and was not, because nothing verified the
/// written file; here every container is probed, including the one that takes its picture from
/// TagLib# after ffmpeg has finished.
/// </para>
/// </remarks>
[Trait("Category", "Ffmpeg")]
public sealed class CoverArtIntegrationTests : IDisposable
{
    private readonly EncodeWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static Track SampleTrack() => new()
    {
        Artist = "Test Artist",
        Title = "Test Title",
        Album = "Test Album",
    };

    /// <summary>
    /// The containers ffmpeg handles in the encode pass: the picture arrives as a second input
    /// and comes back as a video stream flagged <c>attached_pic</c>.
    /// </summary>
    [Theory]
    [InlineData(MediaFormat.Mp3)]
    [InlineData(MediaFormat.Flac)]
    [InlineData(MediaFormat.Aac)]
    public async Task Encode_AttachesCoverArtInTheSamePass(MediaFormat format)
    {
        var source = await _workspace.CreateSourceWavAsync();
        var cover = await _workspace.CreateCoverArtAsync();
        var output = _workspace.PathFor(format, "with-cover");

        var outcome = await new AudioEncoder(_workspace.Runner).EncodeAsync(
            new EncodeRequest(source, output, format, 192, SampleTrack(), cover));

        Assert.False(outcome.HasWarning);

        var streams = await FFprobe.VideoStreamsAsync(output);

        Assert.Contains("codec_name=mjpeg", streams, StringComparison.Ordinal);
        Assert.Contains("attached_pic=1", streams, StringComparison.Ordinal);
    }

    /// <summary>
    /// The attached picture is typed as the front cover, and described.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>-disposition:v attached_pic</c> does not settle this. It marks the stream as cover art
    /// and leaves the picture type at 0 — <c>Other</c> — which is what every file written before
    /// 2026-09-02 carries, and what makes software hunting for a front cover skip a file that
    /// has one. The type comes from the stream's <c>comment</c> tag, which the muxer matches
    /// against the spellings the format defines rather than storing verbatim.
    /// </para>
    /// <para>
    /// Asserted through TagLib# rather than ffprobe because that is what a player sees, and
    /// because ffprobe reports the type through the same <c>comment</c> key the argument uses,
    /// so it would pass on a file where the string had been stored and never interpreted.
    /// M4A is excluded: the mov muxer has nowhere to keep either field.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(MediaFormat.Mp3)]
    [InlineData(MediaFormat.Flac)]
    public async Task Encode_TypesTheCoverArtAsTheFrontCover(MediaFormat format)
    {
        var source = await _workspace.CreateSourceWavAsync();
        var cover = await _workspace.CreateCoverArtAsync();
        var output = _workspace.PathFor(format, "typed-cover");

        var outcome = await new AudioEncoder(_workspace.Runner).EncodeAsync(
            new EncodeRequest(source, output, format, 192, SampleTrack(), cover));

        Assert.False(outcome.HasWarning);

        using var tagged = TagLib.File.Create(output);

        var picture = Assert.Single(tagged.Tag.Pictures);

        Assert.Equal(TagLib.PictureType.FrontCover, picture.Type);
        Assert.Equal("Album cover", picture.Description);
    }

    /// <summary>
    /// <b>The §5.2 fallback.</b> ffmpeg's <c>METADATA_BLOCK_PICTURE</c> support for Ogg is weak,
    /// so the profile routes Opus through TagLib# after the encode. The picture must survive
    /// into the finished file, and the textual tags ffmpeg wrote must survive TagLib# rewriting
    /// the comment header — that second half is the part that silently regresses.
    /// </summary>
    [Fact]
    public async Task Encode_Opus_EmbedsCoverArtAfterEncodingWithoutLosingTags()
    {
        var source = await _workspace.CreateSourceWavAsync();
        var cover = await _workspace.CreateCoverArtAsync();
        var output = _workspace.PathFor(MediaFormat.Opus, "with-cover");

        var outcome = await new AudioEncoder(_workspace.Runner).EncodeAsync(
            new EncodeRequest(source, output, MediaFormat.Opus, 160, SampleTrack(), cover));

        Assert.False(outcome.HasWarning);

        using (var tagged = TagLib.File.Create(output))
        {
            var picture = Assert.Single(tagged.Tag.Pictures);

            Assert.Equal(TagLib.PictureType.FrontCover, picture.Type);
            Assert.Equal("image/jpeg", picture.MimeType);
            Assert.True(picture.Data.Count > 0);

            Assert.Equal("Test Title", tagged.Tag.Title);
            Assert.Equal("Test Album", tagged.Tag.Album);
        }

        // And the tags are still where ffprobe expects to find them for this container.
        var streamTags = await FFprobe.AudioStreamTagsAsync(output);
        Assert.Contains("Test Title", streamTags, StringComparison.Ordinal);
    }

    /// <summary>
    /// The profile says WAV cannot carry a picture. Passing one anyway is a normal thing for a
    /// caller to do — the user's format choice should not have to change what metadata is
    /// gathered — so it is dropped silently rather than failing the recording.
    /// </summary>
    [Fact]
    public async Task Encode_Wav_IgnoresCoverArtWithoutComplaining()
    {
        var source = await _workspace.CreateSourceWavAsync();
        var cover = await _workspace.CreateCoverArtAsync();
        var output = _workspace.PathFor(MediaFormat.Wav, "with-cover");

        var outcome = await new AudioEncoder(_workspace.Runner).EncodeAsync(
            new EncodeRequest(source, output, MediaFormat.Wav, 192, SampleTrack(), cover));

        Assert.False(outcome.HasWarning);
        Assert.True(new FileInfo(output).Length > 0);
        Assert.Empty((await FFprobe.VideoStreamsAsync(output)).Trim());
    }

    /// <summary>
    /// A missing image must not cost the user a finished recording. The audio is on disk and
    /// playable; the failure is reported as a warning on the outcome instead.
    /// </summary>
    [Fact]
    public async Task Encode_Opus_WithMissingCoverArt_KeepsTheRecordingAndWarns()
    {
        var source = await _workspace.CreateSourceWavAsync();
        var output = _workspace.PathFor(MediaFormat.Opus, "no-cover");

        var outcome = await new AudioEncoder(_workspace.Runner).EncodeAsync(
            new EncodeRequest(source, output, MediaFormat.Opus, 160, SampleTrack(),
                _workspace.PathTo("does-not-exist.jpg")));

        Assert.True(outcome.HasWarning);
        Assert.NotNull(outcome.CoverArtFailure);
        Assert.True(new FileInfo(output).Length > 0);
    }

    /// <summary>ffmpeg fails outright when its second input is missing, so the encode fails too.</summary>
    [Fact]
    public async Task Encode_Mp3_WithMissingCoverArt_FailsBecauseFfmpegCannotOpenIt()
    {
        var source = await _workspace.CreateSourceWavAsync();
        var output = _workspace.PathFor(MediaFormat.Mp3, "no-cover");

        await Assert.ThrowsAsync<FFmpegException>(() =>
            new AudioEncoder(_workspace.Runner).EncodeAsync(
                new EncodeRequest(source, output, MediaFormat.Mp3, 192, SampleTrack(),
                    _workspace.PathTo("does-not-exist.jpg"))));
    }

    [Fact]
    public void MimeTypeFor_MapsTheTwoFormatsCoverArtArrivesIn()
    {
        Assert.Equal("image/png", CoverArtWriter.MimeTypeFor(@"C:\art\cover.PNG"));
        Assert.Equal("image/jpeg", CoverArtWriter.MimeTypeFor(@"C:\art\cover.jpg"));
        Assert.Equal("image/jpeg", CoverArtWriter.MimeTypeFor(@"C:\art\cover.jpeg"));
    }
}
