using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Xunit;

namespace Offstream.Core.Tests.Encoding;

/// <summary>
/// Regression suite 2 (plan §9.2): golden tests over the exact ffmpeg argument vector.
/// </summary>
/// <remarks>
/// These assert the argv without invoking ffmpeg, so encoder flag drift shows up as a diff in
/// a fast unit test rather than as a corrupt file discovered later. Changing an expectation
/// here should always be a deliberate act.
/// </remarks>
public sealed class FFmpegArgumentsTests
{
    private const string Input = @"C:\temp\capture.wav";

    private static EncodeRequest Request(
        MediaFormat format, int bitrate = 320, Track? track = null, string? cover = null) =>
        new(Input, $@"C:\music\out.{EncodingProfiles.For(format).Extension}", format, bitrate, track, cover);

    [Fact]
    public void Mp3_ProducesExpectedArgv()
    {
        var argv = FFmpegArguments.Build(Request(MediaFormat.Mp3, 320));

        Assert.Equal(
        [
            "-hide_banner", "-nostdin", "-y",
            "-i", Input,
            "-c:a", "libmp3lame", "-b:a", "320k",

            // Not ffmpeg's default of 2.4: Windows Explorer and Windows Media Player do not read
            // v2.4 cover art, so the picture is in the file and neither shows it.
            "-id3v2_version", "3",
            @"C:\music\out.mp3",
        ], argv);
    }

    [Fact]
    public void Wav_ProducesExpectedArgv()
    {
        var argv = FFmpegArguments.Build(Request(MediaFormat.Wav));

        Assert.Equal(
        [
            "-hide_banner", "-nostdin", "-y",
            "-i", Input,
            "-c:a", "pcm_s16le",
            @"C:\music\out.wav",
        ], argv);
    }

    [Fact]
    public void Opus_ProducesExpectedArgv()
    {
        var argv = FFmpegArguments.Build(Request(MediaFormat.Opus, 160));

        Assert.Equal(
        [
            "-hide_banner", "-nostdin", "-y",
            "-i", Input,
            "-c:a", "libopus", "-b:a", "160k",
            @"C:\music\out.opus",
        ], argv);
    }

    [Fact]
    public void Flac_IgnoresBitrateAndUsesCompressionLevel()
    {
        var argv = FFmpegArguments.Build(Request(MediaFormat.Flac, 320));

        Assert.Equal(
        [
            "-hide_banner", "-nostdin", "-y",
            "-i", Input,
            "-c:a", "flac", "-compression_level", "8",
            @"C:\music\out.flac",
        ], argv);

        Assert.DoesNotContain("-b:a", argv);
    }

    [Fact]
    public void Aac_ProducesExpectedArgvWithM4aExtension()
    {
        var argv = FFmpegArguments.Build(Request(MediaFormat.Aac, 256));

        Assert.Equal(
        [
            "-hide_banner", "-nostdin", "-y",
            "-i", Input,
            "-c:a", "aac", "-b:a", "256k",
            @"C:\music\out.m4a",
        ], argv);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(160)]
    [InlineData(256)]
    [InlineData(320)]
    public void Bitrate_IsSubstitutedIntoTheCodecFlags(int bitrate)
    {
        var argv = FFmpegArguments.Build(Request(MediaFormat.Mp3, bitrate));

        var index = argv.ToList().IndexOf("-b:a");
        Assert.True(index >= 0);
        Assert.Equal($"{bitrate}k", argv[index + 1]);
    }

    [Fact]
    public void CoverArt_ForMp3_AddsSecondInputAndExplicitMaps()
    {
        var argv = FFmpegArguments.Build(Request(MediaFormat.Mp3, cover: @"C:\temp\cover.jpg"));

        Assert.Equal(
        [
            "-hide_banner", "-nostdin", "-y",
            "-i", Input,
            "-i", @"C:\temp\cover.jpg",
            "-map", "0:a", "-map", "1:v",
            "-c:v", "mjpeg", "-disposition:v", "attached_pic",
            "-c:a", "libmp3lame", "-b:a", "320k",
            "-id3v2_version", "3",
            @"C:\music\out.mp3",
        ], argv);
    }

    /// <summary>
    /// The whole point of the ID3 version flag: it travels with the cover art.
    /// </summary>
    /// <remarks>
    /// ffmpeg defaults to ID3v2.4, whose APIC frame Windows Explorer's thumbnail handler and
    /// Windows Media Player do not read — the art is in the file and shows up in VLC and nowhere
    /// the user actually looks. The predecessor wrote v2.3 (TagLib#'s default), which is why art
    /// worked there and stopped working here.
    /// </remarks>
    [Fact]
    public void CoverArt_ForMp3_IsWrittenAsId3v23()
    {
        var argv = FFmpegArguments.Build(Request(MediaFormat.Mp3, cover: @"C:\temp\cover.jpg"));

        var flag = argv.ToList().IndexOf("-id3v2_version");

        Assert.True(flag >= 0, "the ID3 version to be pinned");
        Assert.Equal("3", argv[flag + 1]);

        // Before the output path, or ffmpeg treats it as an input option and ignores it.
        Assert.True(flag < argv.Count - 1);
    }

    /// <summary>
    /// Ogg cover art goes through TagLib# after encoding, so ffmpeg must not be handed a
    /// second input it would mishandle (§5.2).
    /// </summary>
    [Fact]
    public void CoverArt_ForOpus_IsNotPassedToFfmpeg()
    {
        var argv = FFmpegArguments.Build(Request(MediaFormat.Opus, cover: @"C:\temp\cover.jpg"));

        Assert.DoesNotContain(@"C:\temp\cover.jpg", argv);
        Assert.DoesNotContain("attached_pic", argv);
        Assert.Equal(CoverArtSupport.PostProcess, EncodingProfiles.For(MediaFormat.Opus).CoverArt);
    }

    [Fact]
    public void CoverArt_ForWav_IsIgnored()
    {
        var argv = FFmpegArguments.Build(Request(MediaFormat.Wav, cover: @"C:\temp\cover.jpg"));

        Assert.DoesNotContain(@"C:\temp\cover.jpg", argv);
    }

    [Fact]
    public void Metadata_WritesTheFullTagSet()
    {
        var track = new Track
        {
            Artist = "Artist",
            Title = "Title",
            Album = "Album",
            AlbumArtists = ["Album Artist"],
            Genres = ["Rock", "Indie"],
            Year = 1999,
            AlbumPosition = 4,
            Disc = 2,
        };

        var argv = FFmpegArguments.Build(Request(MediaFormat.Mp3, track: track));

        Assert.Contains("title=Title", argv);
        Assert.Contains("album=Album", argv);
        Assert.Contains("album_artist=Album Artist", argv);
        Assert.Contains("genre=Rock, Indie", argv);
        Assert.Contains("date=1999", argv);
        Assert.Contains("track=4", argv);
        Assert.Contains("disc=2", argv);
    }

    /// <summary>
    /// <c>artist</c> credits the performers on the track, <c>album_artist</c> credits the album.
    /// </summary>
    /// <remarks>
    /// <see cref="Track.Artists"/> returns the album artists whenever they are known, so using it
    /// for <c>artist</c> made the two tags identical on every enriched file and lost the featured
    /// artist. The predecessor wrote them to separate frames and so does this.
    /// </remarks>
    [Fact]
    public void Metadata_CreditsPerformersAsTheArtistAndKeepsAlbumArtistSeparate()
    {
        var track = new Track
        {
            Artist = "Artist",
            Title = "Title",
            Performers = ["Artist", "Guest"],
            AlbumArtists = ["Artist"],
        };

        var argv = FFmpegArguments.MetadataArguments(track);

        Assert.Contains("artist=Artist, Guest", argv);
        Assert.Contains("album_artist=Artist", argv);
    }

    /// <summary>With no performers the artist tag still gets filled, from the window title.</summary>
    [Fact]
    public void Metadata_WithNoPerformers_FallsBackToTheTrackArtists()
    {
        var track = new Track { Artist = "Artist", Title = "Title" };

        Assert.Contains("artist=Artist", FFmpegArguments.MetadataArguments(track));
    }

    /// <summary>
    /// The "of how many" form is how players tell a partial rip from a complete album.
    /// </summary>
    [Fact]
    public void Metadata_WritesTheTrackTotalWhenTheAlbumLengthIsKnown()
    {
        var track = new Track { Artist = "Artist", Title = "Title", AlbumPosition = 4, AlbumTrackCount = 12 };

        Assert.Contains("track=4/12", FFmpegArguments.MetadataArguments(track));
    }

    /// <summary>A total below the position is wrong, so the bare position is written instead.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3)]
    public void Metadata_WithAnUnusableTrackTotal_WritesThePositionAlone(int? albumTrackCount)
    {
        var track = new Track
        {
            Artist = "Artist",
            Title = "Title",
            AlbumPosition = 4,
            AlbumTrackCount = albumTrackCount,
        };

        Assert.Contains("track=4", FFmpegArguments.MetadataArguments(track));
    }

    /// <summary>
    /// A counter of 42 is not the forty-second of anything, so the override drops the total.
    /// </summary>
    [Fact]
    public void Metadata_WithATrackNumberOverride_SuppressesTheAlbumPositionAndTotal()
    {
        var track = new Track { Artist = "Artist", Title = "Title", AlbumPosition = 4, AlbumTrackCount = 12 };

        Assert.Contains("track=42", FFmpegArguments.MetadataArguments(track, trackNumberOverride: 42));
    }

    /// <summary>The provider's precision survives; <c>Year</c> only fills in when it does not.</summary>
    [Fact]
    public void Metadata_PrefersTheFullReleaseDateOverTheYear()
    {
        var track = new Track { Artist = "Artist", Title = "Title", Year = 1997, ReleaseDate = "1997-03-04" };

        Assert.Contains("date=1997-03-04", FFmpegArguments.MetadataArguments(track));
    }

    [Fact]
    public void Metadata_WithNoReleaseDate_FallsBackToTheYear()
    {
        var track = new Track { Artist = "Artist", Title = "Title", Year = 1997 };

        Assert.Contains("date=1997", FFmpegArguments.MetadataArguments(track));
    }

    [Fact]
    public void Metadata_WritesTheCopyrightLine()
    {
        var track = new Track { Artist = "Artist", Title = "Title", Copyright = "1997 Recording Ltd" };

        Assert.Contains("copyright=1997 Recording Ltd", FFmpegArguments.MetadataArguments(track));
    }

    /// <summary>
    /// Container flags configure the file being written, so they must precede the output path —
    /// after it, ffmpeg has nothing left to apply them to.
    /// </summary>
    [Fact]
    public void ContainerArguments_AreAppliedAfterTheCodecFlagsAndBeforeTheOutput()
    {
        var profile = EncodingProfiles.For(MediaFormat.Mp3);
        var argv = FFmpegArguments.Build(Request(MediaFormat.Mp3));

        Assert.NotEmpty(profile.ContainerArguments);

        foreach (var argument in profile.ContainerArguments)
            Assert.Contains(argument, argv);

        var lastCodecFlag = argv.ToList().IndexOf("libmp3lame");
        var containerFlag = argv.ToList().IndexOf(profile.ContainerArguments[0]);

        Assert.InRange(containerFlag, lastCodecFlag + 1, argv.Count - 2);
    }

    /// <summary>Formats that need no muxer flags carry an empty list, never null.</summary>
    [Fact]
    public void ContainerArguments_DefaultToEmpty() =>
        Assert.All(EncodingProfiles.Known, profile => Assert.NotNull(profile.ContainerArguments));

    [Fact]
    public void Metadata_OmitsEmptyValues()
    {
        var track = new Track { Artist = "Artist", Title = "Title" };

        var argv = FFmpegArguments.MetadataArguments(track);

        Assert.DoesNotContain(argv, a => a.StartsWith("album=", StringComparison.Ordinal));
        Assert.DoesNotContain(argv, a => a.StartsWith("date=", StringComparison.Ordinal));
        Assert.DoesNotContain(argv, a => a.StartsWith("track=", StringComparison.Ordinal));
    }

    /// <summary>
    /// The injection case the argv design exists to prevent. Track metadata comes from window
    /// titles, so a title full of shell syntax must land as one inert argument.
    /// </summary>
    [Theory]
    [InlineData(@"Title"" & del C:\ &")]
    [InlineData("Title'; rm -rf /")]
    [InlineData("Title\" -c:a evil")]
    [InlineData("Title | tee owned.txt")]
    public void Metadata_TreatsHostileTitlesAsOneInertArgument(string hostileTitle)
    {
        var track = new Track { Artist = "Artist", Title = hostileTitle };

        var argv = FFmpegArguments.MetadataArguments(track);

        // The whole hostile string lands in exactly one argv element, unsplit and unescaped.
        Assert.Contains($"title={hostileTitle}", argv);

        // Nothing leaked into a separate element that ffmpeg could read as a flag.
        Assert.DoesNotContain(argv, a => a.StartsWith('-') && a != "-metadata");
    }

    [Fact]
    public void EveryFormat_HasAProfile()
    {
        foreach (var format in Enum.GetValues<MediaFormat>())
        {
            var profile = EncodingProfiles.For(format);

            Assert.Equal(format, profile.Format);
            Assert.False(string.IsNullOrWhiteSpace(profile.Extension));
            Assert.NotEmpty(profile.CodecArguments);
        }
    }

    [Fact]
    public void OnlyBitrateFormats_CarryTheRateToken()
    {
        foreach (var profile in EncodingProfiles.Known)
        {
            var hasToken = profile.CodecArguments.Any(a => a.Contains("{rate}", StringComparison.Ordinal));
            Assert.Equal(profile.SupportsBitrate, hasToken);
        }
    }
}
