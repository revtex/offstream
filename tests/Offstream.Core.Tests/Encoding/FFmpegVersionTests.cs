using Offstream.Core.Encoding;
using Xunit;

namespace Offstream.Core.Tests.Encoding;

/// <summary>
/// Banner parsing and the minimum-version gate (plan §5.1).
/// </summary>
/// <remarks>
/// The banner formats here are real: a gyan.dev release build, a git release tag, a nightly,
/// and a distribution build. Each has bitten someone's version check somewhere.
/// </remarks>
public sealed class FFmpegVersionTests
{
    private const string ReleaseBanner =
        """
        ffmpeg version 8.1.2-essentials_build-www.gyan.dev Copyright (c) 2000-2025 the FFmpeg developers
        built with gcc 14.2.0 (Rev1, Built by MSYS2 project)
        configuration: --enable-gpl --enable-libmp3lame
        """;

    [Fact]
    public void Parse_ReadsAReleaseBanner()
    {
        var version = FFmpegVersion.Parse(ReleaseBanner);

        Assert.Equal(8, version.Major);
        Assert.Equal(1, version.Minor);
        Assert.Equal(2, version.Patch);
        Assert.Equal("8.1.2-essentials_build-www.gyan.dev", version.Raw);
        Assert.True(version.IsKnown);
    }

    [Theory]
    [InlineData("ffmpeg version n7.1 Copyright (c) 2000-2024", 7, 1, 0)]
    [InlineData("ffmpeg version 6.0 Copyright (c) 2000-2023", 6, 0, 0)]
    [InlineData("ffmpeg version 4.4.2-0ubuntu0.22.04.1 Copyright", 4, 4, 2)]
    public void Parse_HandlesTheCommonBannerShapes(string banner, int major, int minor, int patch)
    {
        var version = FFmpegVersion.Parse(banner);

        Assert.Equal((major, minor, patch), (version.Major, version.Minor, version.Patch));
    }

    /// <summary>
    /// A nightly's <c>N-118488</c> is a revision counter, not a version. Reading it as major
    /// version 118488 would pass any floor check by accident; it must come back unknown.
    /// </summary>
    [Fact]
    public void Parse_NightlyBuildIsUnknownRatherThanEnormous()
    {
        var version = FFmpegVersion.Parse("ffmpeg version N-118488-g1e1e4d1e5a Copyright (c) 2000-2026");

        Assert.False(version.IsKnown);
        Assert.Equal(0, version.Major);
        Assert.Equal("N-118488-g1e1e4d1e5a", version.Raw);
    }

    [Fact]
    public void Parse_UnrecognisableBannerIsUnknownAndDoesNotThrow()
    {
        var version = FFmpegVersion.Parse("this is not an ffmpeg banner");

        Assert.False(version.IsKnown);
        Assert.Equal("unknown", version.ToString());
    }

    /// <summary>An unidentifiable build is newer than any release, so it must not be rejected.</summary>
    [Fact]
    public void IsSupported_UnknownVersionIsAllowed() =>
        Assert.True(FFmpegVersion.Parse("ffmpeg version N-118488-g1e1e4d1e5a").IsSupported);

    [Theory]
    [InlineData("ffmpeg version 8.1.2 Copyright", true)]
    [InlineData("ffmpeg version 6.0 Copyright", true)]
    [InlineData("ffmpeg version 5.1.4 Copyright", false)]
    [InlineData("ffmpeg version 4.4.2-0ubuntu0.22.04.1 Copyright", false)]
    public void IsSupported_GatesOnTheMinimum(string banner, bool supported) =>
        Assert.Equal(supported, FFmpegVersion.Parse(banner).IsSupported);

    [Fact]
    public void IsAtLeast_ComparesComponentwise()
    {
        var reference = new FFmpegVersion(6, 1, 2, "6.1.2");

        Assert.True(new FFmpegVersion(7, 0, 0, "7.0").IsAtLeast(reference));
        Assert.True(new FFmpegVersion(6, 2, 0, "6.2").IsAtLeast(reference));
        Assert.True(new FFmpegVersion(6, 1, 2, "6.1.2").IsAtLeast(reference));
        Assert.False(new FFmpegVersion(6, 1, 1, "6.1.1").IsAtLeast(reference));
        Assert.False(new FFmpegVersion(6, 0, 9, "6.0.9").IsAtLeast(reference));
        Assert.False(new FFmpegVersion(5, 9, 9, "5.9.9").IsAtLeast(reference));
    }

    [Fact]
    public void ToString_ShowsTheRawTokenSoLogsMatchBugReports() =>
        Assert.Equal("8.1.2-essentials_build-www.gyan.dev", FFmpegVersion.Parse(ReleaseBanner).ToString());
}
