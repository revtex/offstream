using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Moq;
using NAudio.Wave;
using Offstream.Core.Audio;
using Offstream.Core.Diagnostics;
using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Offstream.Core.Recording;
using Offstream.Core.Settings;
using Offstream.Core.Spotify;
using Xunit;

namespace Offstream.Core.Tests.Recording;

/// <summary>
/// The whole pipeline joined up — capture, detection, recording, encoding, renaming — driven by
/// stubs rather than by an audio device or Spotify.
/// </summary>
/// <remarks>
/// This is the coverage the reference implementation's <c>Watcher</c> never had: it took the
/// WinForms form as a constructor argument, so nothing about the session could be asserted
/// without a UI. Here the only real thing is the in-memory file system.
/// </remarks>
public sealed class RecordingSessionTests
{
    private const string MusicRoot = @"C:\music";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>100 bytes to a "second", so a few writes clear a 2-second minimum.</summary>
    private static WaveFormat TinyFormat() => new(50, 8, 2);

    /// <summary>A capture source the test drives by hand.</summary>
    /// <param name="format">
    /// Defaults to <see cref="TinyFormat"/>, which keeps recordings short. The level-meter tests
    /// pass a 16-bit format instead, because 8-bit is not one the meter reads.
    /// </param>
    private sealed class FakeCaptureSource(WaveFormat? format = null) : IAudioCaptureSource
    {
        public WaveFormat Format { get; } = format ?? TinyFormat();

        public bool IsCapturing { get; private set; }

        public bool WasDisposed { get; private set; }

        public event EventHandler<AudioDataEventArgs>? DataAvailable;

        public event EventHandler<CaptureStoppedEventArgs>? Stopped;

        public void StartCapture() => IsCapturing = true;

        public void StopCapture()
        {
            IsCapturing = false;
            Stopped?.Invoke(this, new CaptureStoppedEventArgs(null));
        }

        public void Dispose() => WasDisposed = true;

        /// <summary>Delivers audio the way WASAPI would.</summary>
        public void Deliver(int bytes)
        {
            var buffer = new byte[bytes];
            for (var i = 0; i < bytes; i++) buffer[i] = (byte)(i % 251 + 1);

            DataAvailable?.Invoke(this, new AudioDataEventArgs(buffer, bytes));
        }

        /// <summary>Delivers an exact buffer, for asserting on what the level meter made of it.</summary>
        public void Deliver(byte[] buffer) =>
            DataAvailable?.Invoke(this, new AudioDataEventArgs(buffer, buffer.Length));
    }

    /// <summary>An encoder that produces a plausible file instead of running ffmpeg.</summary>
    private sealed class FakeEncoder(MockFileSystem fileSystem) : IAudioEncoder
    {
        public ConcurrentQueue<EncodeRequest> Requests { get; } = new();

        /// <summary>Set to make the encode fail, as a missing ffmpeg would.</summary>
        public Exception? Failure { get; set; }

        public Task<EncodeOutcome> EncodeAsync(
            EncodeRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);

            if (Failure is not null) return Task.FromException<EncodeOutcome>(Failure);

            fileSystem.AddFile(request.OutputPath, new MockFileData("encoded"));

            return Task.FromResult(new EncodeOutcome(request));
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Mock<ITrackSource> _trackSource = new();

        /// <param name="fileSystemDelay">
        /// Artificially delays every file the session opens. Widens the window in the
        /// track-change race far past anything real thread-pool scheduling would produce, so a
        /// test can prove the fix deterministically instead of merely exercising the race and
        /// hoping to catch it.
        /// </param>
        public Harness(
            Action<RecordingSettings>? configure = null,
            TimeSpan? fileSystemDelay = null,
            WaveFormat? captureFormat = null)
        {
            Capture = new FakeCaptureSource(captureFormat);

            FileSystem = new MockFileSystem();
            FileSystem.Directory.CreateDirectory(MusicRoot);

            Settings = new RecordingSettings
            {
                OutputPath = MusicRoot,
                OutputTemplate = "{artist} - {title}",
                MediaFormat = MediaFormat.Mp3,
                BitrateKbps = 320,
                MinimumRecordedLengthSeconds = 2,
            };

            configure?.Invoke(Settings);

            Encoder = new FakeEncoder(FileSystem);
            Poller = new SpotifyPoller(_trackSource.Object);

            _trackSource
                .Setup(x => x.GetCurrentTrackAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Current);

            IFileSystem sessionFileSystem = fileSystemDelay is { } delay
                ? new DelayedFileSystem(FileSystem, delay)
                : FileSystem;

            Session = new RecordingSession(
                Capture, Poller, Settings, Encoder, sessionFileSystem, Progress);

            Session.TrackSaved += (_, e) => Saved.Enqueue(e);
            Session.TrackRecorded += (_, e) => Recorded.Enqueue(e);
            Session.Failed += (_, e) => Failures.Enqueue(e);
        }

        public MockFileSystem FileSystem { get; }

        public RecordingSettings Settings { get; }

        public FakeCaptureSource Capture { get; }

        public FakeEncoder Encoder { get; }

        public SpotifyPoller Poller { get; }

        public RecordingSession Session { get; }

        public ConcurrentQueue<RecordingProgress> Reports { get; } = new();

        public ConcurrentQueue<TrackSavedEventArgs> Saved { get; } = new();

        public ConcurrentQueue<TrackRecordedEventArgs> Recorded { get; } = new();

        public ConcurrentQueue<RecordingFailedEventArgs> Failures { get; } = new();

        public IProgress<RecordingProgress> Progress => new Progress<RecordingProgress>(Reports.Enqueue);

        /// <summary>What Spotify currently reports.</summary>
        public Track? Current { get; private set; }

        public static Track Playing(string artist, string title) =>
            new() { Artist = artist, Title = title, Playing = true };

        /// <summary>
        /// Changes what Spotify reports. The session owns the poller, so the running poll loop
        /// picks this up on its own — driving <c>PollOnceAsync</c> by hand here would race with it.
        /// </summary>
        public void Play(Track? track) => Current = track;

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            Capture.Dispose();
        }
    }

    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {because}.");
    }

    /// <summary>Plays a track and feeds it audio, leaving it recording.</summary>
    private static async Task RecordTrackAsync(Harness harness, Track track, int bytes = 500)
    {
        harness.Play(track);

        await WaitFor(
            () => harness.Session.CurrentTrack?.Title == track.Title, $"the recorder to start on {track.Title}");

        harness.Capture.Deliver(bytes);
    }

    [Fact]
    public async Task Session_RecordsATrackAndSavesItUnderTheTemplateName()
    {
        await using var harness = new Harness();

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));
        harness.Play(Harness.Playing("Artist", "Next"));

        await WaitFor(() => !harness.Saved.IsEmpty, "the recording to be saved");

        Assert.True(harness.Saved.TryDequeue(out var saved));
        Assert.Equal(@"C:\music\Artist - Title.mp3", saved!.Path);
        Assert.True(harness.FileSystem.File.Exists(saved.Path));
    }

    /// <summary>
    /// The temp WAV and the temp encode output are both scratch files; neither belongs in the
    /// user's library once the recording has landed.
    /// </summary>
    [Fact]
    public async Task Session_CleansUpItsTemporaryFiles()
    {
        await using var harness = new Harness();

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));
        harness.Play(Harness.Playing("Artist", "Next"));

        await WaitFor(() => !harness.Saved.IsEmpty, "the recording to be saved");

        // After the session has stopped: a recording still in progress legitimately holds an
        // open temp WAV, so asserting mid-session would be asserting the wrong thing.
        await harness.Session.StopAsync();

        Assert.DoesNotContain(harness.FileSystem.AllFiles, f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Session_QueuesTheEncodeWithTheConfiguredFormatAndBitrate()
    {
        await using var harness = new Harness(s =>
        {
            s.MediaFormat = MediaFormat.Flac;
            s.BitrateKbps = 192;
        });

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));
        harness.Play(Harness.Playing("Artist", "Next"));

        await WaitFor(() => !harness.Encoder.Requests.IsEmpty, "the encode to be queued");

        Assert.True(harness.Encoder.Requests.TryDequeue(out var request));
        Assert.Equal(MediaFormat.Flac, request!.Format);
        Assert.Equal(192, request.BitrateKbps);
        Assert.Equal("Title", request.Track?.Title);
    }

    /// <summary>Ads are not tracks. Recording them is opt-in, and off by default.</summary>
    [Fact]
    public async Task Session_DoesNotRecordAdvertisements()
    {
        await using var harness = new Harness();

        harness.Session.Start();

        harness.Play(new Track { Artist = "Spotify", Ad = true, Playing = true });
        await Task.Delay(300);

        Assert.Null(harness.Session.CurrentTrack);
        Assert.Empty(harness.Encoder.Requests);
    }

    [Fact]
    public async Task Session_StopsTheRunningRecorderWhenTheTrackChanges()
    {
        await using var harness = new Harness();

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "First"));
        await RecordTrackAsync(harness, Harness.Playing("Artist", "Second"));

        await WaitFor(() => !harness.Saved.IsEmpty, "the first recording to be saved");

        Assert.Equal("Second", harness.Session.CurrentTrack?.Title);
        Assert.Contains(harness.Saved, s => s.Path.EndsWith(@"Artist - First.mp3", StringComparison.Ordinal));
    }

    /// <summary>
    /// Back-to-back track changes must never lose the outgoing track's audio to the incoming
    /// one's buffer discard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two recorders share one <see cref="AudioCaptureBuffer"/>. The incoming recorder's
    /// <c>Prime()</c> discards whatever is in it, on the theory that leftover audio belongs to
    /// the track that just ended. That is only true once the outgoing recorder has actually
    /// finished reading its own tail out of the buffer — and without a wait for that,
    /// <c>Prime()</c> can run first, on the poll loop, before the outgoing recorder's
    /// background task ever gets scheduled. <see cref="RecordingSession.OnTrackChanged"/> now
    /// blocks on <c>TrackRecorder.BufferDrained</c> for exactly this reason.
    /// </para>
    /// <para>
    /// Run many times in a tight loop, the same way <c>TrackRecorderTests.Stop_DoesNotDiscardTheChunkAlreadyInFlight</c>
    /// races the equivalent case one level down — a single pass proves nothing about a race
    /// that depends on which of two tasks a thread pool happens to schedule first.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Session_RapidTrackChangesNeverDropTheOutgoingTracksAudio()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var harness = new Harness();

            harness.Session.Start();

            for (var i = 0; i < 4; i++)
            {
                await RecordTrackAsync(harness, Harness.Playing("Artist", $"Track {i}"));
            }

            await harness.Session.StopAsync();

            Assert.DoesNotContain(harness.Recorded, r => r.Outcome == RecordingOutcome.Silent);
            Assert.Equal(4, harness.Saved.Count);
        }
    }

    /// <summary>
    /// The deterministic counterpart to the stress test above. Racing real background-thread
    /// scheduling only reproduces the original bug when the thread pool happens to be slow
    /// enough at exactly the wrong moment — real, but too rare to fail reliably even across
    /// hundreds of iterations on a typical dev machine. A <see cref="DelayedFileSystem"/>
    /// stretches the outgoing recorder's file-open past the poll interval on every run, which
    /// turns "usually wins the race" into "always does" — reproducing the bug on every run
    /// without the fix, and proving it closed with it.
    /// </summary>
    [Fact]
    public async Task Session_TrackChangeWaitsForTheOutgoingRecorderToDrainTheBufferFirst()
    {
        await using var harness = new Harness(fileSystemDelay: TimeSpan.FromMilliseconds(300));

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "First"));
        harness.Play(Harness.Playing("Artist", "Second"));

        await WaitFor(() => !harness.Recorded.IsEmpty, "the first track to be reported");

        Assert.True(harness.Recorded.TryDequeue(out var first));
        Assert.Equal("First", first!.Track.Title);
        Assert.Equal(RecordingOutcome.Captured, first.Outcome);
    }

    /// <summary>
    /// A track cut short after a couple of seconds should leave nothing behind — no file, and
    /// no half-finished encode in the queue.
    /// </summary>
    [Fact]
    public async Task Session_DiscardsATrackShorterThanTheMinimum()
    {
        await using var harness = new Harness(s => s.MinimumRecordedLengthSeconds = 30);

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"), bytes: 300);
        harness.Play(Harness.Playing("Artist", "Next"));

        await WaitFor(
            () => harness.Recorded.Any(r => r.Outcome == RecordingOutcome.TooShort),
            "the short recording to be discarded");

        Assert.Empty(harness.Encoder.Requests);
        Assert.Empty(harness.Saved);
    }

    /// <summary>
    /// Nothing captured means Spotify is playing to a device this session is not recording —
    /// worth saying plainly, because the fix is a settings change rather than a retry.
    /// </summary>
    [Fact]
    public async Task Session_ReportsWhenNothingWasCaptured()
    {
        await using var harness = new Harness();

        harness.Session.Start();

        harness.Play(Harness.Playing("Artist", "Title"));
        await WaitFor(() => harness.Session.CurrentTrack is not null, "the recorder to start");

        harness.Play(Harness.Playing("Artist", "Next"));

        await WaitFor(() => !harness.Failures.IsEmpty, "the empty capture to be reported");

        Assert.True(harness.Failures.TryDequeue(out var failure));
        Assert.Contains("different audio device", failure!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The captured audio is the part that cannot be recreated. A missing or broken ffmpeg must
    /// not take it with it — the WAV stays, and the message says where.
    /// </summary>
    [Fact]
    public async Task Session_WhenEncodingFails_KeepsTheCapturedWave()
    {
        await using var harness = new Harness();

        harness.Encoder.Failure = new FFmpegException("ffmpeg is not installed.");
        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));
        harness.Play(Harness.Playing("Artist", "Next"));

        await WaitFor(() => !harness.Failures.IsEmpty, "the encode failure to be reported");

        Assert.True(harness.Failures.TryDequeue(out var failure));
        Assert.Contains("ffmpeg is not installed.", failure!.Message, StringComparison.Ordinal);

        Assert.True(harness.Encoder.Requests.TryDequeue(out var request));
        Assert.True(
            harness.FileSystem.File.Exists(request!.InputPath),
            "the captured WAV should survive a failed encode");
    }

    /// <summary>Stopping mid-song keeps what has played rather than throwing it away.</summary>
    [Fact]
    public async Task StopAsync_FinishesTheTrackInProgress()
    {
        await using var harness = new Harness();

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));
        await harness.Session.StopAsync();

        Assert.False(harness.Session.IsRunning);
        Assert.False(harness.Capture.IsCapturing);
        Assert.Single(harness.Saved);
        Assert.Equal(0, harness.Session.PendingEncodes);
    }

    [Fact]
    public async Task Start_IsIdempotent()
    {
        await using var harness = new Harness();

        harness.Session.Start();
        harness.Session.Start();

        Assert.True(harness.Session.IsRunning);

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));
        await harness.Session.StopAsync();

        Assert.Single(harness.Saved);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var harness = new Harness();

        harness.Session.Start();

        await harness.Session.DisposeAsync();
        await harness.Session.DisposeAsync();

        await harness.DisposeAsync();
    }

    [Fact]
    public async Task Session_ReportsProgressForTheShell()
    {
        await using var harness = new Harness();

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));
        await harness.Session.StopAsync();

        await WaitFor(
            () => harness.Reports.Any(r => r.Stage == RecordingStage.Recording),
            "a recording progress report");

        Assert.Contains(harness.Reports, r => r.Stage == RecordingStage.Stopped);
    }

    /// <summary>
    /// The shell shows a running time. It comes from the poller through progress rather than
    /// from a timer in the UI, so a paused or stalled session stops counting on its own.
    /// </summary>
    [Fact]
    public async Task Session_ReportsElapsedTimeWhileATrackPlays()
    {
        await using var harness = new Harness();

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));

        await WaitFor(
            () => harness.Reports.Any(r => r.Elapsed is not null && r.Track is not null),
            "an elapsed-time report naming the track");
    }

    [Fact]
    public async Task Level_MeasuresWhatCaptureDelivers()
    {
        await using var harness = new Harness(captureFormat: new WaveFormat(44100, 16, 2));

        harness.Session.Start();

        Assert.True(harness.Session.Level.IsSupported);

        harness.Capture.Deliver(BitConverter.GetBytes((short)16384));

        Assert.Equal(0.5f, harness.Session.Level.Read(), precision: 4);
    }

    /// <summary>
    /// Nothing drains the meter once a session stops, so a peak left in it would freeze the
    /// display at whatever was playing when the user pressed stop.
    /// </summary>
    [Fact]
    public async Task Level_IsClearedWhenTheSessionStops()
    {
        await using var harness = new Harness(captureFormat: new WaveFormat(44100, 16, 2));

        harness.Session.Start();
        harness.Capture.Deliver(BitConverter.GetBytes(short.MaxValue));

        await harness.Session.StopAsync();

        Assert.Equal(0f, harness.Session.Level.Read());
    }
}
