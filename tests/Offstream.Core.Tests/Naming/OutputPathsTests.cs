using System.IO.Abstractions.TestingHelpers;
using Offstream.Core.Metadata;
using Offstream.Core.Naming;
using Offstream.Core.Recording;
using Offstream.Core.Settings;
using Xunit;

namespace Offstream.Core.Tests.Naming;

/// <summary>
/// Covers how <see cref="OutputPaths"/> assembles paths from a rendered template and
/// manipulates files on disk. The naming rules themselves live in
/// <see cref="FileNameTemplateTests"/>.
/// </summary>
/// <remarks>Ported from the reference suite's <c>FileManagerTests</c>, assertions unchanged.</remarks>
public sealed class OutputPathsTests
{
    private const string Path = @"C:\path";
    private const string NetworkPath = @"\\path\home";

    private readonly Track _track;
    private readonly RecordingSettings _settings;
    private MockFileSystem _fileSystem;
    private OutputPaths _paths;

    public OutputPathsTests()
    {
        _settings = new RecordingSettings
        {
            OutputPath = Path,
            MediaFormat = MediaFormat.Mp3,
            OutputTemplate = FileNameTemplate.Default,
            OrderNumberInMediaTagEnabled = false,
            InternalOrderNumber = 1,
        };

        _track = new Track
        {
            Title = "Title",
            Artist = "Artist",
            TitleExtended = "Live",
            TitleExtendedSeparatorType = TitleSeparatorType.Dash,
            Album = "Single",
            Ad = false,
        };

        _fileSystem = new MockFileSystem();
        _paths = Build();
    }

    private OutputPaths Build() => new(_settings, _track, _fileSystem, DateTime.Now);

    private void Rebuild() => _paths = Build();

    // ---- output path -------------------------------------------------------

    [Fact]
    public void OutputFile_WithDefaultTemplate_GoesAtTheRoot()
    {
        var outputFile = _paths.GetOutputFileAndInitDirectories();

        Assert.Equal($@"{Path}\Artist - Title - Live.mp3", outputFile.ToMediaFilePath());
    }

    [Fact]
    public void OutputFile_WithNetworkPath_GoesAtTheRoot()
    {
        _settings.OutputPath = NetworkPath;
        Rebuild();

        var outputFile = _paths.GetOutputFileAndInitDirectories();

        Assert.Equal($@"{NetworkPath}\Artist - Title - Live.mp3", outputFile.ToMediaFilePath());
    }

    [Theory]
    [InlineData(MediaFormat.Mp3, "mp3")]
    [InlineData(MediaFormat.Wav, "wav")]
    [InlineData(MediaFormat.Opus, "opus")]
    public void OutputFile_UsesTheMediaFormatExtension(MediaFormat format, string extension)
    {
        _settings.MediaFormat = format;
        Rebuild();

        var outputFile = _paths.GetOutputFileAndInitDirectories();

        Assert.EndsWith($".{extension}", outputFile.ToMediaFilePath(), StringComparison.Ordinal);
    }

    [Fact]
    public void OutputFile_WithFolderTemplate_CreatesAndUsesFolders()
    {
        _settings.OutputTemplate = @"{artist}\{album}\{title}";
        Rebuild();

        var outputFile = _paths.GetOutputFileAndInitDirectories();

        Assert.Equal($@"{Path}\Artist\Single\Title - Live.mp3", outputFile.ToMediaFilePath());
        Assert.True(_fileSystem.Directory.Exists($@"{Path}\Artist\Single"));
    }

    [Fact]
    public void OutputFile_WithCounterToken_PrefixesTheCounter()
    {
        _settings.OutputTemplate = "{count:000} {artist} - {title}";
        _settings.InternalOrderNumber = 7;
        Rebuild();

        var outputFile = _paths.GetOutputFileAndInitDirectories();

        Assert.Equal($@"{Path}\007 Artist - Title - Live.mp3", outputFile.ToMediaFilePath());
    }

    [Fact]
    public void OutputFile_WithTrackToken_PrefixesTheAlbumPosition()
    {
        _track.AlbumPosition = 3;
        _settings.OutputTemplate = "{track:00} {title}";
        Rebuild();

        var outputFile = _paths.GetOutputFileAndInitDirectories();

        Assert.Equal($@"{Path}\03 Title - Live.mp3", outputFile.ToMediaFilePath());
    }

    [Fact]
    public void OutputFile_WhenDuplicating_IncrementsUntilFree()
    {
        _fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { $@"{Path}\Artist - Title - Live.mp3", new MockFileData(string.Empty) },
            { $@"{Path}\Artist - Title - Live 2.mp3", new MockFileData(string.Empty) },
        });
        _settings.ExistingFilePolicy = ExistingFilePolicy.Duplicate;
        Rebuild();

        var outputFile = _paths.GetOutputFileAndInitDirectories();

        Assert.Equal($@"{Path}\Artist - Title - Live 3.mp3", outputFile.ToMediaFilePath());
    }

    [Fact]
    public void OutputFile_WithAd_UsesTheAdvertisementName()
    {
        var now = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);
        var ad = new Track { Artist = "Artist", Ad = true };

        var (folders, fileName) = OutputPaths.BuildFromTemplate(ad, _settings, now);

        Assert.Empty(folders);
        Assert.Equal("Advertisement 20210304050607", fileName);
    }

    [Fact]
    public void OutputFile_WithoutArtist_Throws()
    {
        var unknown = new Track { Title = "Title" };

        Assert.Throws<UnrecognizedTrackException>(
            () => OutputPaths.BuildFromTemplate(unknown, _settings, DateTime.Now));
    }

    // ---- existence ---------------------------------------------------------

    [Fact]
    public void IsPathFileNameExists_ReturnsNotFound() =>
        Assert.False(_paths.IsPathFileNameExists(_track, _settings));

    [Fact]
    public void IsPathFileNameExists_ReturnsFound()
    {
        _fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { $@"{Path}\Artist - Title - Live.mp3", new MockFileData(string.Empty) },
        });
        Rebuild();

        Assert.True(_paths.IsPathFileNameExists(_track, _settings));
    }

    [Fact]
    public void IsPathFileNameExists_WithFolderTemplate_ReturnsFound()
    {
        _fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { $@"{Path}\Artist\Single\Title - Live.mp3", new MockFileData(string.Empty) },
        });
        _settings.OutputTemplate = @"{artist}\{album}\{title}";
        Rebuild();

        Assert.True(_paths.IsPathFileNameExists(_track, _settings));
    }

    // ---- rename ------------------------------------------------------------

    [Fact]
    public void RenameFile_MoveFileToDestination()
    {
        var source = $@"{Path}\temp.tmp";
        var destination = $@"{Path}\Artist - Title.mp3";

        _fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { source, new MockFileData("audio") },
        });
        Rebuild();

        _paths.RenameFile(source, destination);

        Assert.False(_fileSystem.File.Exists(source));
        Assert.True(_fileSystem.File.Exists(destination));
    }

    [Fact]
    public void RenameFile_WithNetworkFormattedPath_MoveFileToDestination()
    {
        var source = $@"{NetworkPath}\temp.tmp";
        var destination = $@"{NetworkPath}\Artist - Title.mp3";

        _fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { source, new MockFileData("audio") },
        });
        _settings.OutputPath = NetworkPath;
        Rebuild();

        _paths.RenameFile(source, destination);

        Assert.True(_fileSystem.File.Exists(destination));
    }

    [Fact]
    public void RenameFile_MoveFileToDestinationAndOverwrite()
    {
        var source = $@"{Path}\temp.tmp";
        var destination = $@"{Path}\Artist - Title.mp3";

        _fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { source, new MockFileData("new") },
            { destination, new MockFileData("old") },
        });
        Rebuild();

        _paths.RenameFile(source, destination);

        Assert.True(_fileSystem.File.Exists(destination));
        Assert.Equal("new", _fileSystem.File.ReadAllText(destination));
    }

    [Theory]
    [InlineData(null, "destination")]
    [InlineData("", "destination")]
    [InlineData("source", null)]
    [InlineData("source", "")]
    public void RenameFile_WithInvalidFileName_Throws(string? source, string? destination) =>
        Assert.Throws<ArgumentException>(() => _paths.RenameFile(source!, destination!));

    [Fact]
    public void RenameFile_WhenSourceFileNoLongerExists_Throws() =>
        Assert.Throws<SourceFileNotFoundException>(
            () => _paths.RenameFile($@"{Path}\gone.tmp", $@"{Path}\out.mp3"));

    [Fact]
    public void RenameFile_WhenDestinationPathNoLongerExists_Throws()
    {
        var source = $@"{Path}\temp.tmp";

        _fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { source, new MockFileData("audio") },
        });
        Rebuild();

        Assert.Throws<DestinationPathNotFoundException>(
            () => _paths.RenameFile(source, $@"{Path}\missing\out.mp3"));
    }

    // ---- delete ------------------------------------------------------------

    [Fact]
    public void DeleteFile_DeletesFile()
    {
        var file = $@"{Path}\Artist - Title.mp3";

        _fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { file, new MockFileData("audio") },
        });
        Rebuild();

        _paths.DeleteFile(file);

        Assert.False(_fileSystem.File.Exists(file));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DeleteFile_WithInvalidFileName_Throws(string? file) =>
        Assert.Throws<ArgumentException>(() => _paths.DeleteFile(file!));

    [Fact]
    public void DeleteFile_DeletesTempFile()
    {
        var file = $@"{Path}\temp.tmp";

        _fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { file, new MockFileData("audio") },
        });
        Rebuild();

        _paths.DeleteFile(file);

        Assert.False(_fileSystem.File.Exists(file));
    }

    [Fact]
    public void DeleteFile_WithFolderTemplate_RemovesTheEmptiedFolder()
    {
        var folder = $@"{Path}\Artist";
        var file = $@"{folder}\Title.mp3";

        _fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { file, new MockFileData("audio") },
        });
        Rebuild();

        _paths.DeleteFile(file);

        Assert.False(_fileSystem.Directory.Exists(folder));
    }

    [Fact]
    public void DeleteFile_KeepsAFolderThatStillHasFiles()
    {
        var folder = $@"{Path}\Artist";
        var file = $@"{folder}\Title.mp3";

        _fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { file, new MockFileData("audio") },
            { $@"{folder}\Other.mp3", new MockFileData("audio") },
        });
        Rebuild();

        _paths.DeleteFile(file);

        Assert.True(_fileSystem.Directory.Exists(folder));
    }

    // ---- helpers -----------------------------------------------------------

    [Fact]
    public void GetCleanPath_ReturnsPathCleaner() =>
        Assert.Equal(@"C:\path\to", OutputPaths.GetCleanPath("C:\\path\\to\0"));

    [Fact]
    public void GetCleanFileFolder_ReturnsFileFolderCleaned() =>
        Assert.Equal("AB", OutputPaths.GetCleanFileFolder("A?B", -1));

    [Fact]
    public void GetCleanFileFolder_WithOnlyInvalidChars_ReturnsInvalid() =>
        Assert.Equal(PathText.InvalidSegmentPlaceholder, OutputPaths.GetCleanFileFolder(@"?*|", -1));

    [Fact]
    public void ConcatPaths_ReturnsPath() =>
        Assert.Equal(@"a\b\c", OutputPaths.ConcatPaths("a", null, "b", "  ", "c"));

    [Theory]
    [InlineData(@"C:\short", false)]
    [InlineData(
        @"C:\a-very-long-output-path-that-leaves-no-room-at-all-for-a-rendered-file-name-because-it-is-far-too-long-to-be-useful-and-then-some-more-characters-to-push-it-over-the-limit-entirely-for-sure-yes-really-truly-definitely-over-the-line-now",
        true)]
    public void IsOutputPathTooLong_DetectsLongPaths(string path, bool expected) =>
        Assert.Equal(expected, OutputPaths.IsOutputPathTooLong(path));

    [Fact]
    public void GetTempFile_IsTaggedForOffstream()
    {
        var tempFile = _paths.GetTempFile();

        Assert.Contains(".offstream", tempFile, StringComparison.Ordinal);
    }
}
