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
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _pollLoop;
    private Task? _songLoop;
    private bool _playing;
    private bool _disposed;

    /// <summary>Time played on the current track, up to the last pause.</summary>
    private TimeSpan _played;

    /// <summary>When the current unpaused stretch began, or null while paused.</summary>
    private long? _playingSince;

    public SpotifyPoller(ITrackSource trackSource, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(trackSource);

        _trackSource = trackSource;
        _time = timeProvider ?? TimeProvider.System;
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
        RestartClock();
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

                // The counter measures time played, so a pause has to stop the clock rather than
                // merely stop being sampled — otherwise it resumes having counted the silence.
                if (_playing) ResumeClock();
                else PauseClock();

                PlayStateChanged?.Invoke(this, new PlayStateChangedEventArgs(newest.Playing));
            }

            var isSameTrack = newest.Equals(previous);

            if (!isSameTrack)
            {
                _playing = newest.Playing;
                RestartClock();
                TrackChanged?.Invoke(this, new TrackChangedEventArgs(previous, newest));
            }

            TrackTimeChanged?.Invoke(
                this, new TrackTimeChangedEventArgs(isSameTrack ? (int)PlayedSoFar().TotalSeconds : 0));

            // A continuing track keeps its elapsed count; a new one starts unmeasured.
            newest.CurrentPosition = isSameTrack ? (int)PlayedSoFar().TotalSeconds : null;
        }
        else
        {
            _playing = newest.Playing;
            RestartClock();
        }

        CurrentTrack = newest;
    }

    /// <summary>
    /// Republishes the elapsed-time counter from the clock. Exposed for tests.
    /// </summary>
    /// <remarks>
    /// <b>It samples a clock; it does not count ticks.</b> This used to do
    /// <c>CurrentPosition += 1</c> on a one-second <see cref="PeriodicTimer"/>, which makes the
    /// counter a tally of ticks that were observed rather than of time that passed —
    /// <see cref="PeriodicTimer"/> schedules the next tick from when the previous one was
    /// consumed and never makes up a late one. Every delayed tick therefore lost a second
    /// permanently, so the counter drifted monotonically behind Spotify's own position and could
    /// never catch up: it looked like the timer paused whenever the app was busy or in the
    /// background. Reading a monotonic clock instead means a late tick reports the truth and the
    /// drift corrects itself.
    /// </remarks>
    public void AdvanceElapsed()
    {
        if (CurrentTrack is null || !_playing) return;

        CurrentTrack.CurrentPosition = (int)PlayedSoFar().TotalSeconds;
    }

    /// <summary>Time the current track has actually been playing, pauses excluded.</summary>
    private TimeSpan PlayedSoFar() =>
        _playingSince is { } since ? _played + _time.GetElapsedTime(since) : _played;

    /// <summary>Starts the clock for a track, or resumes it after a pause.</summary>
    private void ResumeClock() => _playingSince ??= _time.GetTimestamp();

    /// <summary>Banks the current stretch and stops the clock.</summary>
    private void PauseClock()
    {
        if (_playingSince is not { } since) return;

        _played += _time.GetElapsedTime(since);
        _playingSince = null;
    }

    /// <summary>Puts the clock back to zero, for a track that has just started.</summary>
    private void RestartClock()
    {
        _played = TimeSpan.Zero;
        _playingSince = _playing ? _time.GetTimestamp() : null;
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
