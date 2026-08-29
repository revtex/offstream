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

        /// <summary>Ends the capture the way a lost endpoint does: without being asked to.</summary>
        public void Lose(string reason)
        {
            IsCapturing = false;
            Stopped?.Invoke(this, new CaptureStoppedEventArgs(new InvalidOperationException(reason)));
        }

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

    /// <summary>A metadata lookup that answers immediately, from a script.</summary>
    private sealed class FakeEnricher : ITrackEnricher
    {
        /// <summary>What to write onto the track, standing in for a provider's response.</summary>
        public Action<Track>? Apply { get; set; }

        /// <summary>The cover file the fetch "produced", or null for no art.</summary>
        public string? CoverArtPath { get; set; }

        public int Calls { get; private set; }

        /// <summary>Which tracks were looked up, in order.</summary>
        public List<string?> Tracks { get; } = [];

        /// <summary>Whether the lookup hangs, as a real one still chasing a provider would.</summary>
        public bool NeverAnswers { get; set; }

        /// <summary>
        /// Held open, the lookup does not come back until the test lets it — which is how a
        /// provider that answers three songs later is reproduced without a real delay.
        /// </summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>The token the session gave the most recent lookup.</summary>
        public CancellationToken Token { get; private set; }

        public Task<TrackEnrichment> EnrichAsync(Track track, CancellationToken cancellationToken = default)
        {
            Calls++;
            Tracks.Add(track.Title);
            Token = cancellationToken;

            Apply?.Invoke(track);

            if (NeverAnswers) return NeverAsync(cancellationToken);

            return Gate is { } gate ? AfterAsync(gate) : Task.FromResult(new TrackEnrichment(Updated: true, CoverArtPath));
        }

        private async Task<TrackEnrichment> AfterAsync(TaskCompletionSource gate)
        {
            await gate.Task;

            return new TrackEnrichment(Updated: true, CoverArtPath);
        }

        private static async Task<TrackEnrichment> NeverAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);

            return TrackEnrichment.None;
        }
    }

    /// <summary>Spotify's transport, counting what the session asked it to do.</summary>
    private sealed class FakePlaybackControl : IPlaybackControl
    {
        private int _skips;

        /// <summary>Whether Spotify takes the command, as it declines to during an advertisement.</summary>
        public bool Accepts { get; set; } = true;

        /// <summary>Set to make the transport throw, as one whose session has gone away does.</summary>
        public Exception? Failure { get; set; }

        public int Skips => Volatile.Read(ref _skips);

        public Task<bool> TrySkipNextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _skips);

            return Failure is not null ? Task.FromException<bool>(Failure) : Task.FromResult(Accepts);
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
        /// <param name="enricher">
        /// The metadata lookup the session runs per track. Null is "no provider configured",
        /// which is what most of these tests want: they are about the pipeline, not the tags.
        /// </param>
        public Harness(
            Action<RecordingSettings>? configure = null,
            TimeSpan? fileSystemDelay = null,
            WaveFormat? captureFormat = null,
            ITrackEnricher? enricher = null,
            FakePlaybackControl? playback = null)
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

            Playback = playback;

            Session = new RecordingSession(
                Capture, Poller, Settings, Encoder, sessionFileSystem, enricher, Progress, playback: playback);

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

        /// <summary>Null when the session was built without a transport, as most tests want.</summary>
        public FakePlaybackControl? Playback { get; }

        public ConcurrentQueue<RecordingProgress> Reports { get; } = new();

        public ConcurrentQueue<TrackSavedEventArgs> Saved { get; } = new();

        public ConcurrentQueue<TrackRecordedEventArgs> Recorded { get; } = new();

        public ConcurrentQueue<RecordingFailedEventArgs> Failures { get; } = new();

        public IProgress<RecordingProgress> Progress => new Progress<RecordingProgress>(Reports.Enqueue);

        /// <summary>What Spotify currently reports.</summary>
        public Track? Current { get; private set; }

        public static Track Playing(string artist, string title) =>
            new() { Artist = artist, Title = title, Playing = true };

        /// <summary>The same song, showing in Spotify but stopped.</summary>
        public static Track Paused(string artist, string title) =>
            new() { Artist = artist, Title = title, Playing = false };

        /// <summary>
        /// Changes what Spotify reports. The session owns the poller, so the running poll loop
        /// picks this up on its own — driving <c>PollOnceAsync</c> by hand here would race with it.
        /// </summary>
        public void Play(Track? track) => Current = track;

        /// <summary>
        /// Only the session is disposed here: disposing the capture is <em>its</em> job, and a
        /// harness that did it too would hide the day that stopped being true.
        /// </summary>
        public async ValueTask DisposeAsync() => await Session.DisposeAsync();
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

    /// <summary>
    /// Gets a session past the track it will not skip, so a test can be about skipping.
    /// </summary>
    /// <remarks>
    /// The first track a session admits is deliberately never a skip candidate — it is the song
    /// already under way when the user pressed record, and it is what the media session reports
    /// for a moment before it catches up. A track that is <em>already on disk</em> is used for
    /// this on purpose: it arms the session without starting a recorder, so nothing is left in
    /// flight to save itself half-way through the assertions that follow.
    /// </remarks>
    private static async Task ArmSkippingAsync(Harness harness)
    {
        harness.FileSystem.AddFile(@"C:\music\Warm Up - First Track.mp3", new MockFileData("already here"));
        harness.Play(Harness.Playing("Warm Up", "First Track"));

        await WaitFor(
            () => harness.Reports.Any(r => r.Track?.Contains("First Track", StringComparison.Ordinal) == true),
            "the session to admit its first track");
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

    /// <summary>
    /// The bug this closes: a recording carried the window title's artist and title and nothing
    /// else — no album, no track number, no year, no art — because nothing in the pipeline ever
    /// called a metadata provider.
    /// </summary>
    [Fact]
    public async Task Session_QueuesTheEncodeWithWhatTheProviderFoundAndTheCoverArtItFetched()
    {
        var enricher = new FakeEnricher
        {
            CoverArtPath = @"C:\temp\cover.jpg",
            Apply = track =>
            {
                track.Album = "Album";
                track.AlbumPosition = 7;
                track.Disc = 2;
                track.Year = 1997;
                track.Genres = ["Post-rock"];
                track.AlbumArtists = ["Album Artist"];
            },
        };

        await using var harness = new Harness(enricher: enricher);

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));
        harness.Play(Harness.Playing("Artist", "Next"));

        await WaitFor(() => !harness.Encoder.Requests.IsEmpty, "the encode to be queued");

        Assert.True(harness.Encoder.Requests.TryDequeue(out var request));
        Assert.Equal(@"C:\temp\cover.jpg", request!.CoverArtPath);
        Assert.Equal("Album", request.Track?.Album);
        Assert.Equal(7, request.Track?.AlbumPosition);
        Assert.Equal(2, request.Track?.Disc);
        Assert.Equal(1997, request.Track?.Year);

        // The tag arguments are what actually reach ffmpeg, so assert those rather than trusting
        // that a populated Track implies a populated file.
        var arguments = FFmpegArguments.Build(request);

        Assert.Contains("album=Album", arguments, StringComparer.Ordinal);
        Assert.Contains("track=7", arguments, StringComparer.Ordinal);
        Assert.Contains("disc=2", arguments, StringComparer.Ordinal);
        Assert.Contains("date=1997", arguments, StringComparer.Ordinal);
        Assert.Contains("genre=Post-rock", arguments, StringComparer.Ordinal);
        Assert.Contains("album_artist=Album Artist", arguments, StringComparer.Ordinal);
    }

    /// <summary>
    /// Enrichment starts when the track does, so it overlaps the recording rather than delaying
    /// the file — but the encode request is still built from what it found.
    /// </summary>
    [Fact]
    public async Task Session_EnrichesEachTrackOnce()
    {
        var enricher = new FakeEnricher { Apply = track => track.Album = "Album" };

        await using var harness = new Harness(enricher: enricher);

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "One"));
        await RecordTrackAsync(harness, Harness.Playing("Artist", "Two"));
        harness.Play(Harness.Playing("Artist", "Three"));

        await WaitFor(() => harness.Encoder.Requests.Count == 2, "both encodes to be queued");

        // Three, not two: the track that ends the second one is itself recording by now, and is
        // looked up as soon as it starts rather than when it finishes. What matters is that no
        // track is looked up twice.
        Assert.Equal(["One", "Two", "Three"], enricher.Tracks);
        Assert.Equal(enricher.Tracks.Count, enricher.Tracks.Distinct().Count());
    }

    /// <summary>
    /// The counter goes into the track-number tag when asked, without disturbing the
    /// <c>{track}</c> filename token, which keeps meaning the position within the album.
    /// </summary>
    [Fact]
    public async Task Session_WhenTheCounterIsTagged_OverridesTheTrackNumber()
    {
        var enricher = new FakeEnricher { Apply = track => track.AlbumPosition = 7 };

        await using var harness = new Harness(
            s =>
            {
                s.OrderNumberInMediaTagEnabled = true;
                s.InternalOrderNumber = 42;
            },
            enricher: enricher);

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));
        harness.Play(Harness.Playing("Artist", "Next"));

        await WaitFor(() => !harness.Encoder.Requests.IsEmpty, "the encode to be queued");

        Assert.True(harness.Encoder.Requests.TryDequeue(out var request));
        Assert.Equal(42, request!.TrackNumberOverride);
        Assert.Contains("track=42", FFmpegArguments.Build(request), StringComparer.Ordinal);
    }

    /// <summary>The fetched cover is a scratch file, and goes the same way as the temp WAV.</summary>
    [Fact]
    public async Task Session_DeletesTheFetchedCoverArtOnceTheEncodeIsDone()
    {
        const string CoverPath = @"C:\temp\cover.jpg";

        var enricher = new FakeEnricher { CoverArtPath = CoverPath, Apply = track => track.Album = "Album" };

        await using var harness = new Harness(enricher: enricher);

        harness.FileSystem.AddFile(CoverPath, new MockFileData([1, 2, 3]));

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));
        harness.Play(Harness.Playing("Artist", "Next"));

        await WaitFor(() => !harness.Saved.IsEmpty, "the recording to be saved");

        Assert.False(harness.FileSystem.File.Exists(CoverPath));
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
    /// The lookup belongs to the recording rather than to the session, and it ends with it.
    /// A discarded fragment has nothing left to tag, so the lookup it started is stopped instead
    /// of being left to spend the next several seconds asking about a track that has already
    /// ended — and the track that replaced it keeps a lookup of its own, which is what makes this
    /// a per-recording cancellation rather than the session's.
    /// </summary>
    [Fact]
    public async Task Session_DiscardingAShortRecording_CancelsOnlyThatTracksLookup()
    {
        var enricher = new FakeEnricher { NeverAnswers = true };

        await using var harness = new Harness(s => s.MinimumRecordedLengthSeconds = 30, enricher: enricher);

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"), bytes: 300);

        var discarded = enricher.Token;

        harness.Play(Harness.Playing("Artist", "Next"));

        await WaitFor(
            () => harness.Recorded.Any(r => r.Outcome == RecordingOutcome.TooShort),
            "the short recording to be discarded");

        await WaitFor(
            () => discarded.IsCancellationRequested,
            "the discarded recording's lookup to be cancelled");

        Assert.False(enricher.Token.IsCancellationRequested);
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

    /// <summary>
    /// A failure is narrated once, on the event, and not a second time on the progress report.
    /// </summary>
    /// <remarks>
    /// Both ended up in the activity log — the event at Error and the report's message at
    /// Information — so every failure was printed twice, identically, once in a colour that said
    /// act on this and once in one that said carry on. The report itself still has to fire,
    /// because it is what moves the display back to waiting; it just no longer carries the text.
    /// </remarks>
    [Fact]
    public async Task Session_WhenEncodingFails_DoesNotAlsoNarrateItOnProgress()
    {
        await using var harness = new Harness();

        harness.Encoder.Failure = new FFmpegException("ffmpeg is not installed.");
        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));
        harness.Play(Harness.Playing("Artist", "Next"));

        await WaitFor(() => !harness.Failures.IsEmpty, "the encode failure to be reported");

        Assert.True(harness.Failures.TryDequeue(out var failure));
        Assert.Contains("ffmpeg is not installed.", failure!.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(
            harness.Reports,
            report => report.Message is { } message
                      && message.Contains("ffmpeg is not installed.", StringComparison.Ordinal));
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

    /// <summary>
    /// Every report says what is playing, separately from what the report is about.
    /// </summary>
    /// <remarks>
    /// The two are not the same. A track is encoded, tagged and saved while the next one is
    /// already recording, so those reports name a song that has finished — and a shell that read
    /// the subject as the now-playing line put the previous track back on the page, taking the
    /// album, cover art and destination of the one actually recording down with it. Reporting
    /// both facts is what lets the shell tell them apart; the flag is here so it does not have to
    /// compare names, which two plays of the same song would defeat.
    /// </remarks>
    [Fact]
    public async Task Session_ReportsWhatIsPlayingAlongsideWhatTheReportIsAbout()
    {
        await using var harness = new Harness();

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));

        await WaitFor(
            () => harness.Reports.Any(r => r.Stage == RecordingStage.Recording && r.NowPlaying == "Artist - Title"),
            "a recording report naming what is playing");

        // The invariant the shell leans on: a report that claims to be about the live track has
        // to actually name it, or the elapsed counter it carries belongs to something else.
        Assert.DoesNotContain(
            harness.Reports,
            r => r.ConcernsNowPlaying && r.Track is not null && r.Track != r.NowPlaying);
    }

    /// <summary>
    /// Pressing record with Spotify paused, then pressing play, has to start recording.
    /// </summary>
    /// <remarks>
    /// Playing is half of what makes a track recordable, and the check ran only when the track
    /// changed. Since <see cref="Track.Equals"/> ignores the play state, a song that starts
    /// playing is not a new track — so the session skipped it once as not recordable and then
    /// waited for a change that never came. What the user saw was a level meter moving, a counter
    /// running, and no file, until they stopped and started again.
    /// </remarks>
    [Fact]
    public async Task Session_WhenPlaybackStartsOnTheTrackAlreadyShowing_RecordsIt()
    {
        await using var harness = new Harness();

        harness.Session.Start();
        harness.Play(Harness.Paused("Artist", "Title"));

        await WaitFor(
            () => harness.Reports.Any(r => r.Message?.Contains("Not a recordable track", StringComparison.Ordinal) == true),
            "the paused track to be passed over");

        Assert.Null(harness.Session.CurrentTrack);

        harness.Play(Harness.Playing("Artist", "Title"));

        await WaitFor(
            () => harness.Session.CurrentTrack?.Title == "Title",
            "the recorder to start once playback begins");
    }

    /// <summary>
    /// And exactly one recorder for it. Starting with Spotify already playing is a single poll
    /// that sees both a new track and a change of play state, and both used to be a reason to
    /// start — the second one tearing down the first and discarding the fragment as too short.
    /// </summary>
    [Fact]
    public async Task Session_WhenPlaybackIsAlreadyUnderWay_StartsOneRecorder()
    {
        await using var harness = new Harness();

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));

        await harness.Session.StopAsync();

        await WaitFor(() => !harness.Recorded.IsEmpty, "the recording to finish");

        Assert.Single(harness.Recorded);
    }

    [Fact]
    public async Task Level_MeasuresWhatCaptureDelivers()
    {
        await using var harness = new Harness(captureFormat: new WaveFormat(44100, 16, 2));

        harness.Session.Start();

        Assert.True(harness.Session.Level.IsSupported);

        harness.Capture.Deliver(BitConverter.GetBytes((short)16384));

        // Half scale is -6.02 dBFS, which on the meter's 60 dB scale is 0.8997. What matters
        // here is that capture reached the meter at all; the decoding is AudioLevelMeterTests.
        Assert.Equal(0.8997f, harness.Session.Level.Read().Level, precision: 4);
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

        Assert.Equal(0f, harness.Session.Level.Read().Level);
    }

    /// <summary>
    /// The capture holds a WASAPI client and an endpoint-notification registration, and Windows
    /// goes on calling that registration until it is withdrawn. Stopping does not withdraw it —
    /// only disposing does — so a session that stopped without disposing its capture left the
    /// audio service calling into an object nothing was keeping alive, and a few start/stop cycles
    /// ended the process with an access violation that never reached a log.
    /// </summary>
    [Fact]
    public async Task Session_DisposesTheCaptureItWasGiven()
    {
        var harness = new Harness();

        harness.Session.Start();
        await harness.Session.StopAsync();

        Assert.False(harness.Capture.WasDisposed);

        await harness.DisposeAsync();

        Assert.True(harness.Capture.WasDisposed);
    }

    /// <summary>
    /// The capture is opened when the session is built, not when it starts, so a session that
    /// never ran is still holding one — the case a failed start leaves behind.
    /// </summary>
    [Fact]
    public async Task Session_DisposesACaptureThatNeverStarted()
    {
        var harness = new Harness();

        await harness.DisposeAsync();

        Assert.True(harness.Capture.WasDisposed);
    }

    /// <summary>
    /// The endpoint watcher has reported a lost device since it was written, and nothing listened:
    /// the capture stopped and the session went on believing it was recording, with the elapsed
    /// counter frozen and the transport still offering to stop.
    /// </summary>
    [Fact]
    public async Task Session_EndsWhenTheCaptureStopsOnItsOwn()
    {
        await using var harness = new Harness();

        var ended = 0;
        harness.Session.Ended += (_, _) => Interlocked.Increment(ref ended);

        harness.Session.Start();
        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));

        harness.Capture.Lose("The audio endpoint 'Headphones' became unavailable during recording.");

        await WaitFor(() => Volatile.Read(ref ended) == 1, "the session to end itself");

        Assert.False(harness.Session.IsRunning);
    }

    /// <summary>
    /// What was captured before the endpoint went is as much of that track as will ever exist, so
    /// it is finished and written rather than abandoned — the same terms as pressing stop mid-song.
    /// </summary>
    [Fact]
    public async Task Session_SavesTheTrackInFlightWhenTheCaptureStops()
    {
        await using var harness = new Harness();

        harness.Session.Start();
        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));

        harness.Capture.Lose("The audio endpoint became unavailable during recording.");

        await WaitFor(() => !harness.Saved.IsEmpty, "the recording to be saved");

        Assert.True(harness.Saved.TryDequeue(out var saved));
        Assert.Equal(@"C:\music\Artist - Title.mp3", saved!.Path);
    }

    /// <summary>
    /// A recording that ends on its own without a word is indistinguishable from one that broke,
    /// and the endpoint's name is the part that says which device to plug back in.
    /// </summary>
    [Fact]
    public async Task Session_SaysWhyTheCaptureStopped()
    {
        await using var harness = new Harness();

        harness.Session.Start();
        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));

        harness.Capture.Lose("The audio endpoint 'Headphones' became unavailable during recording.");

        await WaitFor(() => !harness.Failures.IsEmpty, "the reason to be reported");

        Assert.True(harness.Failures.TryDequeue(out var failure));
        Assert.Contains("Headphones", failure!.Message, StringComparison.Ordinal);
        Assert.Contains("Recording has stopped.", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A session the user stopped has not "ended" in the sense the event means: its owner asked
    /// for the stop and is already tearing it down, and a second teardown from underneath that is
    /// how a session gets released twice.
    /// </summary>
    [Fact]
    public async Task Session_DoesNotRaiseEndedWhenItIsStopped()
    {
        await using var harness = new Harness();

        var ended = 0;
        harness.Session.Ended += (_, _) => Interlocked.Increment(ref ended);

        harness.Session.Start();
        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));

        await harness.Session.StopAsync();

        Assert.Equal(0, Volatile.Read(ref ended));
    }

    /// <summary>
    /// Declining to record a track the user already has leaves Spotify playing it to nobody.
    /// This is the setting that closes the loop and asks Spotify to move on.
    /// </summary>
    [Fact]
    public async Task Session_AsksSpotifyToMovePastATrackItAlreadyHas()
    {
        var playback = new FakePlaybackControl();

        await using var harness = new Harness(
            s => s.SkipAlreadyRecordedTracks = true, playback: playback);

        harness.FileSystem.AddFile(@"C:\music\Artist - Title.mp3", new MockFileData("already here"));

        harness.Session.Start();
        await ArmSkippingAsync(harness);

        harness.Play(Harness.Playing("Artist", "Title"));

        await WaitFor(() => playback.Skips == 1, "Spotify to be asked to move on");

        // And the file on disk is still the one that was there: skipping is in addition to the
        // existing policy, not instead of it.
        Assert.Equal("already here", harness.FileSystem.File.ReadAllText(@"C:\music\Artist - Title.mp3"));
        Assert.Null(harness.Session.CurrentTrack);
    }

    /// <summary>The setting is off by default, and off means Spotify is left alone.</summary>
    [Fact]
    public async Task Session_LeavesSpotifyAloneWhenSkippingIsOff()
    {
        var playback = new FakePlaybackControl();

        await using var harness = new Harness(playback: playback);

        harness.FileSystem.AddFile(@"C:\music\Artist - Title.mp3", new MockFileData("already here"));

        harness.Session.Start();
        await ArmSkippingAsync(harness);

        harness.Play(Harness.Playing("Artist", "Title"));

        await WaitFor(
            () => harness.Reports.Any(
                r => r.Track?.Contains("Title", StringComparison.Ordinal) == true
                    && r.Message?.Contains("Kept the file", StringComparison.Ordinal) == true),
            "the track to be declined");

        Assert.Equal(0, playback.Skips);
    }

    /// <summary>
    /// The UI greys the setting out under the other two policies, but a hand-edited settings file
    /// can still say true — so the pair is read together in the core rather than trusted from the
    /// page. Overwrite records the track again, and there is nothing to skip past.
    /// </summary>
    [Fact]
    public async Task Session_UnderAnOverwritePolicy_NeverAsksSpotifyToMoveOn()
    {
        var playback = new FakePlaybackControl();

        await using var harness = new Harness(
            s =>
            {
                s.ExistingFilePolicy = ExistingFilePolicy.Overwrite;
                s.SkipAlreadyRecordedTracks = true;
            },
            playback: playback);

        harness.FileSystem.AddFile(@"C:\music\Artist - Title.mp3", new MockFileData("already here"));

        harness.Session.Start();

        await RecordTrackAsync(harness, Harness.Playing("Warm Up", "First Track"));
        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));

        Assert.Equal(0, playback.Skips);
    }

    /// <summary>
    /// The one that matters. Spotify goes on reporting the outgoing track for a moment after it
    /// takes the command, so a session that decided per observation would ask twice — and the
    /// second skip lands on a song the user has not recorded and would have wanted.
    /// </summary>
    [Fact]
    public async Task Session_AsksToMovePastATrackOnlyOnce_HoweverLongSpotifyKeepsReportingIt()
    {
        var playback = new FakePlaybackControl();

        await using var harness = new Harness(
            s => s.SkipAlreadyRecordedTracks = true, playback: playback);

        harness.FileSystem.AddFile(@"C:\music\Artist - Title.mp3", new MockFileData("already here"));

        harness.Session.Start();
        await ArmSkippingAsync(harness);

        harness.Play(Harness.Playing("Artist", "Title"));

        await WaitFor(() => playback.Skips == 1, "Spotify to be asked to move on");

        // The stale window: the same song, still reported, pausing and resuming as the transport
        // catches up. Every one of these reaches Consider again.
        for (var i = 0; i < 4; i++)
        {
            harness.Play(Harness.Paused("Artist", "Title"));
            await Task.Delay(SpotifyPoller.PollInterval * 3);
            harness.Play(Harness.Playing("Artist", "Title"));
            await Task.Delay(SpotifyPoller.PollInterval * 3);
        }

        Assert.Equal(1, playback.Skips);

        // A different song that is also on disk is a separate decision, and gets its own ask.
        harness.FileSystem.AddFile(@"C:\music\Artist - Other.mp3", new MockFileData("also here"));
        harness.Play(Harness.Playing("Artist", "Other"));

        await WaitFor(() => playback.Skips == 2, "the next recorded track to be asked about too");
    }

    /// <summary>
    /// The check in <c>Consider</c> runs before the lookup, so a template built on <c>{album}</c>
    /// renders a path nothing will ever be written to and the track looks new. Wired only there,
    /// skipping would silently never fire for exactly the libraries organised well enough to want
    /// it; this is the second checkpoint, once the lookup has landed.
    /// </summary>
    [Fact]
    public async Task Session_WhenTheLookupRevealsTheFileIsAlreadyThere_AsksSpotifyToMoveOn()
    {
        var playback = new FakePlaybackControl();
        var enricher = new FakeEnricher { Apply = track => track.Album = "Album" };

        await using var harness = new Harness(
            s =>
            {
                s.OutputTemplate = @"{album}\{artist} - {title}";
                s.SkipAlreadyRecordedTracks = true;
            },
            enricher: enricher,
            playback: playback);

        harness.FileSystem.AddFile(@"C:\music\Album\Artist - Title.mp3", new MockFileData("already here"));

        harness.Session.Start();
        await ArmSkippingAsync(harness);

        // It starts recording, which is the early check being wrong — the un-enriched name is
        // "C:\music\Artist - Title.mp3" and nothing is there.
        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));

        await WaitFor(() => playback.Skips == 1, "Spotify to be asked to move on once the album was known");
    }

    /// <summary>
    /// A lookup outlives the track it belongs to — that is the point of starting it early. By the
    /// time it comes back the user may be two songs on, and skipping then would skip a song
    /// nobody has recorded.
    /// </summary>
    [Fact]
    public async Task Session_DoesNotAskToMovePastATrackTheUserHasAlreadyLeft()
    {
        var playback = new FakePlaybackControl();
        var gate = new TaskCompletionSource();
        var enricher = new FakeEnricher { Gate = gate, Apply = track => track.Album = "Album" };

        await using var harness = new Harness(
            s =>
            {
                s.OutputTemplate = @"{album}\{artist} - {title}";
                s.SkipAlreadyRecordedTracks = true;
            },
            enricher: enricher,
            playback: playback);

        harness.FileSystem.AddFile(@"C:\music\Album\Artist - Title.mp3", new MockFileData("already here"));

        harness.Session.Start();
        await ArmSkippingAsync(harness);

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Title"));

        // The user moves on before the provider answers.
        await RecordTrackAsync(harness, Harness.Playing("Artist", "Next"));

        gate.SetResult();

        await Task.Delay(SpotifyPoller.PollInterval * 5);

        Assert.Equal(0, playback.Skips);
        Assert.Equal("Next", harness.Session.CurrentTrack?.Title);
    }

    /// <summary>
    /// A transport that will not take commands is a broken convenience, not a broken recorder:
    /// detection has to carry on, and the next track still gets recorded.
    /// </summary>
    [Fact]
    public async Task Session_KeepsRecordingWhenTheSkipItselfFails()
    {
        var playback = new FakePlaybackControl
        {
            Failure = new InvalidOperationException("The media session went away."),
        };

        await using var harness = new Harness(
            s => s.SkipAlreadyRecordedTracks = true, playback: playback);

        harness.FileSystem.AddFile(@"C:\music\Artist - Title.mp3", new MockFileData("already here"));

        harness.Session.Start();
        await ArmSkippingAsync(harness);

        harness.Play(Harness.Playing("Artist", "Title"));

        await WaitFor(() => playback.Skips == 1, "the skip to be attempted");
        await WaitFor(
            () => harness.Reports.Any(r => r.Message?.Contains("went away", StringComparison.Ordinal) == true),
            "the reason to be reported");

        await RecordTrackAsync(harness, Harness.Playing("Artist", "Next"));
        harness.Play(Harness.Playing("Artist", "Third"));

        await WaitFor(() => !harness.Saved.IsEmpty, "the next track to be recorded anyway");
    }

    /// <summary>
    /// The terminating condition. Put a queue Offstream already has on repeat and every skip lands
    /// on another track that is also on disk, so without a ceiling the session drives Spotify
    /// round the queue forever at the speed of a media command rather than of a song. It stops,
    /// says why, and starts again as soon as a recording actually reaches the library — which is
    /// the only real evidence there is anything new left to record.
    /// </summary>
    [Fact]
    public async Task Session_StopsAskingAfterAWholeQueueOfTracksItAlreadyHas()
    {
        const string GaveUp = "Stopped asking Spotify to move on";
        const int Cap = 50;

        var playback = new FakePlaybackControl();

        await using var harness = new Harness(
            s => s.SkipAlreadyRecordedTracks = true, playback: playback);

        for (var i = 0; i < Cap + 5; i++)
        {
            harness.FileSystem.AddFile($@"C:\music\Artist - Track {i}.mp3", new MockFileData("already here"));
        }

        harness.Session.Start();
        await ArmSkippingAsync(harness);

        for (var i = 0; i < Cap + 5; i++)
        {
            var title = $"Track {i}";

            harness.Play(Harness.Playing("Artist", title));

            await WaitFor(
                () => harness.Reports.Any(r => r.Track?.Contains(title, StringComparison.Ordinal) == true),
                $"{title} to be considered");
        }

        Assert.Equal(Cap, playback.Skips);
        Assert.Equal(1, harness.Reports.Count(r => r.Message?.Contains(GaveUp, StringComparison.Ordinal) == true));

        // Two new tracks: the first records, the second ends it so it reaches the library.
        await RecordTrackAsync(harness, Harness.Playing("Artist", "Something New"));
        await RecordTrackAsync(harness, Harness.Playing("Artist", "Also New"));

        await WaitFor(() => !harness.Saved.IsEmpty, "the new recording to be saved");

        harness.Play(Harness.Playing("Artist", "Track 0"));

        await WaitFor(
            () => playback.Skips == Cap + 1, "skipping to resume now the queue has produced something new");
    }

    /// <summary>
    /// Pressing record does not mean "start rearranging what is playing". The song already under
    /// way is one the user chose and is part-way through; it was never going to be recorded whole,
    /// and cutting it off is a worse answer than letting it finish.
    /// </summary>
    /// <remarks>
    /// This is also the only defence against the media session's opening lie. Starting playback
    /// from a stopped Spotify reports the <em>previous</em> track for a few hundred milliseconds
    /// with the play state already true, so the first thing a session sees can be a song that is
    /// not playing at all — and a skip fired on that lands on the song the user has just started.
    /// </remarks>
    [Fact]
    public async Task Session_DoesNotSkipTheTrackThatWasAlreadyPlayingWhenRecordingStarted()
    {
        var playback = new FakePlaybackControl();

        await using var harness = new Harness(
            s => s.SkipAlreadyRecordedTracks = true, playback: playback);

        harness.FileSystem.AddFile(@"C:\music\Artist - Title.mp3", new MockFileData("already here"));
        harness.FileSystem.AddFile(@"C:\music\Artist - Next.mp3", new MockFileData("also here"));

        harness.Session.Start();
        harness.Play(Harness.Playing("Artist", "Title"));

        await WaitFor(
            () => harness.Reports.Any(
                r => r.Track?.Contains("Title", StringComparison.Ordinal) == true
                    && r.Message?.Contains("Kept the file", StringComparison.Ordinal) == true),
            "the track already playing to be declined");

        Assert.Equal(0, playback.Skips);

        // The next one is fair game: it began under Offstream's watch.
        harness.Play(Harness.Playing("Artist", "Next"));

        await WaitFor(() => playback.Skips == 1, "the track that started afterwards to be skipped");
    }
}
