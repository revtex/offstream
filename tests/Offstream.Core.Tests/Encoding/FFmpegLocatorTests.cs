using System.IO.Abstractions.TestingHelpers;
using Offstream.Core.Encoding;
using Xunit;

namespace Offstream.Core.Tests.Encoding;

/// <summary>
/// The ffmpeg search order (plan §5.1): configured, then bundled, then <c>PATH</c>.
/// </summary>
public sealed class FFmpegLocatorTests
{
    private const string AppDirectory = @"C:\Program Files\Offstream";
    private const string BundledFfmpeg = @"C:\Program Files\Offstream\ffmpeg\ffmpeg.exe";
    private const string PathFfmpeg = @"C:\tools\ffmpeg\bin\ffmpeg.exe";

    private static MockFileSystem FileSystemWith(params string[] files)
    {
        var fileSystem = new MockFileSystem();

        foreach (var file in files) fileSystem.AddFile(file, new MockFileData([0x4D, 0x5A]));

        return fileSystem;
    }

    private static FFmpegLocator Locator(MockFileSystem fileSystem, string? searchPath = null) =>
        new(fileSystem, AppDirectory, searchPath ?? @"C:\Windows\system32;C:\tools\ffmpeg\bin");

    [Fact]
    public void Locate_PrefersTheBundledCopyOverPath()
    {
        var location = Locator(FileSystemWith(BundledFfmpeg, PathFfmpeg)).Locate();

        Assert.Equal(BundledFfmpeg, location.ExecutablePath);
        Assert.Equal(FFmpegSource.Bundled, location.Source);
    }

    [Fact]
    public void Locate_AcceptsAnFfmpegSittingDirectlyBesideTheApplication()
    {
        var beside = @"C:\Program Files\Offstream\ffmpeg.exe";

        var location = Locator(FileSystemWith(beside)).Locate();

        Assert.Equal(beside, location.ExecutablePath);
        Assert.Equal(FFmpegSource.Bundled, location.Source);
    }

    [Fact]
    public void Locate_FallsBackToPathWhenNothingIsBundled()
    {
        var location = Locator(FileSystemWith(PathFfmpeg)).Locate();

        Assert.Equal(PathFfmpeg, location.ExecutablePath);
        Assert.Equal(FFmpegSource.SystemPath, location.Source);
    }

    /// <summary>
    /// The override has to beat the bundle, or it is not an override — and a wrong one must not
    /// silently degrade to a different binary than the user named.
    /// </summary>
    [Fact]
    public void Locate_ConfiguredPathWinsOverBundled()
    {
        var configured = @"D:\custom\ffmpeg.exe";

        var location = Locator(FileSystemWith(BundledFfmpeg, PathFfmpeg, configured)).Locate(configured);

        Assert.Equal(configured, location.ExecutablePath);
        Assert.Equal(FFmpegSource.Configured, location.Source);
    }

    [Fact]
    public void Locate_AcceptsAFolderAsTheConfiguredPath()
    {
        var fileSystem = FileSystemWith(@"D:\custom\ffmpeg.exe");

        var location = Locator(fileSystem).Locate(@"D:\custom");

        Assert.Equal(@"D:\custom\ffmpeg.exe", location.ExecutablePath);
        Assert.Equal(FFmpegSource.Configured, location.Source);
    }

    [Fact]
    public void Locate_QuotedConfiguredPathIsAccepted()
    {
        var fileSystem = FileSystemWith(@"D:\custom\ffmpeg.exe");

        var location = Locator(fileSystem).Locate("\"D:\\custom\\ffmpeg.exe\"  ");

        Assert.Equal(@"D:\custom\ffmpeg.exe", location.ExecutablePath);
    }

    [Fact]
    public void Locate_MissingConfiguredPathThrowsRatherThanFallingBack()
    {
        var locator = Locator(FileSystemWith(BundledFfmpeg, PathFfmpeg));

        var exception = Assert.Throws<FFmpegNotFoundException>(() => locator.Locate(@"D:\gone\ffmpeg.exe"));

        Assert.Contains(@"D:\gone\ffmpeg.exe", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Locate_WithNothingAnywhereThrows() =>
        Assert.Throws<FFmpegNotFoundException>(() => Locator(FileSystemWith()).Locate());

    [Fact]
    public void Locate_ResolvesFfprobeAsASibling()
    {
        var location = Locator(FileSystemWith(BundledFfmpeg)).Locate();

        Assert.Equal(@"C:\Program Files\Offstream\ffmpeg\ffprobe.exe", location.ProbePath);
    }

    /// <summary>A real machine's <c>PATH</c> contains junk; one bad segment must not end the search.</summary>
    [Fact]
    public void Locate_SkipsMalformedPathSegments()
    {
        var searchPath = $"\"C:\\quoted|bad<segment\";;   ;{Path.GetDirectoryName(PathFfmpeg)}";

        var location = Locator(FileSystemWith(PathFfmpeg), searchPath).Locate();

        Assert.Equal(PathFfmpeg, location.ExecutablePath);
    }

    [Fact]
    public void TryLocate_ReportsFailureInsteadOfThrowing()
    {
        Assert.False(Locator(FileSystemWith()).TryLocate(null, out var location));
        Assert.Null(location);

        Assert.False(Locator(FileSystemWith(BundledFfmpeg)).TryLocate(@"D:\gone\ffmpeg.exe", out _));
        Assert.True(Locator(FileSystemWith(BundledFfmpeg)).TryLocate(null, out var found));
        Assert.Equal(BundledFfmpeg, found!.ExecutablePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Locate_BlankConfiguredPathMeansNoOverride(string? configured)
    {
        var location = Locator(FileSystemWith(BundledFfmpeg)).Locate(configured);

        Assert.Equal(FFmpegSource.Bundled, location.Source);
    }
}
