using System.IO.Abstractions.TestingHelpers;
using NAudio.Wave;
using Offstream.Core.Audio;
using Offstream.Core.Metadata;
using Offstream.Core.Naming;
using Offstream.Core.Recording;
using Offstream.Core.Settings;
using Xunit;

namespace Offstream.Core.Tests.Recording;

/// <summary>
/// One track's capture, end to end, over an in-memory file system.
/// </summary>
/// <remarks>
/// These are where the reference implementation's <c>RecorderTests</c> land. They could not be
/// written against the original without a WinForms form mock, an audio session and NAudio.Lame;
/// here the recorder takes a capture buffer and an <c>IFileSystem</c>, and the audio is bytes.
/// </remarks>
public sealed class TrackRecorderTests
{
    private const string MusicRoot = @"C:\music";

    /// <summary>A "second" of audio is 100 bytes, so durations are easy to state exactly.</summary>
    private static WaveFormat TinyFormat() => new(50, 8, 2);

    private static Track SampleTrack() => new()
    {
        Artist = "Artist",
        Title = "Title",
        Playing = true,
    };

    private static RecordingSettings Settings(int minimumSeconds = 2) => new()
    {
        OutputPath = MusicRoot,
        MediaFormat = MediaFormat.Mp3,
        BitrateKbps = 320,
        MinimumRecordedLengthSeconds = minimumSeconds,
    };

    private static byte[] Audio(int length)
    {
        var data = new byte[length];

        for (var i = 0; i < length; i++) data[i] = (byte)(i % 251 + 1);

        return data;
    }

    private sealed class Harness : IDisposable
    {
        public Harness(
            int minimumSeconds = 2,
            Track? track = null,
            ExistingFilePolicy policy = ExistingFilePolicy.Overwrite,
            string? template = null,
            Func<Track, CancellationToken, Task<TrackEnrichment>>? enrich = null)
        {
            FileSystem = new MockFileSystem();
            FileSystem.Directory.CreateDirectory(MusicRoot);

            Settings = TrackRecorderTests.Settings(minimumSeconds);
            Settings.ExistingFilePolicy = policy;
            if (template is not null) Settings.OutputTemplate = template;

            Buffer = new AudioCaptureBuffer(TinyFormat());
            Track = track ?? SampleTrack();

            Paths = new OutputPaths(Settings, Track, FileSystem, new DateTime(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc));
            // Created before the lookup so the lookup can observe it, exactly as the session does.
            EnrichmentCancellation = enrich is null ? null : new CancellationTokenSource();

            Recorder = new TrackRecorder(
                Buffer,
                Settings,
                Track,
                Paths,
                FileSystem,
                enrich?.Invoke(Track, EnrichmentCancellation!.Token),
                EnrichmentCancellation);
        }

        public MockFileSystem FileSystem { get; }

        public RecordingSettings Settings { get; }

        public AudioCaptureBuffer Buffer { get; }

        public Track Track { get; }

        public OutputPaths Paths { get; }

        public TrackRecorder Recorder { get; }

        /// <summary>The lookup's cancellation, as the session hands it to the recorder.</summary>
        public CancellationTokenSource? EnrichmentCancellation { get; }

        public void Dispose() => Recorder.Dispose();

        /// <summary>Feeds audio, then stops the recorder once it has all been consumed.</summary>
        public async Task<TrackRecording> RecordAsync(int bytes)
        {
            var running = Recorder.RunAsync();

            if (bytes > 0)
            {
                // In chunk-sized pieces, as capture delivers it, so the recorder's read loop is
                // exercised rather than a single drain.
                for (var written = 0; written < bytes; written += Buffer.ChunkSize)
                {
                    Buffer.Write(Audio(Math.Min(Buffer.ChunkSize, bytes - written)));

                    while (Buffer.Count > 0 && !running.IsCompleted) await Task.Delay(5);
                }
            }

            Recorder.Stop();

            return await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Record_CapturesAudioAndReturnsAnEncodeRequest()
    {
        using var harness = new Harness();

        var recording = await harness.RecordAsync(bytes: 500);

        Assert.Equal(RecordingOutcome.Captured, recording.Outcome);
        Assert.Equal(5, recording.Duration.TotalSeconds, 1);
        Assert.NotNull(recording.Encode);
        Assert.True(harness.FileSystem.File.Exists(recording.Encode!.InputPath));
    }

    /// <summary>The captured file has to be a real WAV, header and all, or ffmpeg cannot read it.</summary>
    [Fact]
    public async Task Record_WritesAPlayableWaveFileWithTheCaptureFormat()
    {
        using var harness = new Harness();

        var recording = await harness.RecordAsync(bytes: 400);
        var bytes = harness.FileSystem.File.ReadAllBytes(recording.Encode!.InputPath);

        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("WAVE"u8.ToArray(), bytes[8..12]);

        using var reader = new WaveFileReader(new MemoryStream(bytes));

        Assert.Equal(TinyFormat().SampleRate, reader.WaveFormat.SampleRate);
        Assert.Equal(TinyFormat().Channels, reader.WaveFormat.Channels);
        Assert.Equal(400, reader.Length);
    }

    [Fact]
    public async Task Record_EncodesToATempFileWithTheFormatExtension()
    {
        using var harness = new Harness();

        var recording = await harness.RecordAsync(bytes: 500);

        Assert.EndsWith(".mp3", recording.Encode!.OutputPath, StringComparison.Ordinal);
        Assert.NotEqual(recording.Encode.InputPath, recording.Encode.OutputPath);

        // Not under the user's output folder: the destination name is claimed at rename time.
        Assert.DoesNotContain(MusicRoot, recording.Encode.OutputPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Record_CarriesTheTrackIntoTheEncodeRequest()
    {
        using var harness = new Harness();

        var recording = await harness.RecordAsync(bytes: 500);

        Assert.Same(harness.Track, recording.Encode!.Track);
        Assert.Equal(MediaFormat.Mp3, recording.Encode.Format);
        Assert.Equal(320, recording.Encode.BitrateKbps);
    }

    /// <summary>
    /// The template a user actually writes puts album, year and track number in the path, and
    /// none of those exist until the metadata lookup lands — so the destination is only knowable
    /// after enrichment. Checking any earlier looks at a path nothing is ever written to, always
    /// finds nothing, and lets the recording overwrite the file the user asked to keep.
    /// </summary>
    [Fact]
    public async Task Record_WithAnEnrichedTemplate_KeepsTheFileAlreadyOnDisk()
    {
        const string Template = @"{artist}\({year}) {album}\{track:00} {title}";
        const string Existing = @"C:\music\Artist\(1983) Album\04 Title.mp3";

        using var harness = new Harness(
            policy: ExistingFilePolicy.Skip,
            template: Template,
            enrich: (track, _) =>
            {
                track.Album = "Album";
                track.Year = 1983;
                track.AlbumPosition = 4;

                return Task.FromResult(TrackEnrichment.None);
            });

        harness.FileSystem.Directory.CreateDirectory(@"C:\music\Artist\(1983) Album");
        harness.FileSystem.File.WriteAllText(Existing, "the recording already there");

        var recording = await harness.RecordAsync(bytes: 500);

        Assert.Equal(RecordingOutcome.AlreadyRecorded, recording.Outcome);
        Assert.Equal(Existing, recording.Destination);
        Assert.Null(recording.Encode);

        // The point of the policy: what was there is untouched, and the capture is not left behind.
        Assert.Equal("the recording already there", harness.FileSystem.File.ReadAllText(Existing));
        Assert.Empty(harness.FileSystem.Directory.GetFiles(harness.FileSystem.Path.GetTempPath()));
    }

    /// <summary>The same template, with nothing on disk, records normally.</summary>
    [Fact]
    public async Task Record_WithAnEnrichedTemplateAndNothingOnDisk_Records()
    {
        using var harness = new Harness(
            policy: ExistingFilePolicy.Skip,
            template: @"{artist}\({year}) {album}\{track:00} {title}",
            enrich: (track, _) =>
            {
                track.Album = "Album";
                track.Year = 1983;
                track.AlbumPosition = 4;

                return Task.FromResult(TrackEnrichment.None);
            });

        var recording = await harness.RecordAsync(bytes: 500);

        Assert.Equal(RecordingOutcome.Captured, recording.Outcome);
        Assert.NotNull(recording.Encode);
    }

    /// <summary>
    /// The policy is Skip or nothing here. Overwrite and Duplicate both have work to do at the
    /// destination, and that work belongs to the rename, not to a recording that gets discarded.
    /// </summary>
    [Theory]
    [InlineData(ExistingFilePolicy.Overwrite)]
    [InlineData(ExistingFilePolicy.Duplicate)]
    public async Task Record_WithAnExistingFileAndAnotherPolicy_StillRecords(ExistingFilePolicy policy)
    {
        using var harness = new Harness(policy: policy, template: "{artist} - {title}");

        harness.FileSystem.File.WriteAllText(@"C:\music\Artist - Title.mp3", "already there");

        var recording = await harness.RecordAsync(bytes: 500);

        Assert.Equal(RecordingOutcome.Captured, recording.Outcome);
    }

    /// <summary>
    /// A track skipped after a few seconds is not worth a file. The reference deleted it too,
    /// but decided on a timer count rather than on the audio it actually had.
    /// </summary>
    [Fact]
    public async Task Record_ShorterThanTheMinimum_IsDiscarded()
    {
        using var harness = new Harness(minimumSeconds: 30);

        var recording = await harness.RecordAsync(bytes: 500);

        Assert.Equal(RecordingOutcome.TooShort, recording.Outcome);
        Assert.Null(recording.Encode);
        Assert.DoesNotContain(harness.FileSystem.AllFiles, f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    /// <summary>
    /// Nothing captured means Spotify is playing somewhere this session is not listening — a
    /// distinct outcome, because the fix is for the user to change the device, not to retry.
    /// </summary>
    [Fact]
    public async Task Record_WithNoAudioAtAll_ReportsSilentAndLeavesNothingBehind()
    {
        using var harness = new Harness();

        var recording = await harness.RecordAsync(bytes: 0);

        Assert.Equal(RecordingOutcome.Silent, recording.Outcome);
        Assert.Null(recording.Encode);
        Assert.Equal(TimeSpan.Zero, recording.Duration);
        Assert.DoesNotContain(harness.FileSystem.AllFiles, f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    /// <summary>
    /// A recording that is thrown away has nothing left to tag, so its lookup is stopped rather
    /// than left to finish. It used to run on for seconds against a track that had already ended
    /// — spending calls on a rate limit shared with the recording that replaced it, and reporting
    /// a missing tag on a file that was never written.
    /// </summary>
    [Theory]
    [InlineData(30, 500)]  // Discarded as too short.
    [InlineData(2, 0)]     // Discarded as silent.
    public async Task Record_Discarded_StopsTheMetadataLookup(int minimumSeconds, int bytes)
    {
        using var harness = new Harness(
            minimumSeconds: minimumSeconds,
            enrich: async (_, token) =>
            {
                // A lookup still chasing a provider when the recording is decided.
                await Task.Delay(Timeout.Infinite, token);
                return TrackEnrichment.None;
            });

        await harness.RecordAsync(bytes);

        Assert.True(harness.EnrichmentCancellation!.IsCancellationRequested);
    }

    /// <summary>
    /// The lookup often finishes before the recording is decided, and its cover art is a temp
    /// file that nothing else will ever reference once the recording is gone. Only the
    /// already-recorded branch used to delete one, so every discarded recording that had enriched
    /// in time left an image behind.
    /// </summary>
    [Fact]
    public async Task Record_ShorterThanTheMinimum_DeletesCoverArtTheLookupAlreadyFetched()
    {
        const string CoverArt = @"C:\art\cover.jpg";

        using var harness = new Harness(
            minimumSeconds: 30,
            enrich: (_, _) => Task.FromResult(new TrackEnrichment(Updated: true, CoverArt)));

        harness.FileSystem.Directory.CreateDirectory(@"C:\art");
        harness.FileSystem.File.WriteAllBytes(CoverArt, [1, 2, 3]);

        var recording = await harness.RecordAsync(bytes: 500);

        Assert.Equal(RecordingOutcome.TooShort, recording.Outcome);
        Assert.False(harness.FileSystem.File.Exists(CoverArt));
    }

    /// <summary>
    /// The other half of the rule: a recording that is kept still needs its art, and its lookup
    /// must not be cancelled on the way to the encode request.
    /// </summary>
    [Fact]
    public async Task Record_Captured_KeepsTheCoverArtAndTheLookup()
    {
        const string CoverArt = @"C:\art\cover.jpg";

        using var harness = new Harness(
            enrich: (_, _) => Task.FromResult(new TrackEnrichment(Updated: true, CoverArt)));

        harness.FileSystem.Directory.CreateDirectory(@"C:\art");
        harness.FileSystem.File.WriteAllBytes(CoverArt, [1, 2, 3]);

        var recording = await harness.RecordAsync(bytes: 500);

        Assert.Equal(RecordingOutcome.Captured, recording.Outcome);
        Assert.Equal(CoverArt, recording.Encode!.CoverArtPath);
        Assert.True(harness.FileSystem.File.Exists(CoverArt));
        Assert.False(harness.EnrichmentCancellation!.IsCancellationRequested);
    }

    /// <summary>
    /// Whatever is buffered when a track starts is the tail of the previous one; keeping it is
    /// how a recording opens with the last second of the song before it.
    /// </summary>
    [Fact]
    public async Task Record_DropsWhatWasBufferedBeforeTheTrackStarted()
    {
        using var harness = new Harness();

        harness.Buffer.Write(Audio(300));

        var recording = await harness.RecordAsync(bytes: 300);

        Assert.Equal(RecordingOutcome.Captured, recording.Outcome);
        Assert.Equal(3, recording.Duration.TotalSeconds, 1);
    }

    /// <summary>Audio arriving between the last chunk and the stop request still belongs to the file.</summary>
    [Fact]
    public async Task Record_DrainsTheTailAfterStopIsRequested()
    {
        using var harness = new Harness(minimumSeconds: 0);

        var running = harness.Recorder.RunAsync();

        // Less than a chunk, so only the final drain can pick it up.
        await Task.Delay(20);
        harness.Buffer.Write(Audio(40));
        harness.Recorder.Stop();

        var recording = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RecordingOutcome.Captured, recording.Outcome);
        Assert.Equal(0.4, recording.Duration.TotalSeconds, 1);
    }

    [Fact]
    public async Task Record_WhenTheSessionIsTornDown_DiscardsThePartialFile()
    {
        using var harness = new Harness(minimumSeconds: 0);
        using var cancellation = new CancellationTokenSource();

        var running = harness.Recorder.RunAsync(cancellation.Token);

        harness.Buffer.Write(Audio(200));
        await Task.Delay(20);
        await cancellation.CancelAsync();

        var recording = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RecordingOutcome.Cancelled, recording.Outcome);
        Assert.Null(recording.Encode);
        Assert.DoesNotContain(harness.FileSystem.AllFiles, f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Elapsed_TracksTheAudioWrittenRatherThanWallClock()
    {
        using var harness = new Harness(minimumSeconds: 0);

        var running = harness.Recorder.RunAsync();

        // Idle for far longer than the audio it is given: a clock-based count would say a
        // second or more, the bytes say a tenth of one.
        await Task.Delay(300);
        harness.Buffer.Write(Audio(10));
        harness.Recorder.Stop();

        var recording = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0.1, recording.Duration.TotalSeconds, 2);
    }

    /// <summary>
    /// The stop token is cancelled at the moment a track ends, which is exactly when the last
    /// chunk is in flight. A read has already taken that audio out of the ring buffer, so a
    /// write that honours the token drops it with nowhere to recover it from.
    /// </summary>
    [Fact]
    public async Task Stop_DoesNotDiscardTheChunkAlreadyInFlight()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var harness = new Harness(minimumSeconds: 0);

            var running = harness.Recorder.RunAsync();

            harness.Buffer.Write(Audio(100));
            harness.Recorder.Stop();

            var recording = await running.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(RecordingOutcome.Captured, recording.Outcome);
            Assert.Equal(1, recording.Duration.TotalSeconds, 2);
        }
    }

    [Fact]
    public async Task Stop_IsIdempotent()
    {
        using var harness = new Harness(minimumSeconds: 0);

        var running = harness.Recorder.RunAsync();

        harness.Buffer.Write(Audio(100));
        harness.Recorder.Stop();
        harness.Recorder.Stop();

        var recording = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RecordingOutcome.Captured, recording.Outcome);
        Assert.False(harness.Recorder.IsRecording);
    }
}
