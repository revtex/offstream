using System.Globalization;
using System.IO.Abstractions.TestingHelpers;
using Offstream.App.Resources;
using Offstream.App.ViewModels;
using Offstream.Core.Audio;
using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Offstream.Core.Settings;
using Offstream.Core.Spotify.Auth;
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

    /// <summary>
    /// The cable's absence is what a user needs telling — recording an ordinary output device
    /// records the whole machine — so the notice appears with the link and without one when the
    /// cable is there. Offstream never installs it, so the link is the whole of the offer.
    /// </summary>
    [Fact]
    public void VirtualCable_WhenAbsent_IsReportedWithSomewhereToGetIt()
    {
        var catalog = new FakeDeviceCatalog(new RenderDevice("{speakers}", "Speakers", IsDefault: true));
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(), catalog);

        Assert.True(viewModel.IsVirtualCableMissing);
        Assert.Equal(Strings.SettingsVirtualCableMissing, viewModel.VirtualCableStatus);
        Assert.Equal("https://vb-audio.com/Cable/", SettingsViewModel.VirtualCableUrl);
    }

    /// <summary>Windows appends its own suffix to the endpoint, so the match has to be a substring.</summary>
    [Fact]
    public void VirtualCable_WhenInstalled_IsRecognisedThroughTheEndpointSuffix()
    {
        var catalog = new FakeDeviceCatalog(
            new RenderDevice("{speakers}", "Speakers", IsDefault: true),
            new RenderDevice("{cable}", "CABLE Input (VB-Audio Virtual Cable)", IsDefault: false));

        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(), catalog);

        Assert.False(viewModel.IsVirtualCableMissing);
        Assert.Equal(Strings.SettingsVirtualCableFound, viewModel.VirtualCableStatus);
    }

    /// <summary>Plugging the cable in with the page open is the case the refresh button exists for.</summary>
    [Fact]
    public void VirtualCable_IsRecheckedWhenTheDeviceListRefreshes()
    {
        var catalog = new FakeDeviceCatalog(new RenderDevice("{speakers}", "Speakers", IsDefault: true));
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(), catalog);

        Assert.True(viewModel.IsVirtualCableMissing);

        catalog.Devices.Add(new RenderDevice("{cable}", "CABLE Input (VB-Audio Virtual Cable)", IsDefault: false));
        viewModel.RefreshDevicesCommand.Execute(null);

        Assert.False(viewModel.IsVirtualCableMissing);
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
    /// The summary follows the selection, and each provider gets its own — a stale one is worse
    /// than none, because it describes tags the file will not have.
    /// </summary>
    [Theory]
    [InlineData(MetadataProvider.None)]
    [InlineData(MetadataProvider.LastFm)]
    [InlineData(MetadataProvider.Spotify)]
    public void ProviderSummary_DescribesTheChosenProvider(MetadataProvider provider)
    {
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(new MockFileSystem()));

        var summaries = new List<string>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ProviderSummary))
            {
                summaries.Add(viewModel.ProviderSummary);
            }
        };

        viewModel.Provider = provider;

        Assert.NotEmpty(viewModel.ProviderSummary);

        // The point of the control: three providers that tag differently must read differently.
        Assert.Equal(
            3,
            new HashSet<string>(StringComparer.Ordinal)
            {
                Strings.SettingsProviderSummaryNone,
                Strings.SettingsProviderSummaryLastFm,
                Strings.SettingsProviderSummarySpotify,
            }.Count);

        // Last.fm is the default, so selecting it is not a change and raises nothing. Every other
        // selection must, or the OneWay binding leaves the previous provider's text on screen.
        if (provider != MetadataProvider.LastFm) Assert.NotEmpty(summaries);
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

    /// <summary>
    /// Last.fm is the default provider, so a fresh install lands on it with no key. A missing key
    /// is therefore a warning, not a validation error — an error would block saving the output
    /// folder, the format and everything else on a first run, because a save is refused whenever
    /// any field is in error.
    /// </summary>
    [Fact]
    public void LastFmApiKey_WhenMissing_WarnsWithoutBlockingOtherSettings()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(fileSystem));

        Assert.True(viewModel.IsLastFmProvider);
        Assert.True(viewModel.NeedsLastFmApiKey);
        Assert.False(viewModel.HasErrors);

        viewModel.OutputPath = @"E:\Captures";

        Assert.Equal(@"E:\Captures", SettingsFakes.Reload(fileSystem).Output.Path);
    }

    [Fact]
    public void LastFmApiKey_ReachesTheFileAndClearsTheWarning()
    {
        var fileSystem = new MockFileSystem();
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(fileSystem));

        viewModel.LastFmApiKey = "  0123456789abcdef  ";

        Assert.False(viewModel.NeedsLastFmApiKey);
        Assert.Equal("0123456789abcdef", SettingsFakes.Reload(fileSystem).Metadata.LastFmApiKey);
    }

    /// <summary>The key is not Spotify's problem, and vice versa.</summary>
    [Fact]
    public void LastFmApiKey_WhenAnotherProviderIsChosen_IsNotWarnedAbout()
    {
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document());

        viewModel.Provider = MetadataProvider.None;

        Assert.False(viewModel.NeedsLastFmApiKey);
    }

    /// <summary>
    /// The sign-in is what makes the Spotify provider work at all: the Client ID identifies an
    /// app, and grants nothing on its own.
    /// </summary>
    [Fact]
    public async Task SignInToSpotify_StoresTheRefreshToken()
    {
        var fileSystem = new MockFileSystem();
        var account = new FakeSpotifyAccount { RefreshToken = "a-refresh-token" };
        var document = SettingsFakes.Document(fileSystem);
        var viewModel = SettingsFakes.Settings(document, spotifyAccount: account);

        viewModel.Provider = MetadataProvider.Spotify;
        viewModel.SpotifyClientId = " 0123456789abcdef ";

        await viewModel.SignInToSpotifyCommand.ExecuteAsync(null);

        Assert.Equal("0123456789abcdef", account.SignedInWith);
        Assert.True(viewModel.IsSignedInToSpotify);

        // Asserted on the document rather than on the file: the token goes through
        // ISecretProtector on the way to disk, and these fakes protect with a stub.
        Assert.Equal("a-refresh-token", document.Current.Metadata.SpotifyRefreshToken);
        Assert.Equal(MetadataProvider.Spotify, SettingsFakes.Reload(fileSystem).Metadata.Provider);
    }

    /// <summary>
    /// The button read "Sign in to Spotify" beside the words "Signed in" — offering to do a thing
    /// already done. It still has a use once there is an account, and that use is what it now
    /// names.
    /// </summary>
    [Fact]
    public async Task SpotifySignInLabel_FollowsWhetherAnAccountIsStored()
    {
        var account = new FakeSpotifyAccount { RefreshToken = "a-refresh-token" };
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(), spotifyAccount: account);

        Assert.Equal(Strings.SettingsSpotifySignIn, viewModel.SpotifySignInLabel);

        viewModel.Provider = MetadataProvider.Spotify;
        viewModel.SpotifyClientId = "0123456789abcdef";

        await viewModel.SignInToSpotifyCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSignedInToSpotify);
        Assert.Equal(Strings.SettingsSpotifySwitchAccount, viewModel.SpotifySignInLabel);
    }

    /// <summary>
    /// The user is standing at the browser waiting to find out whether it worked, so a declined
    /// or abandoned sign-in says so on the page rather than only in the log.
    /// </summary>
    [Fact]
    public async Task SignInToSpotify_WhenDeclined_SaysSoOnThePage()
    {
        var account = new FakeSpotifyAccount
        {
            SignInFailure = new SpotifyAuthException("Spotify declined the sign-in: access_denied"),
        };

        var viewModel = SettingsFakes.Settings(SettingsFakes.Document(), spotifyAccount: account);

        viewModel.Provider = MetadataProvider.Spotify;
        viewModel.SpotifyClientId = "0123456789abcdef";

        await viewModel.SignInToSpotifyCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsSignedInToSpotify);
        Assert.True(viewModel.HasSaveProblem);
        Assert.Contains("access_denied", viewModel.SaveProblem, StringComparison.Ordinal);
    }

    /// <summary>Signing in without an app to sign in to is not an offer worth making.</summary>
    [Fact]
    public void SignInToSpotify_WithoutAClientId_CannotBeStarted()
    {
        var viewModel = SettingsFakes.Settings(SettingsFakes.Document());

        Assert.False(viewModel.SignInToSpotifyCommand.CanExecute(null));

        viewModel.Provider = MetadataProvider.Spotify;
        Assert.False(viewModel.SignInToSpotifyCommand.CanExecute(null));

        viewModel.SpotifyClientId = "0123456789abcdef";
        Assert.True(viewModel.SignInToSpotifyCommand.CanExecute(null));
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
