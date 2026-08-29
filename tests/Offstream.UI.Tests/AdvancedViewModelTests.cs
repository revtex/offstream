using System.IO.Abstractions.TestingHelpers;
using Offstream.App.Resources;
using Offstream.App.ViewModels;
using Offstream.Core.Metadata;
using Offstream.Core.Naming;
using Offstream.Core.Recording;
using Offstream.Core.Settings;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// The Advanced page: naming, the timer, detection and tag options.
/// </summary>
/// <remarks>
/// Same rule as the Settings page — a change is asserted by reading the file back, because
/// without an OK button there is no other moment at which it counts as applied.
/// </remarks>
public sealed class AdvancedViewModelTests
{
    [Fact]
    public void Constructor_ShowsWhatIsInTheFile()
    {
        var stored = OffstreamSettings.CreateDefault() with
        {
            Output = new OutputSettings(
                Path: @"C:\Music",
                Template: FileNameTemplate.Grouped,
                ExistingFilePolicy: ExistingFilePolicy.Overwrite,
                CurrentFileCounter: 7),
            Recording = new RecordingOptions(MuteAds: false, RecordEverything: true, RecordAds: true),
            Metadata = new MetadataSettings(WriteCounterToTrackNumber: true),
            App = new AppSettings(MinimizeToTray: false, Language: "fr"),
        };

        var viewModel = Build(stored);

        Assert.Equal(FileNameTemplate.Grouped, viewModel.Template);
        Assert.Equal("7", viewModel.FileCounter);
        Assert.Equal(ExistingFilePolicy.Overwrite, viewModel.ExistingFilePolicy);
        Assert.False(viewModel.MuteAds);
        Assert.True(viewModel.RecordEverything);
        Assert.True(viewModel.RecordAds);
        Assert.True(viewModel.WriteCounterToTrackNumber);
        Assert.False(viewModel.MinimizeToTray);
        Assert.Equal("fr", viewModel.SelectedLanguage?.Value);
        Assert.False(viewModel.HasErrors);
    }

    [Fact]
    public void Template_WhenValid_ReachesTheFile()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = Build(fileSystem: fileSystem);

        viewModel.Template = @"{album_artist}\{title}";

        Assert.False(viewModel.HasErrors);
        Assert.Equal(@"{album_artist}\{title}", SettingsFakes.Reload(fileSystem).Output.Template);
    }

    /// <summary>
    /// The renderer's own message names the offending token, which is the whole value of it — a
    /// translated "invalid template" would be a worse error in every language.
    /// </summary>
    [Fact]
    public void Template_WhenUnknownTokenIsTyped_SaysWhichAndSavesNothing()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = Build(fileSystem: fileSystem);

        viewModel.Template = "{artistt} - {title}";

        Assert.True(viewModel.HasErrors);
        Assert.Contains(
            viewModel.GetErrors(nameof(AdvancedViewModel.Template)).Cast<object>(),
            error => error.ToString()!.Contains("artistt", StringComparison.Ordinal));

        Assert.Equal(FileNameTemplate.Default, SettingsFakes.Reload(fileSystem).Output.Template);
    }

    /// <summary>
    /// Rendered through the recorder's own naming code, so the preview cannot drift from what
    /// the next file is actually called.
    /// </summary>
    [Fact]
    public void TemplatePreview_ShowsTheFileTheTemplateWouldProduce()
    {
        var viewModel = Build(OffstreamSettings.CreateDefault() with
        {
            Output = new OutputSettings(Path: @"C:\Music", Template: "{artist} - {title}"),
        });

        Assert.StartsWith(@"C:\Music", viewModel.TemplatePreview, StringComparison.Ordinal);
        Assert.EndsWith(".mp3", viewModel.TemplatePreview, StringComparison.Ordinal);
        Assert.Contains(" - ", viewModel.TemplatePreview, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplatePreview_FollowsTheFormatTheSettingsPageChose()
    {
        var viewModel = Build(OffstreamSettings.CreateDefault() with
        {
            Output = new OutputSettings(Path: @"C:\Music", Format: MediaFormat.Flac),
        });

        Assert.EndsWith(".flac", viewModel.TemplatePreview, StringComparison.Ordinal);
    }

    /// <summary>The output folder belongs to the other page, so the preview has to hear about it.</summary>
    [Fact]
    public void TemplatePreview_UpdatesWhenTheOtherPageChangesTheOutputFolder()
    {
        var document = SettingsFakes.DocumentWith(
            OffstreamSettings.CreateDefault() with { Output = new OutputSettings(Path: @"C:\Music") });

        var viewModel = new AdvancedViewModel(document, new MockFileSystem());
        var raised = false;
        viewModel.PropertyChanged += (_, e) =>
            raised |= e.PropertyName == nameof(AdvancedViewModel.TemplatePreview);

        document.Update(settings => settings with { Output = settings.Output with { Path = @"D:\Elsewhere" } });

        Assert.True(raised);
        Assert.StartsWith(@"D:\Elsewhere", viewModel.TemplatePreview, StringComparison.Ordinal);
    }

    [Fact]
    public void UsesCounter_FollowsTheTemplate()
    {
        var viewModel = Build();

        Assert.False(viewModel.UsesCounter);

        viewModel.Template = "{count:000} - {title}";

        Assert.True(viewModel.UsesCounter);
    }

    /// <summary>
    /// A counter past the mask its template asks for renders wider than the padding, which
    /// breaks both sort order and the "have I recorded this already?" check.
    /// </summary>
    [Fact]
    public void FileCounter_MustFitThePaddingTheTemplateAsksFor()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = Build(fileSystem: fileSystem);

        viewModel.Template = "{count:000} - {title}";
        viewModel.FileCounter = "1000";

        Assert.True(viewModel.HasErrors);

        viewModel.FileCounter = "999";

        Assert.False(viewModel.HasErrors);
        Assert.Equal(999, SettingsFakes.Reload(fileSystem).Output.CurrentFileCounter);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("many")]
    public void FileCounter_WhenUnusable_IsRefused(string typed)
    {
        var viewModel = Build();

        viewModel.FileCounter = typed;

        Assert.True(viewModel.HasErrors);
    }

    [Fact]
    public void ResetTemplate_AndUseGroupedTemplate_OfferTheTwoLayoutsPeopleWant()
    {
        var viewModel = Build();

        viewModel.UseGroupedTemplateCommand.Execute(null);
        Assert.Equal(FileNameTemplate.Grouped, viewModel.Template);

        viewModel.ResetTemplateCommand.Execute(null);
        Assert.Equal(FileNameTemplate.Default, viewModel.Template);
    }

    /// <summary>Every token the renderer knows is described, so the reference cannot fall behind it.</summary>
    [Fact]
    public void Tokens_DescribeEveryTokenTheRendererKnows()
    {
        var viewModel = Build();

        Assert.Equal(FileNameTemplate.KnownTokens.Count, viewModel.Tokens.Count);

        foreach (var token in viewModel.Tokens)
        {
            Assert.False(string.IsNullOrWhiteSpace(token.Description), $"{token.Token} has no description.");
        }
    }

    [Fact]
    public void Timer_WhenEnabled_IsStoredAsSixDigits()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = Build(fileSystem: fileSystem);

        viewModel.Timer = "02:30:00";
        viewModel.IsTimerEnabled = true;

        Assert.False(viewModel.HasErrors);
        Assert.Equal("023000", SettingsFakes.Reload(fileSystem).Recording.Timer);
    }

    /// <summary>
    /// Turning the timer off keeps the duration in the box, so turning it back on does not mean
    /// typing it again.
    /// </summary>
    [Fact]
    public void Timer_WhenDisabled_IsClearedInTheFileButKeptOnScreen()
    {
        var fileSystem = new MockFileSystem();
        var stored = OffstreamSettings.CreateDefault() with
        {
            Output = new OutputSettings(Path: @"C:\Music"),
            Recording = new RecordingOptions(Timer: "013000"),
        };

        var viewModel = Build(stored, fileSystem);

        Assert.True(viewModel.IsTimerEnabled);
        Assert.Equal("01:30:00", viewModel.Timer);

        viewModel.IsTimerEnabled = false;

        Assert.Equal("01:30:00", viewModel.Timer);
        Assert.Null(SettingsFakes.Reload(fileSystem).Recording.Timer);
    }

    [Theory]
    [InlineData("90 minutes")]
    [InlineData("00:00:00")]
    [InlineData("100:00:00")]
    public void Timer_WhenUnusableAndEnabled_IsRefused(string typed)
    {
        var viewModel = Build();

        viewModel.IsTimerEnabled = true;
        viewModel.Timer = typed;

        Assert.True(viewModel.HasErrors);
    }

    /// <summary>A duration the field is not using is not worth an error.</summary>
    [Fact]
    public void Timer_WhenUnusableAndDisabled_IsNotAnError()
    {
        var viewModel = Build();

        viewModel.Timer = "later";

        Assert.False(viewModel.IsTimerEnabled);
        Assert.False(viewModel.HasErrors);
    }

    /// <summary>
    /// With track detection on, an advert has no artist and is never written as a file — so the
    /// advertisement toggle only means anything while everything is being recorded.
    /// </summary>
    [Fact]
    public void CanRecordAds_FollowsRecordEverything()
    {
        var viewModel = Build();

        Assert.False(viewModel.CanRecordAds);

        viewModel.RecordEverything = true;

        Assert.True(viewModel.CanRecordAds);
    }

    [Fact]
    public void DetectionOptions_ReachTheFile()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = Build(fileSystem: fileSystem);

        viewModel.MuteAds = false;
        viewModel.RecordEverything = true;
        viewModel.RecordAds = true;
        viewModel.ExistingFilePolicy = ExistingFilePolicy.Duplicate;
        viewModel.WriteCounterToTrackNumber = true;

        var saved = SettingsFakes.Reload(fileSystem);

        Assert.False(saved.Recording.MuteAds);
        Assert.True(saved.Recording.RecordEverything);
        Assert.True(saved.Recording.RecordAds);
        Assert.Equal(ExistingFilePolicy.Duplicate, saved.Output.ExistingFilePolicy);
        Assert.True(saved.Metadata.WriteCounterToTrackNumber);
    }

    /// <summary>
    /// Overwrite and Duplicate both write the file again, so there is nothing for a skip to move
    /// past. The toggle greys out rather than disappearing — a setting that vanishes looks like
    /// one that never existed.
    /// </summary>
    [Fact]
    public void CanSkipAlreadyRecorded_FollowsTheExistingFilePolicy()
    {
        var viewModel = Build();

        Assert.True(viewModel.CanSkipAlreadyRecorded);

        viewModel.ExistingFilePolicy = ExistingFilePolicy.Overwrite;

        Assert.False(viewModel.CanSkipAlreadyRecorded);

        viewModel.ExistingFilePolicy = ExistingFilePolicy.Skip;

        Assert.True(viewModel.CanSkipAlreadyRecorded);
    }

    [Fact]
    public void SkipAlreadyRecordedTracks_ReachesTheFile()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = Build(fileSystem: fileSystem);

        Assert.False(viewModel.SkipAlreadyRecordedTracks);

        viewModel.SkipAlreadyRecordedTracks = true;

        Assert.True(SettingsFakes.Reload(fileSystem).Output.SkipAlreadyRecordedTracks);
    }

    [Fact]
    public void AppOptions_ReachTheFile()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = Build(fileSystem: fileSystem);

        viewModel.MinimizeToTray = false;
        viewModel.SelectedLanguage = viewModel.Languages.Single(option => option.Value == "fr");

        var saved = SettingsFakes.Reload(fileSystem).App;

        Assert.False(saved.MinimizeToTray);
        Assert.Equal("fr", saved.Language);
    }

    /// <summary>"Follow Windows" is stored as no language at all, not as the current one.</summary>
    [Fact]
    public void Language_FollowingWindows_IsStoredAsNull()
    {
        var fileSystem = new MockFileSystem();
        var stored = OffstreamSettings.CreateDefault() with
        {
            Output = new OutputSettings(Path: @"C:\Music"),
            App = new AppSettings(Language: "fr"),
        };

        var viewModel = Build(stored, fileSystem);
        viewModel.SelectedLanguage = viewModel.Languages.Single(option => option.Value is null);

        Assert.Null(SettingsFakes.Reload(fileSystem).App.Language);
    }

    /// <summary>
    /// An empty box means "the bundled copy, or PATH", which is the usual case. A wrong explicit
    /// path is never a fallback — it stops recording, so it stops the save too.
    /// </summary>
    [Fact]
    public void FfmpegPath_WhenPointingAtNothing_IsRefused()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = Build(fileSystem: fileSystem);

        viewModel.FfmpegPath = @"C:\nowhere\ffmpeg.exe";

        Assert.True(viewModel.HasErrors);
        Assert.Null(SettingsFakes.Reload(fileSystem).App.FfmpegPath);
    }

    [Fact]
    public void FfmpegPath_WhenItResolves_ReachesTheFileAndIsReportedBack()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(@"C:\tools\ffmpeg.exe", new MockFileData([]));

        var viewModel = Build(fileSystem: fileSystem);
        viewModel.FfmpegPath = @"C:\tools\ffmpeg.exe";

        Assert.False(viewModel.HasErrors);
        Assert.Equal(@"C:\tools\ffmpeg.exe", SettingsFakes.Reload(fileSystem).App.FfmpegPath);
        Assert.Contains(@"C:\tools\ffmpeg.exe", viewModel.FfmpegStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void FfmpegPath_WhenEmptyAndNothingIsInstalled_SaysSo()
    {
        var viewModel = Build();

        Assert.Equal(string.Empty, viewModel.FfmpegPath);
        Assert.False(viewModel.HasErrors);
        Assert.Equal(Strings.AdvancedFfmpegMissing, viewModel.FfmpegStatus);
    }

    private static AdvancedViewModel Build(
        OffstreamSettings? stored = null,
        MockFileSystem? fileSystem = null)
    {
        var settingsFileSystem = fileSystem ?? new MockFileSystem();

        var document = stored is null
            ? SettingsFakes.Document(settingsFileSystem)
            : SettingsFakes.DocumentWith(stored, settingsFileSystem);

        // The same file system backs the document and the ffmpeg lookup, so a test that puts an
        // executable on disk sees it from both.
        return new AdvancedViewModel(document, settingsFileSystem);
    }
}
