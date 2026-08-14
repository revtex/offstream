using System.IO.Abstractions.TestingHelpers;
using Offstream.App.Resources;
using Offstream.App.Services;
using Offstream.Core.Audio;
using Offstream.Core.Metadata;
using Offstream.Core.Settings;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// The Record page's answer to "will pressing Start work?".
/// </summary>
/// <remarks>
/// The distinction these pin down is blocked versus degraded. Two of the five checks describe
/// something that silently costs quality rather than stopping a recording, and reporting either
/// of those as a blocker would be worse than not reporting them at all.
/// </remarks>
public sealed class ReadinessProbeTests
{
    private const string Speakers = "{0.0.0.00000000}.{speakers}";
    private const string Cable = "CABLE Input (VB-Audio Virtual Cable)";
    private const string Library = @"C:\Music";

    private static ReadinessCheck Find(IReadOnlyList<ReadinessCheck> checks, string name) =>
        checks.Single(check => check.Name == name);

    /// <summary>
    /// Settings the store will actually accept.
    /// </summary>
    /// <remarks>
    /// An output path is supplied unless the caller is testing that check, because
    /// <see cref="SettingsStore"/> refuses to save settings without one — so a fixture that
    /// omitted it would leave every other check reading stale defaults.
    /// </remarks>
    private static OffstreamSettings Settings(
        OutputSettings? output = null,
        MetadataSettings? metadata = null,
        RecordingOptions? recording = null) =>
        new()
        {
            Output = output ?? new OutputSettings(Path: Library),
            Metadata = metadata ?? new MetadataSettings(),
            Recording = recording ?? new RecordingOptions(),
        };

    /// <summary>A probe over an in-memory disk and a fixed set of endpoints.</summary>
    /// <param name="settings">Null to leave first-run defaults in place, which set no output path.</param>
    private static ReadinessProbe Probe(
        OffstreamSettings? settings = null,
        MockFileSystem? fileSystem = null,
        params RenderDevice[] devices)
    {
        var files = fileSystem ?? new MockFileSystem();
        var document = RecordingFakes.Document(files);

        if (settings is not null)
        {
            var problem = document.Update(_ => settings);

            // The fixture is worthless if the settings did not take, and the resulting failure
            // reads as a bug in the probe rather than in the setup.
            Assert.Null(problem);
        }

        return new ReadinessProbe(files, document, new FakeDeviceCatalog(devices));
    }

    /// <summary>ffmpeg is one of the two that genuinely stops a session.</summary>
    [Fact]
    public void Ffmpeg_WhenItCannotBeFound_Blocks()
    {
        var check = Find(Probe().Run(), Strings.ReadyFfmpeg);

        Assert.Equal(ReadinessState.Blocked, check.State);
        Assert.Equal(Strings.ReadyFfmpegMissing, check.Detail);
    }

    /// <summary>
    /// First run is ready, not blocked.
    /// </summary>
    /// <remarks>
    /// <see cref="SettingsStore"/> supplies a library folder on load and refuses to save settings
    /// without one, so "no folder chosen" is unreachable in practice — the probe still handles it,
    /// as the last defence against a document that got there some other way, but the state a new
    /// install is actually in is this one. Asserting the reachable case is the point; a test that
    /// pinned the unreachable branch would be pinning a path no user can be on.
    /// </remarks>
    [Fact]
    public void Library_OnAFreshInstall_IsReady()
    {
        var check = Find(Probe().Run(), Strings.ReadyOutput);

        Assert.Equal(ReadinessState.Ready, check.State);
        Assert.NotEmpty(check.Detail);
    }

    /// <summary>
    /// A path on a volume that is not mounted looks perfectly valid in the settings file, and is
    /// the case a plain "is it set?" check misses.
    /// </summary>
    [Fact]
    public void Library_OnAVolumeThatIsNotThere_Blocks()
    {
        var settings = Settings(output: new OutputSettings(Path: @"Q:\Music"));

        var check = Find(Probe(settings).Run(), Strings.ReadyOutput);

        Assert.Equal(ReadinessState.Blocked, check.State);
    }

    [Fact]
    public void Library_WhenTheVolumeExists_IsReadyAndNamesTheFolder()
    {
        var files = new MockFileSystem();
        files.Directory.CreateDirectory(Library);

        var check = Find(Probe(Settings(), files).Run(), Strings.ReadyOutput);

        Assert.Equal(ReadinessState.Ready, check.State);
        Assert.Equal(Library, check.Detail);
    }

    /// <summary>
    /// Naming the endpoint is most of this row's value: capturing the wrong output device
    /// produces a silent file and no error at all.
    /// </summary>
    [Fact]
    public void Device_WithNoChoiceStored_NamesTheSystemDefault()
    {
        var check = Find(
            Probe(devices: new RenderDevice(Speakers, "Speakers", IsDefault: true)).Run(),
            Strings.ReadyDevice);

        Assert.Equal(ReadinessState.Ready, check.State);
        Assert.Equal("Speakers", check.Detail);
    }

    /// <summary>
    /// Degraded, not blocked: the session falls back to the default and still records. Saying it
    /// will not start would be a lie, and one the user would have to test to disprove.
    /// </summary>
    [Fact]
    public void Device_WhenTheChosenOneIsUnplugged_IsDegraded()
    {
        var settings = Settings(recording: new RecordingOptions(AudioEndpointDeviceId: "{gone}"));

        var check = Find(
            Probe(settings, devices: new RenderDevice(Speakers, "Speakers", IsDefault: true)).Run(),
            Strings.ReadyDevice);

        Assert.Equal(ReadinessState.Degraded, check.State);
        Assert.Equal(Strings.ReadyDeviceGone, check.Detail);
    }

    [Fact]
    public void Device_WithNoEndpointsAtAll_Blocks()
    {
        var check = Find(Probe().Run(), Strings.ReadyDevice);

        Assert.Equal(ReadinessState.Blocked, check.State);
    }

    /// <summary>
    /// Never blocked, whatever is wrong with it. A provider that cannot be used already degrades
    /// to untagged recordings rather than refusing to record, and this row exists to make that
    /// visible — not to turn it into a refusal.
    /// </summary>
    [Theory]
    [InlineData(MetadataProvider.None)]
    [InlineData(MetadataProvider.LastFm)]
    [InlineData(MetadataProvider.Spotify)]
    public void Metadata_WithNoCredentials_IsDegradedAndNeverBlocked(MetadataProvider provider)
    {
        var settings = Settings(metadata: new MetadataSettings(Provider: provider));

        var check = Find(Probe(settings).Run(), Strings.ReadyMetadata);

        Assert.Equal(ReadinessState.Degraded, check.State);
    }

    [Fact]
    public void Metadata_WithALastFmKey_IsReady()
    {
        var settings = Settings(
            metadata: new MetadataSettings(MetadataProvider.LastFm, LastFmApiKey: "a-key"));

        var check = Find(Probe(settings).Run(), Strings.ReadyMetadata);

        Assert.Equal(ReadinessState.Ready, check.State);
    }

    /// <summary>
    /// The row that tells a user why their recordings have their notification sounds in them.
    /// Degraded, because it records perfectly well — just not only Spotify.
    /// </summary>
    [Fact]
    public void Cable_WhenAbsent_IsDegradedAndSaysWhatThatCosts()
    {
        var check = Find(
            Probe(devices: new RenderDevice(Speakers, "Speakers", IsDefault: true)).Run(),
            Strings.ReadyCable);

        Assert.Equal(ReadinessState.Degraded, check.State);
        Assert.Equal(Strings.ReadyCableMissing, check.Detail);
    }

    /// <summary>Windows appends its own suffix, so this has to match on a substring.</summary>
    [Fact]
    public void Cable_WhenPresent_IsRecognisedThroughTheEndpointSuffix()
    {
        var check = Find(
            Probe(
                devices:
                [
                    new RenderDevice(Speakers, "Speakers", IsDefault: true),
                    new RenderDevice("{cable}", Cable, IsDefault: false),
                ]).Run(),
            Strings.ReadyCable);

        Assert.Equal(ReadinessState.Ready, check.State);
    }

    /// <summary>
    /// One enumeration feeds both endpoint checks, so they cannot report different hardware.
    /// </summary>
    [Fact]
    public void Run_EnumeratesTheEndpointsOnce()
    {
        var catalog = new CountingCatalog(new RenderDevice(Speakers, "Speakers", IsDefault: true));

        _ = new ReadinessProbe(new MockFileSystem(), RecordingFakes.Document(), catalog).Run();

        Assert.Equal(1, catalog.Calls);
    }

    private sealed class CountingCatalog(params RenderDevice[] devices) : IAudioDeviceCatalog
    {
        public int Calls { get; private set; }

        public IReadOnlyList<RenderDevice> ListRender()
        {
            Calls++;
            return devices;
        }
    }
}
