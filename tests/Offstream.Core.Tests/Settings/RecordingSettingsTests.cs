using Offstream.Core.Metadata;
using Offstream.Core.Naming;
using Offstream.Core.Settings;
using Xunit;

namespace Offstream.Core.Tests.Settings;

/// <summary>
/// Ported from the reference suite's <c>UserSettingTests</c>.
/// </summary>
/// <remarks>
/// The original's Spotify API credential cases are not here: those properties arrive in
/// Phase 4 with the SpotifyAPI.Web upgrade, and Phase 5 protects the secret with DPAPI (§6).
/// </remarks>
public sealed class RecordingSettingsTests
{
    [Fact]
    public void Defaults_AreUsableWithoutConfiguration()
    {
        var settings = new RecordingSettings();

        Assert.Equal(FileNameTemplate.Default, settings.OutputTemplate);
        Assert.Equal(MediaFormat.Mp3, settings.MediaFormat);
        Assert.Equal(1, settings.InternalOrderNumber);
        Assert.False(settings.HasRecordingTimerEnabled);
        Assert.Equal(TimeSpan.Zero, settings.RecordingTimerDuration);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("000000", false)]
    [InlineData("0000", false)]
    [InlineData("00000000", false)]
    [InlineData("000001", true)]
    [InlineData("013000", true)]
    public void HasRecordingTimerEnabled_ReturnsAsExpected(string? timer, bool expected) =>
        Assert.Equal(expected, new RecordingSettings { RecordingTimer = timer }.HasRecordingTimerEnabled);

    [Theory]
    [InlineData("000030", 30)]
    [InlineData("000100", 60)]
    [InlineData("013000", 5400)]
    [InlineData("000000", 0)]
    public void RecordingTimerDuration_ParsesHoursMinutesSeconds(string timer, int expectedSeconds) =>
        Assert.Equal(
            expectedSeconds,
            new RecordingSettings { RecordingTimer = timer }.RecordingTimerDuration.TotalSeconds);

    /// <summary>
    /// A non-numeric six-character value must not throw. The field is user-editable text.
    /// </summary>
    [Fact]
    public void RecordingTimerDuration_WithNonNumericValue_IsDisabledRatherThanThrowing()
    {
        var settings = new RecordingSettings { RecordingTimer = "ab:cd:" };

        Assert.False(settings.HasRecordingTimerEnabled);
        Assert.Equal(TimeSpan.Zero, settings.RecordingTimerDuration);
    }

    [Theory]
    [InlineData("{count:000} {title}", 999)]
    [InlineData("{count:0000} {title}", 9999)]
    [InlineData("{title}", int.MaxValue)]
    public void OrderNumberMax_ComesFromTheTemplate(string template, int expected) =>
        Assert.Equal(expected, new RecordingSettings { OutputTemplate = template }.OrderNumberMax);

    [Theory]
    [InlineData("{count:000} {title}", false, true)]
    [InlineData("{title}", true, true)]
    [InlineData("{title}", false, false)]
    public void HasOrderNumberEnabled_ReturnsAsExpected(string template, bool tagEnabled, bool expected)
    {
        var settings = new RecordingSettings
        {
            OutputTemplate = template,
            OrderNumberInMediaTagEnabled = tagEnabled,
        };

        Assert.Equal(expected, settings.HasOrderNumberEnabled);
    }

    [Fact]
    public void OrderNumberAsFile_IsNullWhenTheTemplateHasNoCounter()
    {
        var settings = new RecordingSettings { OutputTemplate = "{title}", InternalOrderNumber = 5 };

        Assert.Null(settings.OrderNumberAsFile);
    }

    [Fact]
    public void OrderNumberAsFile_ReturnsTheCounterWhenTheTemplateUsesIt()
    {
        var settings = new RecordingSettings { OutputTemplate = "{count:000} {title}", InternalOrderNumber = 5 };

        Assert.Equal(5, settings.OrderNumberAsFile);
    }

    /// <summary>
    /// Saturating at the template's ceiling is load-bearing: without it, <c>{count:000}</c>
    /// would render "1000", widening past its mask and breaking both sort order and the
    /// already-recorded check.
    /// </summary>
    [Fact]
    public void OrderNumberAsFile_SaturatesAtTheTemplateMaximum()
    {
        var settings = new RecordingSettings { OutputTemplate = "{count:000} {title}", InternalOrderNumber = 5000 };

        Assert.Equal(999, settings.OrderNumberAsFile);
    }

    [Theory]
    [InlineData(true, 7)]
    [InlineData(false, null)]
    public void OrderNumberAsTag_FollowsTheTagSetting(bool tagEnabled, int? expected)
    {
        var settings = new RecordingSettings
        {
            OrderNumberInMediaTagEnabled = tagEnabled,
            InternalOrderNumber = 7,
        };

        Assert.Equal(expected, settings.OrderNumberAsTag);
    }

    [Theory]
    [InlineData(MediaFormat.Mp3, "mp3")]
    [InlineData(MediaFormat.Wav, "wav")]
    [InlineData(MediaFormat.Opus, "opus")]
    public void MediaFormatExtension_IsLowerCase(MediaFormat format, string expected) =>
        Assert.Equal(expected, new RecordingSettings { MediaFormat = format }.MediaFormatExtension);
}
