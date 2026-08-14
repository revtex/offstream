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
    /// <summary>
    /// Every watcher Windows can still call back, kept alive for as long as that is true.
    /// </summary>
    /// <remarks>
    /// A registered <see cref="IMMNotificationClient"/> is reached from the audio service through
    /// a COM wrapper around this object, and that registration stands until it is explicitly
    /// unregistered — the garbage collector does not know about it and cannot end it. Collecting a
    /// watcher that is still registered therefore leaves the audio service calling into freed
    /// interop memory the next time an endpoint changes, which ends the process with an access
    /// violation rather than an exception: nothing catchable, nothing logged. Holding a reference
    /// here until <see cref="Dispose"/> unregisters means a watcher nobody disposed costs a few
    /// bytes instead of the app.
    /// </remarks>
    private static readonly HashSet<AudioEndpointWatcher> Registered = [];

    private static readonly Lock RegisteredGate = new();

    private readonly MMDeviceEnumerator _enumerator;
    private bool _disposed;

    public AudioEndpointWatcher()
    {
        _enumerator = new MMDeviceEnumerator();
        _enumerator.RegisterEndpointNotificationCallback(this);

        lock (RegisteredGate) Registered.Add(this);
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

        // After the unregister, never before: until it returns, a notification can still arrive.
        lock (RegisteredGate) Registered.Remove(this);

        _enumerator.Dispose();
    }

    /// <summary>
    /// Hands the change to a pool thread and returns, rather than running handlers here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This runs on the audio service's notification thread, which
    /// <see cref="IMMNotificationClient"/> forbids three things on: blocking, waiting on a
    /// synchronisation object, and releasing the last reference to an audio object. The one
    /// handler that exists does all three — ending a capture joins the keep-alive's render thread
    /// and closes its audio client — and it does them against the endpoint whose disappearance
    /// caused the notification, while the audio service holds the lock it called out under. The
    /// note above about not blocking was here from the start; what was missing is that the
    /// handler, not this method, is where the blocking happens.
    /// </para>
    /// <para>
    /// Ordering between notifications is not preserved, and does not need to be: a handler decides
    /// from the endpoint id in the change, not from the sequence it arrived in.
    /// </para>
    /// </remarks>
    private void Raise(AudioEndpointChangeKind kind, string? deviceId)
    {
        var handler = Changed;
        if (handler is null) return;

        var change = new AudioEndpointChange(kind, deviceId);

        ThreadPool.QueueUserWorkItem(_ => handler(this, change));
    }
}
