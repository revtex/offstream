using Offstream.Core;

namespace Offstream.App.Services;

/// <summary>
/// Holds the single-instance claim, and carries "show yourself" from a second launch to the
/// instance already running.
/// </summary>
/// <remarks>
/// <para>
/// Two Offstreams recording the same audio session write two files from one stream and fight
/// over the same settings file, so the second launch has to stand down. Standing down silently
/// is the wrong half of the job: the user double-clicked the icon because they wanted the
/// window, and when the first instance is minimised to the tray there is nothing on screen to
/// tell them it is already running. So the second process signals the first and exits, and the
/// first surfaces.
/// </para>
/// <para>
/// The claim is a named <see cref="Mutex"/> and the signal is a named
/// <see cref="EventWaitHandle"/>, both per logon session — see
/// <see cref="OffstreamPaths.InstanceMutex"/> for why they are not global.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _claim;
    private readonly EventWaitHandle _activation;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex claim, EventWaitHandle activation, Action onActivationRequested)
    {
        _claim = claim;
        _activation = activation;

        // The thread pool owns the wait, so no dedicated thread sits blocked for the life of
        // the process. The callback arrives on a pool thread: everything it touches has to
        // marshal to the UI, which is the caller's job and is documented on TryAcquire.
        _registration = ThreadPool.RegisterWaitForSingleObject(
            activation,
            (_, _) => onActivationRequested(),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>
    /// Claims this session for the calling process, or asks the running instance to surface.
    /// </summary>
    /// <param name="onActivationRequested">
    /// Invoked <b>on a thread-pool thread</b> whenever a later launch asks for the window.
    /// Marshal to the dispatcher before touching anything in the UI.
    /// </param>
    /// <param name="name">
    /// Base name for the kernel objects; defaults to <see cref="OffstreamPaths.InstanceMutex"/>.
    /// Tests pass their own so that a run does not collide with a real Offstream on the same
    /// desktop, or with the next test.
    /// </param>
    /// <returns>
    /// The claim when this process is the only instance — keep it alive for the life of the
    /// app — or <see langword="null"/> when another instance holds it and has been signalled.
    /// </returns>
    public static SingleInstance? TryAcquire(Action onActivationRequested, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(onActivationRequested);

        // Not a default parameter value: the name depends on where the data directory resolved
        // to, which is not a compile-time constant.
        name ??= OffstreamPaths.InstanceMutex;

        // initiallyOwned + createdNew answers "am I first?" without a WaitOne, which also
        // sidesteps AbandonedMutexException: a previous instance that crashed releases the
        // mutex, and the next launch simply creates it again.
        var claim = new Mutex(initiallyOwned: true, name, out var createdNew);

        if (!createdNew)
        {
            claim.Dispose();
            SignalRunningInstance(ActivationName(name));

            return null;
        }

        return new SingleInstance(
            claim,
            new EventWaitHandle(false, EventResetMode.AutoReset, ActivationName(name)),
            onActivationRequested);
    }

    private static string ActivationName(string name) => name + ".Activate";

    /// <summary>
    /// Asks the running instance to show its window, if it is listening yet.
    /// </summary>
    /// <remarks>
    /// Best effort by design. Between a first instance creating its mutex and creating its
    /// event there is a window in which this finds nothing, and two launches close enough
    /// together to hit it are a user double-clicking twice — for whom the right outcome is
    /// still one window, which is what happens anyway.
    /// </remarks>
    private static void SignalRunningInstance(string activationName)
    {
        if (EventWaitHandle.TryOpenExisting(activationName, out var running))
        {
            using (running)
            {
                running.Set();
            }
        }
    }

    public void Dispose()
    {
        // Unregister first, then close what the wait was watching: tearing down in the other
        // order can hand the pool a handle that has already gone.
        _registration?.Unregister(waitObject: null);

        _activation.Dispose();

        _claim.ReleaseMutex();
        _claim.Dispose();
    }
}
