using Offstream.Core.Metadata;
using Offstream.Core.Recording;
using Offstream.Core.Text;
using Xunit;

namespace Offstream.Core.Tests.Text;

/// <summary>
/// Ported from the reference suite's <c>StringExtensionsTest</c>. Assertions are unchanged;
/// only namespaces, type names and the enum-parsing entry points were renamed (plan §0).
/// </summary>
public sealed class StringExtensionsTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("a", null)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    public void ToNullableInt_ReturnsExpectedInt(string? value, int? expected) =>
        Assert.Equal(expected, value.ToNullableInt());

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(@"\\path\home\", @"\\path\home")]
    [InlineData(@"/path/home//", @"/path/home")]
    [InlineData(@"C:\path\ ", @"C:\path")]
    public void TrimEndPath_ReturnsExpectedString(string? value, string? expected) =>
        Assert.Equal(expected, value.TrimEndPath());

    [Theory]
    [InlineData(null, "")]
    [InlineData(" ", "")]
    [InlineData("v1", "1")]
    [InlineData("1", "1")]
    [InlineData("version1", "1")]
    [InlineData("v0.1-beta", "0.1")]
    [InlineData("1.123", "1.123")]
    [InlineData("1.0.10", "1.0.10")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("v1.2.3.4", "1.2.3.4")]
    public void ToVersionAsString_StripsNonVersionCharacters(string? value, string expected) =>
        Assert.Equal(expected, value.ToVersionAsString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("v.1")]
    [InlineData("v1")]
    [InlineData("1")]
    [InlineData("version1")]
    public void ToVersion_ReturnsNullForNonVersions(string? value) =>
        Assert.Null(value.ToVersion());

    [Theory]
    [InlineData("v0.1-beta", 0, 1)]
    [InlineData("1.123", 1, 123)]
    [InlineData("1.1.0.0", 1, 1, 0, 0)]
    [InlineData("1.0.10", 1, 0, 10)]
    [InlineData("1.2.3.4", 1, 2, 3, 4)]
    [InlineData("v1.2.3.4", 1, 2, 3, 4)]
    public void ToVersion_ReturnsVersion(string value, int major, int minor, int? build = null, int? revision = null)
    {
        var actual = value.ToVersion();

        if (build is null) Assert.Equal(new Version(major, minor), actual);
        else if (revision is null) Assert.Equal(new Version(major, minor, build.Value), actual);
        else Assert.Equal(new Version(major, minor, build.Value, revision.Value), actual);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" ", " ")]
    [InlineData("test", "Test")]
    [InlineData("-abc", "-abc")]
    [InlineData("123", "123")]
    [InlineData("Abc", "Abc")]
    [InlineData("aBC", "ABC")]
    [InlineData("a b c", "A b c")]
    public void Capitalize_ReturnsExpected(string? value, string? expected) =>
        Assert.Equal(expected, value.Capitalize());

    [Theory]
    [InlineData("", 10, 0)]
    [InlineData("   ", 10, 3)]
    [InlineData("This is above ten", 10, 10)]
    [InlineData("This equal to twenty", 20, 20)]
    [InlineData("This is under thirty", 30, 20)]
    [InlineData("This has no max length", -1, 22)]
    public void ToMaxLength_ReturnsRightLength(string value, int max, int expected) =>
        Assert.Equal(expected, value.ToMaxLength(max).Length);

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("xsmall", null)]
    [InlineData("SMALL", AlbumCoverSize.Small)]
    [InlineData("Small", AlbumCoverSize.Small)]
    [InlineData("small", AlbumCoverSize.Small)]
    [InlineData("medium", AlbumCoverSize.Medium)]
    [InlineData("large", AlbumCoverSize.Large)]
    [InlineData("extralarge", AlbumCoverSize.ExtraLarge)]
    public void ToEnum_ParsesAlbumCoverSize(string? value, AlbumCoverSize? expected) =>
        Assert.Equal(expected, value.ToEnum<AlbumCoverSize>());

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("success", null)]
    [InlineData("failed", LastFmNodeStatus.Failed)]
    [InlineData("FAILED", LastFmNodeStatus.Failed)]
    [InlineData("Ok", LastFmNodeStatus.Ok)]
    [InlineData("ok", LastFmNodeStatus.Ok)]
    public void ToEnum_ParsesLastFmNodeStatus(string? value, LastFmNodeStatus? expected) =>
        Assert.Equal(expected, value.ToEnum<LastFmNodeStatus>());

    /// <remarks>
    /// The reference asserted <c>"flac" =&gt; null</c> because it had no FLAC support. Phase 3
    /// added FLAC and AAC once ffmpeg owned every conversion (plan §11), so the expectation
    /// changed deliberately. <c>"ogg"</c> takes over as the unknown-format case.
    /// </remarks>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("ogg", null)]
    [InlineData("flac", MediaFormat.Flac)]
    [InlineData("FLAC", MediaFormat.Flac)]
    [InlineData("aac", MediaFormat.Aac)]
    [InlineData("mp3", MediaFormat.Mp3)]
    [InlineData("MP3", MediaFormat.Mp3)]
    [InlineData("WAV", MediaFormat.Wav)]
    [InlineData("wav", MediaFormat.Wav)]
    [InlineData("OPUS", MediaFormat.Opus)]
    [InlineData("opus", MediaFormat.Opus)]
    public void ToEnum_ParsesMediaFormat(string? value, MediaFormat? expected) =>
        Assert.Equal(expected, value.ToEnum<MediaFormat>());

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("API", null)]
    [InlineData("spotify", MetadataProvider.Spotify)]
    [InlineData("lastFm", MetadataProvider.LastFm)]
    public void ToEnum_ParsesMetadataProvider(string? value, MetadataProvider? expected) =>
        Assert.Equal(expected, value.ToEnum<MetadataProvider>());

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("de", null)]
    [InlineData("en", LanguageType.En)]
    [InlineData("FR", LanguageType.Fr)]
    public void ToEnum_ParsesLanguageType(string? value, LanguageType? expected) =>
        Assert.Equal(expected, value.ToEnum<LanguageType>());

    /// <summary>
    /// A numeric string must not parse as an enum member by its underlying value — the
    /// reference implementation matched against member *names* only, and settings files
    /// contain user-editable text.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("99")]
    public void ToEnum_DoesNotParseNumericStrings(string value) =>
        Assert.Null(value.ToEnum<MediaFormat>());

    [Theory]
    [InlineData("Song (feat. Artist)", new[] { "Artist" })]
    [InlineData("Song (feat. A & B)", new[] { "A", "B" })]
    [InlineData("Song (with A & B)", new[] { "A", "B" })]
    [InlineData("Song (feat. A, B & C)", new[] { "A", "B", "C" })]
    public void ToPerformers_ExtractsFeaturedArtists(string value, string[] expected) =>
        Assert.Equal(expected, value.ToPerformers());
}
