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
        public Harness(int minimumSeconds = 2, Track? track = null)
        {
            FileSystem = new MockFileSystem();
            FileSystem.Directory.CreateDirectory(MusicRoot);

            Settings = TrackRecorderTests.Settings(minimumSeconds);
            Buffer = new AudioCaptureBuffer(TinyFormat());
            Track = track ?? SampleTrack();

            Paths = new OutputPaths(Settings, Track, FileSystem, new DateTime(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc));
            Recorder = new TrackRecorder(Buffer, Settings, Track, Paths, FileSystem);
        }

        public MockFileSystem FileSystem { get; }

        public RecordingSettings Settings { get; }

        public AudioCaptureBuffer Buffer { get; }

        public Track Track { get; }

        public OutputPaths Paths { get; }

        public TrackRecorder Recorder { get; }

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
