using System.Globalization;
using System.IO.Abstractions.TestingHelpers;
using Offstream.App.ViewModels;
using Offstream.Core.Audio;
using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Offstream.Core.Settings;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// The Settings page: what it loads, what it refuses, and what reaches the file.
/// </summary>
/// <remarks>
/// Saving is asserted by reading the file back rather than by inspecting the ViewModel. The page
/// has no OK button, so "the edit was applied" and "the edit was written" are the same claim —
/// and a ViewModel that holds a value it failed to persist is precisely the bug the document's
/// design guards against.
/// </remarks>
public sealed class SettingsViewModelTests
{
    [Fact]
    public void Constructor_ShowsWhatIsInTheFile()
    {
        var fileSystem = new MockFileSystem();
        var stored = OffstreamSettings.CreateDefault() with
        {
            Output = new OutputSettings(Path: @"C:\Music", Format: MediaFormat.Flac, BitrateKbps: 192),
            Recording = new RecordingOptions(MinimumLengthSeconds: 45),
            Metadata = new MetadataSettings(Provider: MetadataProvider.LastFm),
        };

        var viewModel = SettingsFakes.Settings(SettingsFakes.DocumentWith(stored, fileSystem));

        Assert.Equal(@"C:\Music", viewModel.OutputPath);
        Assert.Equal(MediaFormat.Flac, viewModel.Format);
        Assert.Equal(192, viewModel.BitrateKbps);
        Assert.Equal("45", viewModel.MinimumLengthSeconds);
        Assert.Equal(MetadataProvider.LastFm, viewModel.Provider);
        Assert.False(viewModel.HasErrors);
    }

    [Fact]
    public void OutputPath_WhenValid_ReachesTheFile()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(fileSystem));

        viewModel.OutputPath = @"D:\Recordings";

        Assert.False(viewModel.HasErrors);
        Assert.Equal(@"D:\Recordings", SettingsFakes.Reload(fileSystem).Output.Path);
    }

    /// <summary>
    /// A relative path resolves against the working directory, which for a shortcut-launched app
    /// is wherever the shortcut points — so the same setting would put recordings in different
    /// places depending on how Offstream was started.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Music\Offstream")]
    public void OutputPath_WhenUnusable_IsRefusedWithoutTouchingTheFile(string typed)
    {
        var fileSystem = new MockFileSystem();
        var document = SettingsFakes.DocumentWith(
            OffstreamSettings.CreateDefault() with { Output = new OutputSettings(Path: @"C:\Music") },
            fileSystem);

        var viewModel = SettingsFakes.Settings(document);
        viewModel.OutputPath = typed;

        Assert.True(viewModel.HasErrors);
        Assert.Equal(@"C:\Music", SettingsFakes.Reload(fileSystem).Output.Path);
    }

    /// <summary>The field says what is wrong; a second banner repeating it would only go stale.</summary>
    [Fact]
    public void OutputPath_WhenUnusable_DoesNotAlsoReportASaveProblem()
    {
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document());

        viewModel.OutputPath = "nowhere";

        Assert.True(viewModel.HasErrors);
        Assert.False(viewModel.HasSaveProblem);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("3601")]
    [InlineData("four")]
    [InlineData("")]
    public void MinimumLength_WhenUnusable_IsRefused(string typed)
    {
        var fileSystem = new MockFileSystem();
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(fileSystem));

        viewModel.MinimumLengthSeconds = typed;

        Assert.True(viewModel.HasErrors);
        Assert.NotEqual(
            typed,
            SettingsFakes.Reload(fileSystem).Recording.MinimumLengthSeconds.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void MinimumLength_WhenValid_ReachesTheFile()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(fileSystem));

        viewModel.MinimumLengthSeconds = "30";

        Assert.Equal(30, SettingsFakes.Reload(fileSystem).Recording.MinimumLengthSeconds);
    }

    /// <summary>
    /// FLAC and WAV have no bitrate to set. The control stays visible and disabled rather than
    /// disappearing, so its absence is not read as a missing feature.
    /// </summary>
    [Theory]
    [InlineData(MediaFormat.Mp3, true)]
    [InlineData(MediaFormat.Aac, true)]
    [InlineData(MediaFormat.Flac, false)]
    [InlineData(MediaFormat.Wav, false)]
    public void Format_DecidesWhetherBitrateApplies(MediaFormat format, bool expected)
    {
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document());

        viewModel.Format = format;

        Assert.Equal(expected, viewModel.SupportsBitrate);
    }

    [Fact]
    public void Format_ReachesTheFile()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(fileSystem));

        viewModel.Format = MediaFormat.Flac;

        Assert.Equal(MediaFormat.Flac, SettingsFakes.Reload(fileSystem).Output.Format);
    }

    /// <summary>A bitrate hand-edited into the file is offered back, not silently rounded away.</summary>
    [Fact]
    public void Bitrates_IncludeAStoredValueOffTheLadder()
    {
        var stored = OffstreamSettings.CreateDefault() with
        {
            Output = new OutputSettings(Path: @"C:\Music", BitrateKbps: 111),
        };

        var viewModel = SettingsFakes.Settings(SettingsFakes.DocumentWith(stored));

        Assert.Contains(111, viewModel.Bitrates);
        Assert.Equal(111, viewModel.BitrateKbps);
        Assert.Equal(viewModel.Bitrates.OrderBy(rate => rate), viewModel.Bitrates);
    }

    [Fact]
    public void Devices_OfferSystemDefaultFirst()
    {
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document());

        Assert.Null(viewModel.Devices[0].Id);
        Assert.Equal(viewModel.Devices[0], viewModel.SelectedDevice);
    }

    /// <summary>
    /// Unplugging headphones must not rewrite the setting. Dropping the missing device would
    /// silently select the system default, and the user would find out by discovering a week of
    /// recordings captured from the wrong endpoint.
    /// </summary>
    [Fact]
    public void Devices_KeepAStoredEndpointThatIsNotConnected()
    {
        var fileSystem = new MockFileSystem();
        var stored = OffstreamSettings.CreateDefault() with
        {
            Output = new OutputSettings(Path: @"C:\Music"),
            Recording = new RecordingOptions(AudioEndpointDeviceId: "{0.0.0.00000000}.{headphones}"),
        };

        var viewModel = SettingsFakes.Settings(SettingsFakes.DocumentWith(stored, fileSystem));

        var selected = Assert.IsType<AudioDeviceOption>(viewModel.SelectedDevice);
        Assert.Equal("{0.0.0.00000000}.{headphones}", selected.Id);
        Assert.False(selected.IsAvailable);

        // And an unrelated edit does not quietly take the endpoint with it.
        viewModel.MinimumLengthSeconds = "10";

        Assert.Equal(
            "{0.0.0.00000000}.{headphones}",
            SettingsFakes.Reload(fileSystem).Recording.AudioEndpointDeviceId);
    }

    [Fact]
    public void SelectedDevice_ReachesTheFile()
    {
        var fileSystem = new MockFileSystem();
        var catalog = new FakeDeviceCatalog(new RenderDevice("{speakers}", "Speakers", IsDefault: true));

        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(fileSystem), catalog);
        viewModel.SelectedDevice = viewModel.Devices.Single(device => device.Id == "{speakers}");

        Assert.Equal("{speakers}", SettingsFakes.Reload(fileSystem).Recording.AudioEndpointDeviceId);
    }

    [Fact]
    public void RefreshDevices_PicksUpAnEndpointPluggedInLater()
    {
        var catalog = new FakeDeviceCatalog(new RenderDevice("{speakers}", "Speakers", IsDefault: true));
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(), catalog);

        viewModel.SelectedDevice = viewModel.Devices.Single(device => device.Id == "{speakers}");
        catalog.Devices.Add(new RenderDevice("{headset}", "Headset", IsDefault: false));

        viewModel.RefreshDevicesCommand.Execute(null);

        Assert.Contains(viewModel.Devices, device => device.Id == "{headset}");
        Assert.Equal("{speakers}", viewModel.SelectedDevice?.Id);
    }

    [Fact]
    public void Browse_StartsWhereTheOutputPathPointsAndAppliesTheChoice()
    {
        var fileSystem = new MockFileSystem();
        var document = SettingsFakes.DocumentWith(
            OffstreamSettings.CreateDefault() with { Output = new OutputSettings(Path: @"C:\Music") },
            fileSystem);

        var picker = new FakeFolderPicker { Result = @"E:\Captures" };
        var viewModel = SettingsFakes.Settings(document, folderPicker: picker);

        viewModel.BrowseCommand.Execute(null);

        Assert.Equal(@"C:\Music", picker.StartingFolder);
        Assert.Equal(@"E:\Captures", viewModel.OutputPath);
        Assert.Equal(@"E:\Captures", SettingsFakes.Reload(fileSystem).Output.Path);
    }

    [Fact]
    public void Browse_WhenCancelled_LeavesTheOutputPathAlone()
    {
        var document = SettingsFakes.DocumentWith(
            OffstreamSettings.CreateDefault() with { Output = new OutputSettings(Path: @"C:\Music") });

        var viewModel = SettingsFakes.Settings(document, folderPicker: new FakeFolderPicker { Result = null });

        viewModel.BrowseCommand.Execute(null);

        Assert.Equal(@"C:\Music", viewModel.OutputPath);
    }

    /// <summary>
    /// The Client ID becomes required the moment the provider changes, without the provider
    /// knowing about the field — which is why every property is revalidated before a save.
    /// </summary>
    [Fact]
    public void Provider_WhenSpotifyWithoutAClientId_IsRefused()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(fileSystem));

        viewModel.Provider = MetadataProvider.Spotify;

        Assert.True(viewModel.IsSpotifyProvider);
        Assert.True(viewModel.HasErrors);
        Assert.Equal(MetadataProvider.LastFm, SettingsFakes.Reload(fileSystem).Metadata.Provider);
    }

    [Fact]
    public void Provider_WhenSpotifyWithAClientId_ReachesTheFile()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(fileSystem));

        viewModel.Provider = MetadataProvider.Spotify;
        viewModel.SpotifyClientId = " 0123456789abcdef ";

        Assert.False(viewModel.HasErrors);

        var saved = SettingsFakes.Reload(fileSystem).Metadata;
        Assert.Equal(MetadataProvider.Spotify, saved.Provider);
        Assert.Equal("0123456789abcdef", saved.SpotifyClientId);
    }

    /// <summary>A Client ID left over from Spotify is not an error once another provider is chosen.</summary>
    [Fact]
    public void Provider_WhenNotSpotify_DoesNotRequireAClientId()
    {
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document());

        viewModel.Provider = MetadataProvider.LastFm;

        Assert.False(viewModel.HasErrors);
        Assert.False(viewModel.IsSpotifyProvider);
    }

    /// <summary>
    /// The page exists to fix an unusable file, so it says what is wrong before anything is
    /// touched rather than waiting for the offending field to be visited.
    /// </summary>
    [Fact]
    public void Constructor_ReportsAStoredValueThatIsAlreadyUnusable()
    {
        var stored = OffstreamSettings.CreateDefault() with
        {
            Output = new OutputSettings(Path: @"C:\Music"),
            Metadata = new MetadataSettings(Provider: MetadataProvider.Spotify),
        };

        var viewModel = SettingsFakes.Settings(SettingsFakes.DocumentWith(stored));

        Assert.True(viewModel.HasErrors);
    }
}
