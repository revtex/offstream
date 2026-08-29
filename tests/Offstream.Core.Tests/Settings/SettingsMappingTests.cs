using Offstream.Core.Metadata;
using Offstream.Core.Recording;
using Offstream.Core.Settings;
using Xunit;

namespace Offstream.Core.Tests.Settings;

/// <summary>
/// The bridge between the on-disk schema and the recording pipeline's working view.
/// </summary>
/// <remarks>
/// Two types rather than one is a deliberate cost (see <see cref="OffstreamSettings"/>), and
/// this is where that cost gets paid: a field added to one and forgotten in the other is a
/// setting that silently does nothing, which these tests exist to catch.
/// </remarks>
public sealed class SettingsMappingTests
{
    private static OffstreamSettings Configured() => OffstreamSettings.CreateDefault() with
    {
        Output = new OutputSettings
        {
            Path = @"D:\Recordings",
            Template = @"{artist} - {title} {count:00}",
            Format = MediaFormat.Opus,
            BitrateKbps = 160,
            ExistingFilePolicy = ExistingFilePolicy.Duplicate,
            CurrentFileCounter = 7,
        },
        Recording = new RecordingOptions
        {
            MinimumLengthSeconds = 45,
            RecordSelection = RecordSelection.Everything,
            Timer = "020000",
        },
        Metadata = new MetadataSettings { WriteCounterToTrackNumber = true },
    };

    [Fact]
    public void ToRecordingSettings_CarriesEveryFieldThePipelineReads()
    {
        var runtime = Configured().ToRecordingSettings();

        Assert.Equal(@"D:\Recordings", runtime.OutputPath);
        Assert.Equal(@"{artist} - {title} {count:00}", runtime.OutputTemplate);
        Assert.Equal(MediaFormat.Opus, runtime.MediaFormat);
        Assert.Equal(160, runtime.BitrateKbps);
        Assert.Equal(ExistingFilePolicy.Duplicate, runtime.ExistingFilePolicy);
        Assert.False(runtime.HasSkipPastRecordedEnabled);
        Assert.False(runtime.KeepsTheExistingFile);
        Assert.Equal(45, runtime.MinimumRecordedLengthSeconds);
        Assert.Equal(RecordSelection.Everything, runtime.RecordSelection);
        Assert.Equal("020000", runtime.RecordingTimer);
        Assert.Equal(7, runtime.InternalOrderNumber);
        Assert.True(runtime.OrderNumberInMediaTagEnabled);
    }

    /// <summary>
    /// The one policy the fold had to carry across: <c>output.skipAlreadyRecordedTracks</c> was a
    /// separate key, and losing it here would leave a user who asked Offstream to move on with a
    /// setting that reads back correctly on the page and does nothing at all.
    /// </summary>
    [Fact]
    public void ToRecordingSettings_CarriesTheMoveOnPolicyAsBothOfTheAnswersItGives()
    {
        var settings = Configured() with
        {
            Output = Configured().Output with { ExistingFilePolicy = ExistingFilePolicy.SkipAndMoveOn },
        };

        var runtime = settings.ToRecordingSettings();

        Assert.True(runtime.HasSkipPastRecordedEnabled);
        Assert.True(runtime.KeepsTheExistingFile);
    }

    [Fact]
    public void ToRecordingSettings_ProducesAWorkingTimerAndCounter()
    {
        var runtime = Configured().ToRecordingSettings();

        Assert.True(runtime.HasRecordingTimerEnabled);
        Assert.Equal(TimeSpan.FromHours(2), runtime.RecordingTimerDuration);
        Assert.Equal(7, runtime.OrderNumberAsFile);
    }

    /// <summary>
    /// The counter is the one thing the pipeline changes while it runs. Losing it on the way
    /// back to disk would restart numbering on the next launch and overwrite yesterday's files.
    /// </summary>
    [Fact]
    public void CaptureRuntimeState_BringsTheIncrementedCounterBack()
    {
        var settings = Configured();
        var runtime = settings.ToRecordingSettings();

        runtime.InternalOrderNumber += 5;

        Assert.Equal(12, settings.CaptureRuntimeState(runtime).Output.CurrentFileCounter);
    }

    [Fact]
    public void CaptureRuntimeState_ChangesNothingElse()
    {
        var settings = Configured();
        var runtime = settings.ToRecordingSettings();

        var captured = settings.CaptureRuntimeState(runtime);

        Assert.Equal(settings with { Output = settings.Output with { CurrentFileCounter = 7 } }, captured);
    }

    [Fact]
    public void Defaults_ProduceUsableRecordingSettings()
    {
        var runtime = OffstreamSettings.CreateDefault().ToRecordingSettings();

        Assert.False(string.IsNullOrWhiteSpace(runtime.OutputPath));
        Assert.False(runtime.HasRecordingTimerEnabled);
        Assert.Equal(1, runtime.InternalOrderNumber);
        Assert.Equal("mp3", runtime.MediaFormatExtension);
    }
}
