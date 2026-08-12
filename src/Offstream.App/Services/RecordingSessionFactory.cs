using System.IO.Abstractions;
using Offstream.Core;
using Offstream.Core.Audio;
using Offstream.Core.Diagnostics;
using Offstream.Core.Encoding;
using Offstream.Core.Interop;
using Offstream.Core.Recording;
using Offstream.Core.Settings;
using Offstream.Core.Spotify;

namespace Offstream.App.Services;

/// <summary>
/// Builds a configured <see cref="RecordingSession"/>.
/// </summary>
/// <remarks>
/// The seam that keeps <see cref="RecordingController"/> testable. Everything on the other side
/// of it needs a render endpoint, a running Spotify and an ffmpeg binary; everything on this
/// side is state machinery — start, stop, what to show when starting fails — which is the part
/// that can actually be wrong in a way a user notices.
/// </remarks>
public interface IRecordingSessionFactory
{
    /// <summary>Builds a session for one run. Sessions are not restartable, so this is per-start.</summary>
    /// <exception cref="FFmpegNotFoundException">No usable ffmpeg was found.</exception>
    RecordingSession Create(OffstreamSettings settings, IProgress<RecordingProgress> progress);
}

/// <summary>Builds the real thing: WASAPI loopback, window-title detection, ffmpeg.</summary>
public sealed class RecordingSessionFactory(IFileSystem fileSystem, IProcessManager processManager)
    : IRecordingSessionFactory
{
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    private readonly IProcessManager _processManager =
        processManager ?? throw new ArgumentNullException(nameof(processManager));

    /// <inheritdoc />
    public RecordingSession Create(OffstreamSettings settings, IProgress<RecordingProgress> progress)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(progress);

        var capture = new LoopbackAudioCapture(settings.Recording.AudioEndpointDeviceId);
        var detector = new SpotifyTrackDetector(_processManager, new SpotifyPlaybackProbe(_processManager));

        return new RecordingSession(
            capture,
            new SpotifyPoller(detector),
            settings.ToRecordingSettings(),
            CreateEncoder(settings),
            _fileSystem,
            progress);
    }

    /// <summary>
    /// Resolves ffmpeg once per session rather than once per app.
    /// </summary>
    /// <remarks>
    /// A user who installs ffmpeg while Offstream is open, or corrects the configured path on
    /// the settings page, gets a working session on the next start instead of having to restart
    /// the app. Resolution order and why a wrong configured path is an error rather than a
    /// fallback are in <see cref="FFmpegLocator"/>.
    /// </remarks>
    private AudioEncoder CreateEncoder(OffstreamSettings settings)
    {
        var locator = new FFmpegLocator(_fileSystem, AppContext.BaseDirectory);
        var location = locator.Locate(settings.App.FfmpegPath);

        return new AudioEncoder(new FFmpegRunner(location.ExecutablePath));
    }
}
