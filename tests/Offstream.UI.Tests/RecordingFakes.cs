using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Moq;
using NAudio.Wave;
using Offstream.App.Services;
using Offstream.Core.Audio;
using Offstream.Core.Diagnostics;
using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Offstream.Core.Recording;
using Offstream.Core.Settings;
using Offstream.Core.Spotify;

namespace Offstream.UI.Tests;

/// <summary>
/// Stand-ins for the half of the recording pipeline that needs real hardware.
/// </summary>
/// <remarks>
/// The controller and the Record page are state machinery — what the button does, what the page
/// says when starting is refused, whether a stopped session is released. None of that needs a
/// render endpoint, a running Spotify or an ffmpeg binary, and requiring them would leave the
/// whole of it unverifiable on a build agent.
/// </remarks>
internal static class RecordingFakes
{
    /// <summary>A settings store over an empty file system: first run, no problems.</summary>
    public static SettingsStore Store() => Store(new MockFileSystem());

    public static SettingsStore Store(IFileSystem fileSystem) =>
        new(fileSystem, Mock.Of<ISecretProtector>(), @"C:\Offstream\settings.json");

    /// <summary>A store whose file is unreadable, so <c>LoadOrDefault</c> reports a problem.</summary>
    public static SettingsStore StoreWithBrokenFile()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(@"C:\Offstream\settings.json", new MockFileData("{ not json"));

        return Store(fileSystem);
    }

    /// <summary>A settings document over an empty file system: first run, no problems.</summary>
    public static SettingsDocument Document() => new(Store());

    public static SettingsDocument Document(IFileSystem fileSystem) => new(Store(fileSystem));

    /// <summary>A document whose file is unreadable, so starting a session is refused.</summary>
    public static SettingsDocument DocumentWithBrokenFile() => new(StoreWithBrokenFile());

    /// <summary>A session wired to fakes, equivalent to one the real factory would build.</summary>
    public static RecordingSession Session(IProgress<RecordingProgress> progress, ITrackSource? trackSource = null)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory(@"C:\Music");

        var settings = new RecordingSettings
        {
            OutputPath = @"C:\Music",
            OutputTemplate = "{artist} - {title}",
            MediaFormat = MediaFormat.Mp3,
            BitrateKbps = 320,
            MinimumRecordedLengthSeconds = 2,
        };

        var source = trackSource ?? Mock.Of<ITrackSource>();

        return new RecordingSession(
            new SilentCapture(),
            new SpotifyPoller(source),
            settings,
            new NoOpEncoder(),
            fileSystem,
            enricher: null,
            progress);
    }
}

/// <summary>Capture that opens and closes but never delivers a byte.</summary>
internal sealed class SilentCapture : IAudioCaptureSource
{
    public WaveFormat Format { get; } = new(44100, 16, 2);

    public bool IsCapturing { get; private set; }

    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    public event EventHandler<CaptureStoppedEventArgs>? Stopped;

    public void StartCapture() => IsCapturing = true;

    public void StopCapture()
    {
        IsCapturing = false;
        Stopped?.Invoke(this, new CaptureStoppedEventArgs(null));
    }

    public void Dispose() => IsCapturing = false;

    /// <summary>Pushes a buffer, so a test can watch it reach the level meter.</summary>
    public void Deliver(byte[] buffer) =>
        DataAvailable?.Invoke(this, new AudioDataEventArgs(buffer, buffer.Length));
}

/// <summary>An encoder that claims success without running ffmpeg.</summary>
internal sealed class NoOpEncoder : IAudioEncoder
{
    public Task<EncodeOutcome> EncodeAsync(EncodeRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EncodeOutcome(request));
}

/// <summary>
/// A factory that hands out fake sessions, or refuses in the ways the real one can.
/// </summary>
internal sealed class FakeSessionFactory : IRecordingSessionFactory
{
    /// <summary>Thrown instead of building, to exercise the controller's refusal paths.</summary>
    public Exception? Failure { get; set; }

    /// <summary>How many sessions have been asked for.</summary>
    public int Calls { get; private set; }

    /// <summary>The most recent session handed out.</summary>
    public RecordingSession? Last { get; private set; }

    /// <summary>
    /// The channel the controller handed in, so a test can report what a session would.
    /// </summary>
    /// <remarks>
    /// Reaching a state like "recording, and this is the track" otherwise means running a real
    /// poller against a fake Spotify and waiting for it to notice — an integration test of the
    /// session, dressed up as a test of whatever is reading the reports.
    /// </remarks>
    public IProgress<RecordingProgress>? Progress { get; private set; }

    public RecordingSession Create(OffstreamSettings settings, IProgress<RecordingProgress> progress)
    {
        Calls++;
        Progress = progress;

        if (Failure is not null) throw Failure;

        Last = RecordingFakes.Session(progress);

        return Last;
    }
}
