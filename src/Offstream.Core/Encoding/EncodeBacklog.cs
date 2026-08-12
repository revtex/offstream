using System.Threading.Channels;

namespace Offstream.Core.Encoding;

/// <summary>An encode finished. Carries the outcome, including any cover-art warning.</summary>
public sealed class EncodeCompletedEventArgs(EncodeOutcome outcome) : EventArgs
{
    public EncodeOutcome Outcome { get; } = outcome;
}

/// <summary>An encode failed. The captured WAV is still on disk; the output may not exist.</summary>
public sealed class EncodeFailedEventArgs(EncodeRequest request, Exception exception) : EventArgs
{
    public EncodeRequest Request { get; } = request;

    public Exception Exception { get; } = exception;
}

/// <summary>
/// The backlog of finished recordings waiting to be encoded: a single-consumer background
/// queue, drained one file at a time.
/// </summary>
/// <remarks>
/// <para>
/// Capture must never wait on encoding. A track ends, its WAV is handed over here, and the next
/// track starts recording immediately; the encode of the previous one runs behind it. The
/// reference implementation encoded on the recording thread, which is why a slow lossless
/// encode could clip the start of the following track.
/// </para>
/// <para>
/// <b>Unbounded, and one at a time.</b> Unbounded because refusing a finished recording is
/// worse than a long backlog — each item is a file already on disk, and the natural arrival
/// rate is one per song. One consumer because ffmpeg already uses every core it can, so
/// parallel encodes would trade throughput for contention with the capture that is still
/// running.
/// </para>
/// <para>
/// Failures are events, not exceptions: nothing is left holding a <c>Task</c> to observe, and
/// one unencodable file must not take the queue down with it. Handlers are invoked inline on
/// the drain loop, in completion order, for the reasons in <c>SpotifyPoller</c> — a throwing
/// subscriber is caught here so it cannot stop the drain either.
/// </para>
/// </remarks>
public sealed class EncodeBacklog(IAudioEncoder encoder) : IAsyncDisposable
{
    private readonly Channel<EncodeRequest> _channel = Channel.CreateUnbounded<EncodeRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();

    private Task? _drainLoop;
    private int _pending;
    private bool _disposed;

    /// <summary>An encode finished, successfully. Raised on the drain loop.</summary>
    public event EventHandler<EncodeCompletedEventArgs>? Completed;

    /// <summary>An encode failed. Raised on the drain loop; the queue keeps going.</summary>
    public event EventHandler<EncodeFailedEventArgs>? Failed;

    /// <summary>
    /// Items queued but not yet finished, including the one in flight. Meaningful while the
    /// queue is running; after a cancelling <see cref="DisposeAsync"/> it counts what was
    /// abandoned rather than what is still coming.
    /// </summary>
    public int PendingCount => Volatile.Read(ref _pending);

    /// <summary>Whether the drain loop is running.</summary>
    public bool IsRunning => _drainLoop is not null;

    /// <summary>Starts the drain loop. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _drainLoop ??= Task.Run(() => DrainAsync(_stopping.Token), CancellationToken.None);
        }
    }

    /// <summary>Queues a recording for encoding and returns immediately.</summary>
    /// <returns><see langword="false"/> once the queue has been completed or disposed.</returns>
    public bool Enqueue(EncodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Interlocked.Increment(ref _pending);

        if (_channel.Writer.TryWrite(request)) return true;

        Interlocked.Decrement(ref _pending);
        return false;
    }

    /// <summary>
    /// Stops accepting work and waits for the backlog to finish encoding.
    /// </summary>
    /// <remarks>
    /// This is the shutdown path that matters: closing the app with three tracks still queued
    /// should finish them, not discard them. <see cref="DisposeAsync"/> without this cancels
    /// instead, for the case where the user is not waiting.
    /// </remarks>
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        _channel.Writer.TryComplete();

        if (_drainLoop is null) return;

        await _drainLoop.WaitAsync(cancellationToken);
    }

    /// <summary>Encodes one item. Exposed so the queue's behaviour is testable a step at a time.</summary>
    internal async Task<bool> ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        if (!_channel.Reader.TryRead(out var request)) return false;

        await RunAsync(request, cancellationToken);
        return true;
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                // ReadAllAsync hands over items already sitting in the buffer without
                // re-checking the token, so cancellation has to be tested here too — otherwise
                // a cancelling shutdown still starts every encode that was already queued.
                if (cancellationToken.IsCancellationRequested) break;

                await RunAsync(request, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down without draining; whatever is left stays on disk as a WAV.
        }
    }

    private async Task RunAsync(EncodeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await encoder.EncodeAsync(request, cancellationToken);
            Raise(Completed, new EncodeCompletedEventArgs(outcome));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately broad: this is the boundary that keeps one bad file — a full disk,
            // a vanished temp WAV, a wedged ffmpeg — from ending the backlog for the whole session.
            Raise(Failed, new EncodeFailedEventArgs(request, ex));
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    /// <summary>Raises an event, absorbing a throwing subscriber so the drain loop survives it.</summary>
    private void Raise<T>(EventHandler<T>? handler, T args) where T : EventArgs
    {
        if (handler is null) return;

        try
        {
            handler(this, args);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A UI handler failing is not a reason to stop encoding.
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _channel.Writer.TryComplete();
        await _stopping.CancelAsync();

        if (_drainLoop is not null)
        {
            try
            {
                await _drainLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            _drainLoop = null;
        }

        _stopping.Dispose();
    }
}
