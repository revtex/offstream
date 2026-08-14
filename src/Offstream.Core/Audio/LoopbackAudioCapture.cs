using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace Offstream.Core.Audio;

/// <summary>Captured audio, valid only for the duration of the callback.</summary>
/// <remarks>
/// The buffer is reused by the capture driver between callbacks, so a handler must copy what it
/// needs — <see cref="AudioCaptureBuffer.Write"/> does — rather than hold the array.
/// </remarks>
public sealed class AudioDataEventArgs(byte[] buffer, int count) : EventArgs
{
    public byte[] Buffer { get; } = buffer;

    /// <summary>Valid bytes at the start of <see cref="Buffer"/>.</summary>
    public int Count { get; } = count;

    /// <summary>The valid portion, as a span.</summary>
    public ReadOnlySpan<byte> Data => Buffer.AsSpan(0, Count);
}

/// <summary>Capture ended, with the exception that ended it when it was not a clean stop.</summary>
public sealed class CaptureStoppedEventArgs(Exception? exception) : EventArgs
{
    public Exception? Exception { get; } = exception;
}

/// <summary>An audio source that pushes captured PCM at whoever is listening.</summary>
/// <remarks>
/// Exists so the recording pipeline can be driven by a stub in tests. Nothing above this
/// interface knows about WASAPI, and no test needs an audio endpoint.
/// </remarks>
public interface IAudioCaptureSource : IDisposable
{
    /// <summary>The format captured audio arrives in.</summary>
    WaveFormat Format { get; }

    /// <summary>Whether capture is currently running.</summary>
    bool IsCapturing { get; }

    /// <summary>Raised on the capture thread, roughly every 10 ms while audio flows.</summary>
    event EventHandler<AudioDataEventArgs>? DataAvailable;

    /// <summary>Raised once capture has stopped, cleanly or otherwise.</summary>
    event EventHandler<CaptureStoppedEventArgs>? Stopped;

    /// <summary>Begins capture. Idempotent.</summary>
    void StartCapture();

    /// <summary>Ends capture. Idempotent. Named for symmetry, and because CA1716 reserves "Stop".</summary>
    void StopCapture();
}

/// <summary>
/// WASAPI loopback capture of a render endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Loopback capture is what makes the whole app work without a virtual cable: it records what
/// an output device is playing. Phase 0 verified it still works unelevated on .NET 10
/// (DR-0001).
/// </para>
/// <para>
/// <b>The silence keep-alive is load-bearing, not decoration.</b> A render endpoint with
/// nothing playing produces no loopback data at all — the callback simply stops firing — so an
/// idle gap between tracks would vanish from the timeline rather than being recorded as
/// silence. Playing a silent stream into the endpoint keeps it active and the callbacks
/// regular. The reference implementation did this with a default-device <c>WaveOutEvent</c>,
/// which keeps the wrong endpoint alive whenever the user records something other than the
/// default; this targets the endpoint actually being captured.
/// </para>
/// </remarks>
public sealed class LoopbackAudioCapture : IAudioCaptureSource
{
    private readonly MMDevice _device;
    private readonly WasapiLoopbackCapture _capture;
    private readonly bool _keepEndpointAlive;
    private readonly string? _requestedDeviceId;
    private readonly IAudioEndpointWatcher? _endpoints;

    private WasapiOut? _keepAlive;
    private bool _disposed;
    private int _stopReported;

    /// <summary>Why the endpoint watcher ended the capture, for whichever path reports the stop.</summary>
    private volatile Exception? _endpointLoss;

    /// <param name="deviceId">Render endpoint to capture, or null for the default device.</param>
    /// <param name="keepEndpointAlive">
    /// Play silence into the endpoint so capture keeps delivering while nothing else plays.
    /// </param>
    /// <param name="endpoints">
    /// Watches for the endpoint disappearing. Injectable so the hot-plug path can be exercised
    /// without unplugging anything; the recording pipeline passes the real one.
    /// </param>
    public LoopbackAudioCapture(
        string? deviceId = null,
        bool keepEndpointAlive = true,
        IAudioEndpointWatcher? endpoints = null)
    {
        _device = AudioEndpoints.Resolve(deviceId);
        _keepEndpointAlive = keepEndpointAlive;
        _requestedDeviceId = deviceId;

        _capture = new WasapiLoopbackCapture(_device) { ShareMode = AudioClientShareMode.Shared };
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        _endpoints = endpoints;
        if (_endpoints is not null) _endpoints.Changed += OnEndpointChanged;
    }

    /// <summary>
    /// Ends the capture when the endpoint it was reading goes away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, losing the endpoint is silent: WASAPI simply stops delivering buffers, the
    /// recording continues believing it is capturing, and the file that lands is however many
    /// seconds of audio arrived before the headphones came out. Stopping deliberately means the
    /// recording is finished and saved rather than left running against a dead stream.
    /// </para>
    /// <para>
    /// <b>It stops rather than re-routes</b>, deliberately — see <see cref="AudioEndpointWatcher"/>.
    /// The replacement endpoint can have a different sample rate and channel count, so continuing
    /// into it would put a seam in the middle of the file or encode a tail that the header does
    /// not describe.
    /// </para>
    /// <para>
    /// The notification arrives on a system thread from inside the audio stack, so this must not
    /// block: <c>StopCapture</c> is what the ordinary stop path already calls, and the
    /// <see cref="Stopped"/> event carries the reason on to whoever is recording.
    /// </para>
    /// </remarks>
    private void OnEndpointChanged(object? sender, AudioEndpointChange change)
    {
        // Notifications are handed over on a pool thread, so one can be in flight while this is
        // being disposed — unsubscribing afterwards does not recall a change already handed over.
        // Both flags are set before disposal touches anything, and stopping a capture that has
        // already released its audio client is not an exception but an access violation.
        if (_disposed || !IsCapturing) return;
        if (!AudioEndpointRelevance.EndsTheCapture(_requestedDeviceId, change)) return;

        Log.Warning(
            "The audio endpoint being recorded ({Device}) is no longer available; stopping capture.",
            DeviceName);

        // Recorded before the stop is requested, so the reason is available to whichever path
        // reports it: stopping only asks the capture thread to finish, and that thread can raise
        // its own stop before this one gets there.
        _endpointLoss = new InvalidOperationException(
            $"The audio endpoint '{DeviceName}' became unavailable during recording.");

        StopCapture();

        RaiseStopped(_endpointLoss);
    }

    /// <inheritdoc />
    public WaveFormat Format => _capture.WaveFormat;

    /// <inheritdoc />
    public bool IsCapturing { get; private set; }

    /// <summary>The endpoint being captured, for the log and the UI.</summary>
    public string DeviceName => _device.FriendlyName;

    /// <inheritdoc />
    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    /// <inheritdoc />
    public event EventHandler<CaptureStoppedEventArgs>? Stopped;

    /// <inheritdoc />
    public void StartCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsCapturing) return;

        _endpointLoss = null;
        Interlocked.Exchange(ref _stopReported, 0);

        if (_keepEndpointAlive) StartKeepAlive();

        IsCapturing = true;
        _capture.StartRecording();
    }

    /// <inheritdoc />
    public void StopCapture()
    {
        if (!IsCapturing) return;

        IsCapturing = false;
        _capture.StopRecording();

        StopKeepAlive();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopCapture();

        // Not left to StopCapture: when the capture loop ends on its own — the endpoint pulled
        // out mid-recording, which is the case the watcher exists for — it has already cleared
        // IsCapturing, so StopCapture returns without reaching here and the keep-alive goes on
        // playing silence into a device nobody is recording. Stopping is idempotent.
        StopKeepAlive();

        if (_endpoints is not null)
        {
            _endpoints.Changed -= OnEndpointChanged;
            _endpoints.Dispose();
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
        _device.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        DataAvailable?.Invoke(this, new AudioDataEventArgs(e.Buffer, e.BytesRecorded));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsCapturing = false;
        RaiseStopped(e.Exception ?? _endpointLoss);
    }

    /// <summary>
    /// Reports the stop once, whichever path gets here first.
    /// </summary>
    /// <remarks>
    /// A lost endpoint is noticed twice over: the capture thread's read fails, and the watcher's
    /// notification arrives. Neither can be relied on alone — a notification can be seconds behind
    /// the hardware, and NAudio's capture thread stops the audio client outside its own catch, so
    /// an endpoint that fails that call takes the thread down without ever reporting the stop. Both
    /// are therefore left to report it, and this makes the second one a no-op instead of a second
    /// ending for whoever is recording.
    /// </remarks>
    private void RaiseStopped(Exception? reason)
    {
        if (Interlocked.Exchange(ref _stopReported, 1) != 0) return;

        Stopped?.Invoke(this, new CaptureStoppedEventArgs(reason));
    }

    private void StartKeepAlive()
    {
        try
        {
            _keepAlive = new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: false, latency: 100);
            _keepAlive.Init(new SilenceProvider(_capture.WaveFormat).ToSampleProvider());
            _keepAlive.Play();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
        {
            // Capture still works without it; only idle gaps are affected. An endpoint that
            // refuses a second client is not a reason to fail the recording session.
            _keepAlive?.Dispose();
            _keepAlive = null;
        }
    }

    private void StopKeepAlive()
    {
        if (_keepAlive is null) return;

        try
        {
            _keepAlive.Stop();
        }
        catch (COMException)
        {
            // The endpoint went away; nothing left to stop.
        }

        _keepAlive.Dispose();
        _keepAlive = null;
    }
}
