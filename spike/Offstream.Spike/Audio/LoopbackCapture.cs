using NAudio.CoreAudioApi;
using NAudio.Wave;
using Offstream.Core.Audio;

namespace Offstream.Spike.Audio;

internal sealed record CaptureResult(
    string Path, TimeSpan Duration, long Bytes, WaveFormat Format, bool AnyNonSilentSample);

/// <summary>
/// WASAPI loopback capture to a WAV file. The Phase 0 question this answers is whether
/// NAudio 2.x loopback still works on .NET 10 and actually delivers non-silent samples.
/// </summary>
internal static class LoopbackCapture
{
    public static async Task<CaptureResult> RecordAsync(
        string? deviceId, TimeSpan duration, string path, CancellationToken cancellationToken)
    {
        using var device = AudioEndpoints.Resolve(deviceId);
        using var capture = new WasapiLoopbackCapture(device);

        var format = capture.WaveFormat;
        await using var writer = new WaveFileWriter(path, format);

        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        long bytes = 0;
        var sawSignal = false;

        capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0) return;

            writer.Write(e.Buffer, 0, e.BytesRecorded);
            bytes += e.BytesRecorded;

            // Loopback returns silence as exact zero bytes, so a cheap scan distinguishes
            // "captured 30s of nothing" from "captured 30s of audio".
            if (!sawSignal)
            {
                for (var i = 0; i < e.BytesRecorded; i++)
                {
                    if (e.Buffer[i] == 0) continue;
                    sawSignal = true;
                    break;
                }
            }
        };

        capture.RecordingStopped += (_, e) => completed.TrySetResult(e.Exception);

        var started = DateTimeOffset.UtcNow;
        capture.StartRecording();

        try
        {
            await Task.Delay(duration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Fall through and stop cleanly so the WAV header still gets written.
        }

        capture.StopRecording();

        var error = await completed.Task;
        if (error is not null) throw error;

        await writer.FlushAsync(CancellationToken.None);

        return new CaptureResult(
            path, DateTimeOffset.UtcNow - started, bytes, format, sawSignal);
    }
}
