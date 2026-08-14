using System.IO.Abstractions.TestingHelpers;
using Offstream.App.Services;
using Offstream.App.ViewModels;
using Offstream.Core.Audio;
using Offstream.Core.Settings;
using SpotifyAPI.Web;

namespace Offstream.UI.Tests;

/// <summary>
/// Stand-ins for the two things the settings pages touch outside the process.
/// </summary>
/// <remarks>
/// Enumerating render endpoints needs a sound card and opening a folder dialog needs a message
/// loop; a build agent has neither. Both are seams on the ViewModel for exactly that reason, so
/// everything else on those pages — validation, saving, the live preview — stays testable in CI.
/// </remarks>
internal static class SettingsFakes
{
    /// <summary>The settings file every test in these fixtures writes to.</summary>
    public const string SettingsPath = @"C:\Offstream\settings.json";

    /// <summary>A document over an empty file system: first run, defaults, saves succeed.</summary>
    public static SettingsDocument Document(MockFileSystem? fileSystem = null) =>
        new(RecordingFakes.Store(fileSystem ?? new MockFileSystem()));

    /// <summary>A document holding <paramref name="settings"/>, as though they had been saved.</summary>
    public static SettingsDocument DocumentWith(OffstreamSettings settings, MockFileSystem? fileSystem = null)
    {
        var store = RecordingFakes.Store(fileSystem ?? new MockFileSystem());
        store.Save(settings);

        return new SettingsDocument(store);
    }

    /// <summary>Reads the file back, which is the only proof a save actually happened.</summary>
    public static OffstreamSettings Reload(MockFileSystem fileSystem) =>
        RecordingFakes.Store(fileSystem).Load();

    public static SettingsViewModel Settings(
        SettingsDocument document,
        IAudioDeviceCatalog? catalog = null,
        IFolderPicker? folderPicker = null,
        ISpotifyAccount? spotifyAccount = null) =>
        new(
            document,
            catalog ?? new FakeDeviceCatalog(),
            folderPicker ?? new FakeFolderPicker(),
            spotifyAccount ?? new FakeSpotifyAccount());
}

/// <summary>A device list a test can rearrange between calls.</summary>
internal sealed class FakeDeviceCatalog : IAudioDeviceCatalog
{
    public FakeDeviceCatalog(params RenderDevice[] devices) => Devices = [.. devices];

    /// <summary>What the next <see cref="ListRender"/> returns.</summary>
    public List<RenderDevice> Devices { get; }

    public IReadOnlyList<RenderDevice> ListRender() => Devices;
}

/// <summary>A folder dialog that answers without one.</summary>
internal sealed class FakeFolderPicker : IFolderPicker
{
    /// <summary>What the dialog "returns"; null stands for the user cancelling.</summary>
    public string? Result { get; set; }

    /// <summary>Where the dialog was told to open, for asserting it starts somewhere useful.</summary>
    public string? StartingFolder { get; private set; }

    public string? Pick(string? startingFolder)
    {
        StartingFolder = startingFolder;

        return Result;
    }
}

/// <summary>
/// A Spotify account that signs in without a browser.
/// </summary>
/// <remarks>
/// The real one opens the user's browser and waits on a loopback listener for up to five
/// minutes, which is neither testable nor survivable on a build agent.
/// </remarks>
internal sealed class FakeSpotifyAccount : ISpotifyAccount
{
    /// <summary>The refresh token <see cref="SignInAsync"/> hands back.</summary>
    public string RefreshToken { get; set; } = "refresh-token";

    /// <summary>Thrown instead of signing in, for the declined and timed-out cases.</summary>
    public Exception? SignInFailure { get; set; }

    /// <summary>The Client ID the sign-in was asked for, to prove the page's value reaches it.</summary>
    public string? SignedInWith { get; private set; }

    public Task<string> SignInAsync(string clientId, CancellationToken cancellationToken = default)
    {
        SignedInWith = clientId;

        return SignInFailure is not null
            ? Task.FromException<string>(SignInFailure)
            : Task.FromResult(RefreshToken);
    }

    public ISpotifyClient? CreateClient(
        string? clientId, string? refreshToken, Action<string> onRefreshTokenRotated) => null;

    /// <summary>What <see cref="DescribeAccountAsync"/> answers — null being "could not tell".</summary>
    public string? AccountDescription { get; set; }

    /// <summary>The refresh token the account lookup was handed, to prove the stored one reaches it.</summary>
    public string? DescribedWith { get; private set; }

    public Task<string?> DescribeAccountAsync(
        string? clientId,
        string? refreshToken,
        Action<string> onRefreshTokenRotated,
        CancellationToken cancellationToken = default)
    {
        DescribedWith = refreshToken;

        return Task.FromResult(AccountDescription);
    }
}
