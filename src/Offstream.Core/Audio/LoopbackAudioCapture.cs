using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

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

    private WasapiOut? _keepAlive;
    private bool _disposed;

    /// <param name="deviceId">Render endpoint to capture, or null for the default device.</param>
    /// <param name="keepEndpointAlive">
    /// Play silence into the endpoint so capture keeps delivering while nothing else plays.
    /// </param>
    public LoopbackAudioCapture(string? deviceId = null, bool keepEndpointAlive = true)
    {
        _device = AudioEndpoints.Resolve(deviceId);
        _keepEndpointAlive = keepEndpointAlive;

        _capture = new WasapiLoopbackCapture(_device) { ShareMode = AudioClientShareMode.Shared };
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
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
        Stopped?.Invoke(this, new CaptureStoppedEventArgs(e.Exception));
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
