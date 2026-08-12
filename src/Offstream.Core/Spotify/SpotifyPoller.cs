using Offstream.Core.Metadata;

namespace Offstream.Core.Spotify;

/// <summary>Reads the current track. Abstracted so the poller is testable without a process.</summary>
public interface ITrackSource
{
    Task<Track?> GetCurrentTrackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Watches Spotify and raises events when the track, play state or elapsed time changes.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the reference implementation's <c>SpotifyHandler</c>, keeping its event
/// semantics. The mechanics are modernised, because the originals were the kind that fail
/// only in production:
/// </para>
/// <list type="bullet">
///   <item>
///     <c>System.Timers.Timer</c> with an <c>async void</c> <c>Elapsed</c> handler is replaced
///     by <see cref="PeriodicTimer"/> loops. An exception escaping an <c>async void</c>
///     handler crashes the process with no stack worth reading.
///   </item>
///   <item>
///     Events were raised through fire-and-forget <c>Task.Run</c>, so a throwing subscriber
///     was silently swallowed and handlers could interleave. They are now raised inline on
///     the polling loop, in order.
///   </item>
///   <item>
///     A <c>bool _processingEvents</c> guard against re-entrancy becomes a single loop that
///     awaits each poll, so overlap is impossible by construction rather than by luck.
///   </item>
/// </list>
/// </remarks>
public sealed class SpotifyPoller : IAsyncDisposable
{
    /// <summary>How often to re-read the window title.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(70);

    /// <summary>How often the elapsed-time counter advances while playing.</summary>
    public static readonly TimeSpan SongTickInterval = TimeSpan.FromSeconds(1);

    private readonly ITrackSource _trackSource;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _pollLoop;
    private Task? _songLoop;
    private bool _playing;
    private bool _disposed;

    public SpotifyPoller(ITrackSource trackSource)
    {
        ArgumentNullException.ThrowIfNull(trackSource);
        _trackSource = trackSource;
    }

    /// <summary>The most recently observed track.</summary>
    public Track? CurrentTrack { get; private set; }

    public event EventHandler<TrackChangedEventArgs>? TrackChanged;
    public event EventHandler<PlayStateChangedEventArgs>? PlayStateChanged;
    public event EventHandler<TrackTimeChangedEventArgs>? TrackTimeChanged;

    /// <summary>Whether the background polling loop is running.</summary>
    public bool IsListening => _pollLoop is not null;

    /// <summary>Starts polling. Idempotent.</summary>
    /// <remarks>
    /// <b>Starting seeds an empty track, and that is load-bearing.</b> With no previous track at
    /// all, the first observation is treated as "nothing changed" and raises nothing — so the
    /// track already playing when a session starts would never be recorded, only the one after
    /// it. The reference implementation set <c>Track = new Track()</c> when it began listening
    /// for exactly this reason. A poller that has never been started still reports no previous
    /// track, because a bare <see cref="PollOnceAsync"/> has nothing to compare against.
    /// </remarks>
    public void Start()
    {
        if (_pollLoop is not null) return;

        CurrentTrack = new Track();
        _pollLoop = RunPollLoopAsync(_stopping.Token);
        _songLoop = RunSongLoopAsync(_stopping.Token);
    }

    /// <summary>
    /// Reads Spotify once and raises whatever changed. Exposed so the state machine can be
    /// tested a poll at a time instead of against wall-clock timers.
    /// </summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        var newest = await _trackSource.GetCurrentTrackAsync(cancellationToken);
        if (newest is null) return;

        var previous = CurrentTrack;

        if (previous is not null)
        {
            if (newest.Playing != previous.Playing)
            {
                _playing = newest.Playing;
                PlayStateChanged?.Invoke(this, new PlayStateChangedEventArgs(newest.Playing));
            }

            var isSameTrack = newest.Equals(previous);

            if (!isSameTrack)
            {
                _playing = newest.Playing;
                TrackChanged?.Invoke(this, new TrackChangedEventArgs(previous, newest));
            }

            TrackTimeChanged?.Invoke(
                this, new TrackTimeChangedEventArgs(isSameTrack ? previous.CurrentPosition ?? 0 : 0));

            // A continuing track keeps its elapsed count; a new one starts unmeasured.
            newest.CurrentPosition = isSameTrack ? previous.CurrentPosition ?? 0 : null;
        }
        else
        {
            _playing = newest.Playing;
        }

        CurrentTrack = newest;
    }

    /// <summary>Advances the elapsed-time counter by one tick. Exposed for tests.</summary>
    public void AdvanceElapsed()
    {
        if (CurrentTrack is null || !_playing) return;

        CurrentTrack.CurrentPosition = (CurrentTrack.CurrentPosition ?? 0) + 1;
    }

    private async Task RunPollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await PollOnceAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task RunSongLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SongTickInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                AdvanceElapsed();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Must be idempotent: `await using` disposes on scope exit even when something has
        // already stopped the poller explicitly.
        if (_disposed) return;
        _disposed = true;

        await _stopping.CancelAsync();

        foreach (var loop in new[] { _pollLoop, _songLoop })
        {
            if (loop is null) continue;

            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _pollLoop = null;
        _songLoop = null;
        _stopping.Dispose();
    }
}
