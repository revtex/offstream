using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Serilog;

namespace Offstream.Core.Audio;

/// <summary>What happened to a render endpoint.</summary>
public enum AudioEndpointChangeKind
{
    /// <summary>A device appeared, or came back from disabled/unplugged.</summary>
    Available,

    /// <summary>A device was removed, disabled or unplugged.</summary>
    Unavailable,

    /// <summary>Windows changed which device is the default.</summary>
    DefaultChanged,
}

/// <param name="Kind">What happened.</param>
/// <param name="DeviceId">The endpoint it happened to, or the new default for a default change.</param>
public readonly record struct AudioEndpointChange(AudioEndpointChangeKind Kind, string? DeviceId);

/// <summary>Reports render endpoints appearing and disappearing.</summary>
public interface IAudioEndpointWatcher : IDisposable
{
    event EventHandler<AudioEndpointChange>? Changed;
}

/// <summary>
/// Decides whether an endpoint change matters to a capture already in progress.
/// </summary>
/// <remarks>
/// Separated from the notification plumbing because it is the part with rules in it, and the
/// plumbing needs real audio hardware to exercise while this needs none.
/// </remarks>
public static class AudioEndpointRelevance
{
    /// <summary>
    /// Whether <paramref name="change"/> takes away the endpoint a capture is reading.
    /// </summary>
    /// <param name="capturedDeviceId">
    /// The endpoint being captured, or null when the capture followed the system default.
    /// </param>
    /// <remarks>
    /// <para>
    /// Two cases lose the audio, and they look different in the notifications. An explicitly
    /// chosen device is lost when that exact id goes away. A capture that followed the default is
    /// lost when the default <em>changes</em> — the old device may still exist and still be
    /// enumerable, but Windows has moved playback elsewhere and the stream being read has gone
    /// quiet, which is the more confusing failure of the two because nothing was unplugged.
    /// </para>
    /// <para>
    /// A device merely appearing never matters mid-capture. Plugging headphones in does not move
    /// the audio Offstream is already reading.
    /// </para>
    /// </remarks>
    public static bool EndsTheCapture(string? capturedDeviceId, AudioEndpointChange change) =>
        string.IsNullOrEmpty(capturedDeviceId)
            ? change.Kind == AudioEndpointChangeKind.DefaultChanged
            : change.Kind == AudioEndpointChangeKind.Unavailable
                && string.Equals(change.DeviceId, capturedDeviceId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Watches Windows' audio endpoints and reports arrivals and departures.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reports; it does not re-route.</b> Automatically moving a running capture to another
/// endpoint sounds like the helpful thing and is not: the new device can have a different sample
/// rate and channel count, so the recording in progress would gain a seam mid-file, or a tail
/// encoded from a format its header does not describe. Losing the endpoint ends that recording
/// cleanly and says why. Choosing a different device is the user's call, and the next recording
/// picks it up.
/// </para>
/// <para>
/// <b>Callbacks arrive on a system thread</b>, from inside the audio stack, so nothing here does
/// real work: it raises an event and returns. Blocking one of these is a documented way to wedge
/// the endpoint enumerator for the whole process.
/// </para>
/// </remarks>
public sealed class AudioEndpointWatcher : IAudioEndpointWatcher, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator;
    private bool _disposed;

    public AudioEndpointWatcher()
    {
        _enumerator = new MMDeviceEnumerator();
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    /// <inheritdoc />
    public event EventHandler<AudioEndpointChange>? Changed;

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) =>
        Raise(
            newState == DeviceState.Active
                ? AudioEndpointChangeKind.Available
                : AudioEndpointChangeKind.Unavailable,
            deviceId);

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) =>
        Raise(AudioEndpointChangeKind.Available, pwstrDeviceId);

    void IMMNotificationClient.OnDeviceRemoved(string deviceId) =>
        Raise(AudioEndpointChangeKind.Unavailable, deviceId);

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // Render only, and one role only. Windows raises this per role, so listening to all three
        // turns a single headphone swap into three identical notifications.
        if (flow != DataFlow.Render || role != Role.Multimedia) return;

        Raise(AudioEndpointChangeKind.DefaultChanged, defaultDeviceId);
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Volume, icon, friendly name. None of it changes whether audio can be captured.
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
        }
#pragma warning disable CA1031 // Unregistering a callback must never fail a session teardown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Debug(ex, "Unregistering the audio endpoint callback failed.");
        }

        _enumerator.Dispose();
    }

    private void Raise(AudioEndpointChangeKind kind, string? deviceId) =>
        Changed?.Invoke(this, new AudioEndpointChange(kind, deviceId));
}
