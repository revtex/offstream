using System.IO;
using System.IO.Abstractions;
using Offstream.App.Resources;
using Offstream.Core.Audio;
using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Offstream.Core.Settings;

namespace Offstream.App.Services;

/// <summary>How a readiness check came out.</summary>
public enum ReadinessState
{
    /// <summary>Recording will work.</summary>
    Ready,

    /// <summary>Recording will work, but not as well as it could.</summary>
    Degraded,

    /// <summary>Recording will not start.</summary>
    Blocked,
}

/// <summary>One line of the answer to "will pressing Start work?".</summary>
/// <param name="Name">What is being checked, in two or three words.</param>
/// <param name="Detail">The specific answer — a path, a device name, a reason.</param>
/// <param name="State">Whether it is a problem, and how much of one.</param>
public sealed record ReadinessCheck(string Name, string Detail, ReadinessState State);

/// <summary>
/// Answers "will pressing Start work?" before it is pressed.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these was previously discoverable only by starting a session and reading the
/// failure — ffmpeg missing, an output folder that cannot be written, a metadata provider with
/// no credentials. Two of them do not even fail loudly: a provider that is selected but not
/// usable degrades silently to untagged recordings, and a machine with no virtual cable records
/// every sound it makes into the file. Those are the ones worth the panel.
/// </para>
/// <para>
/// <b>Degraded is not blocked, and the distinction is the whole point.</b> Only ffmpeg and the
/// output folder can stop a recording. Everything else costs quality, and telling a user their
/// session will not start when it will is worse than saying nothing.
/// </para>
/// <para>
/// Cheap enough to re-run whenever the page thinks it might have changed: four file-system
/// stats and one endpoint enumeration. Nothing here is cached, because every input is something
/// the user can change from another tab while looking at this one.
/// </para>
/// </remarks>
public sealed class ReadinessProbe(IFileSystem fileSystem, SettingsDocument settings, IAudioDeviceCatalog catalog)
{
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    private readonly SettingsDocument _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    private readonly IAudioDeviceCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <summary>Runs every check, in the order they matter.</summary>
    /// <remarks>
    /// The endpoints are enumerated once and shared by the two checks that need them, so the
    /// device row and the cable row can never disagree about what is plugged in.
    /// </remarks>
    public IReadOnlyList<ReadinessCheck> Run()
    {
        var current = _settings.Current;
        var devices = _catalog.ListRender();

        return
        [
            CheckFfmpeg(current),
            CheckOutputFolder(current),
            CheckDevice(current, devices),
            CheckProvider(current),
            CheckVirtualCable(devices),
        ];
    }

    private ReadinessCheck CheckFfmpeg(OffstreamSettings current)
    {
        var locator = new FFmpegLocator(_fileSystem, AppContext.BaseDirectory);

        if (!locator.TryLocate(current.App.FfmpegPath, out var location))
        {
            return new ReadinessCheck(
                Strings.ReadyFfmpeg, Strings.ReadyFfmpegMissing, ReadinessState.Blocked);
        }

        return new ReadinessCheck(Strings.ReadyFfmpeg, location.ExecutablePath, ReadinessState.Ready);
    }

    /// <summary>
    /// Whether the library folder can actually be written to.
    /// </summary>
    /// <remarks>
    /// Existence is not the question — the folder is created on demand — so this asks the one
    /// thing that cannot be recovered from at save time: whether the path is set at all, and
    /// whether the volume it names is there. A folder on a drive that is not mounted looks
    /// perfectly valid in the settings file.
    /// </remarks>
    private ReadinessCheck CheckOutputFolder(OffstreamSettings current)
    {
        var path = current.Output.Path;

        if (string.IsNullOrWhiteSpace(path))
        {
            return new ReadinessCheck(
                Strings.ReadyOutput, Strings.ReadyOutputUnset, ReadinessState.Blocked);
        }

        try
        {
            var root = _fileSystem.Path.GetPathRoot(_fileSystem.Path.GetFullPath(path));

            if (!string.IsNullOrEmpty(root) && !_fileSystem.Directory.Exists(root))
            {
                return new ReadinessCheck(Strings.ReadyOutput, path, ReadinessState.Blocked);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return new ReadinessCheck(Strings.ReadyOutput, path, ReadinessState.Blocked);
        }

        return new ReadinessCheck(Strings.ReadyOutput, path, ReadinessState.Ready);
    }

    /// <summary>
    /// Which endpoint the session will capture.
    /// </summary>
    /// <remarks>
    /// Naming it is most of the value: recording the wrong output device produces a silent file
    /// and no error, and that is the single hardest failure in this app to diagnose from the
    /// outside. A stored device that is no longer connected is degraded rather than blocked —
    /// the session falls back and still records something.
    /// </remarks>
    private static ReadinessCheck CheckDevice(OffstreamSettings current, IReadOnlyList<RenderDevice> devices)
    {
        var wanted = current.Recording.AudioEndpointDeviceId;

        if (string.IsNullOrEmpty(wanted))
        {
            var fallback = devices.FirstOrDefault(device => device.IsDefault);

            return fallback is null
                ? new ReadinessCheck(Strings.ReadyDevice, Strings.ReadyDeviceNone, ReadinessState.Blocked)
                : new ReadinessCheck(Strings.ReadyDevice, fallback.Name, ReadinessState.Ready);
        }

        var chosen = devices.FirstOrDefault(device => device.Id == wanted);

        return chosen is null
            ? new ReadinessCheck(Strings.ReadyDevice, Strings.ReadyDeviceGone, ReadinessState.Degraded)
            : new ReadinessCheck(Strings.ReadyDevice, chosen.Name, ReadinessState.Ready);
    }

    /// <summary>
    /// Whether tags and cover art will be written.
    /// </summary>
    /// <remarks>
    /// Never blocked. A provider that is selected but unusable already degrades to untagged
    /// recordings rather than refusing to record, and this panel exists to make that visible —
    /// not to change it into a refusal.
    /// </remarks>
    private static ReadinessCheck CheckProvider(OffstreamSettings current) => current.Metadata.Provider switch
    {
        MetadataProvider.LastFm when string.IsNullOrWhiteSpace(current.Metadata.LastFmApiKey) =>
            new ReadinessCheck(Strings.ReadyMetadata, Strings.ReadyMetadataNoKey, ReadinessState.Degraded),

        MetadataProvider.LastFm =>
            new ReadinessCheck(Strings.ReadyMetadata, Strings.SettingsProviderLastFm, ReadinessState.Ready),

        MetadataProvider.Spotify when string.IsNullOrWhiteSpace(current.Metadata.SpotifyRefreshToken) =>
            new ReadinessCheck(Strings.ReadyMetadata, Strings.ReadyMetadataSignedOut, ReadinessState.Degraded),

        MetadataProvider.Spotify =>
            new ReadinessCheck(Strings.ReadyMetadata, Strings.SettingsProviderSpotify, ReadinessState.Ready),

        _ => new ReadinessCheck(Strings.ReadyMetadata, Strings.ReadyMetadataOff, ReadinessState.Degraded),
    };

    private static ReadinessCheck CheckVirtualCable(IReadOnlyList<RenderDevice> devices) =>
        VirtualCable.DetectIn(devices).IsInstalled
            ? new ReadinessCheck(Strings.ReadyCable, Strings.ReadyCableFound, ReadinessState.Ready)
            : new ReadinessCheck(Strings.ReadyCable, Strings.ReadyCableMissing, ReadinessState.Degraded);
}
