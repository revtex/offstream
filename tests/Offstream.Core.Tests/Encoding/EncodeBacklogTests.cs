using System.Collections.Concurrent;
using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Xunit;

namespace Offstream.Core.Tests.Encoding;

/// <summary>
/// The encode backlog (plan §10, Phase 3). Driven through a stub encoder, so the queue's own
/// behaviour — ordering, isolation of failures, shutdown — is asserted without ffmpeg.
/// </summary>
public sealed class EncodeBacklogTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static EncodeRequest Request(string name) =>
        new($@"C:\temp\{name}.wav", $@"C:\music\{name}.mp3", MediaFormat.Mp3, 320);

    /// <summary>An <see cref="IAudioEncoder"/> that records what it was asked to do.</summary>
    private sealed class StubEncoder : IAudioEncoder
    {
        private int _inFlight;

        public ConcurrentQueue<string> Encoded { get; } = new();

        /// <summary>Runs before each encode completes; the hook for gating and for failures.</summary>
        public Func<EncodeRequest, Task>? OnEncode { get; set; }

        /// <summary>The most simultaneous encodes ever observed. Must never exceed one.</summary>
        public int PeakConcurrency { get; private set; }

        public async Task<EncodeOutcome> EncodeAsync(
            EncodeRequest request, CancellationToken cancellationToken = default)
        {
            var running = Interlocked.Increment(ref _inFlight);
            PeakConcurrency = Math.Max(PeakConcurrency, running);

            try
            {
                if (OnEncode is not null) await OnEncode(request);

                Encoded.Enqueue(request.InputPath);
                return new EncodeOutcome(request);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>Polls a condition instead of sleeping a fixed interval, so the suite stays fast.</summary>
    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {because}.");
    }

    [Fact]
    public async Task Enqueue_EncodesInTheBackgroundAndRaisesCompleted()
    {
        var encoder = new StubEncoder();
        await using var queue = new EncodeBacklog(encoder);

        var completed = new ConcurrentQueue<EncodeOutcome>();
        queue.Completed += (_, e) => completed.Enqueue(e.Outcome);

        queue.Start();
        Assert.True(queue.Enqueue(Request("one")));

        await WaitFor(() => !completed.IsEmpty, "the encode to complete");

        Assert.True(completed.TryDequeue(out var outcome));
        Assert.Equal(@"C:\music\one.mp3", outcome!.OutputPath);
        Assert.False(outcome.HasWarning);
    }

    [Fact]
    public async Task Queue_EncodesOneAtATimeInOrder()
    {
        var encoder = new StubEncoder();
        await using var queue = new EncodeBacklog(encoder);

        queue.Start();

        foreach (var name in new[] { "first", "second", "third" }) queue.Enqueue(Request(name));

        await queue.CompleteAsync();

        Assert.Equal(
            [@"C:\temp\first.wav", @"C:\temp\second.wav", @"C:\temp\third.wav"],
            encoder.Encoded);

        Assert.Equal(1, encoder.PeakConcurrency);
    }

    /// <summary>
    /// One unencodable file — a full disk, a vanished temp WAV — must not end encoding for the
    /// rest of the session.
    /// </summary>
    [Fact]
    public async Task Queue_KeepsGoingAfterAFailure()
    {
        var encoder = new StubEncoder
        {
            OnEncode = request => request.InputPath.Contains("bad", StringComparison.Ordinal)
                ? throw new FFmpegException("ffmpeg fell over.", 1, "diagnostics")
                : Task.CompletedTask,
        };

        await using var queue = new EncodeBacklog(encoder);

        var failures = new ConcurrentQueue<EncodeFailedEventArgs>();
        queue.Failed += (_, e) => failures.Enqueue(e);

        queue.Start();
        queue.Enqueue(Request("bad"));
        queue.Enqueue(Request("good"));

        await queue.CompleteAsync();

        Assert.Equal([@"C:\temp\good.wav"], encoder.Encoded);
        Assert.True(failures.TryDequeue(out var failure));
        Assert.Equal(@"C:\temp\bad.wav", failure!.Request.InputPath);
        Assert.IsType<FFmpegException>(failure.Exception);
    }

    /// <summary>A UI handler blowing up is the subscriber's problem, not the encoder's.</summary>
    [Fact]
    public async Task Queue_SurvivesAThrowingSubscriber()
    {
        var encoder = new StubEncoder();
        await using var queue = new EncodeBacklog(encoder);

        queue.Completed += (_, _) => throw new InvalidOperationException("handler is broken");

        queue.Start();
        queue.Enqueue(Request("one"));
        queue.Enqueue(Request("two"));

        await queue.CompleteAsync();

        Assert.Equal(2, encoder.Encoded.Count);
    }

    /// <summary>
    /// The shutdown case that matters: closing the app with a backlog finishes it rather than
    /// discarding recordings the user believes they made.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_DrainsTheBacklogBeforeReturning()
    {
        var encoder = new StubEncoder { OnEncode = _ => Task.Delay(20) };
        await using var queue = new EncodeBacklog(encoder);

        queue.Start();

        for (var i = 0; i < 8; i++) queue.Enqueue(Request($"track-{i}"));

        await queue.CompleteAsync();

        Assert.Equal(8, encoder.Encoded.Count);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Enqueue_AfterCompletionIsRefused()
    {
        var encoder = new StubEncoder();
        await using var queue = new EncodeBacklog(encoder);

        queue.Start();
        await queue.CompleteAsync();

        Assert.False(queue.Enqueue(Request("late")));
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task PendingCount_TracksTheBacklog()
    {
        var gate = new TaskCompletionSource();
        var encoder = new StubEncoder { OnEncode = _ => gate.Task };

        await using var queue = new EncodeBacklog(encoder);

        queue.Start();
        queue.Enqueue(Request("one"));
        queue.Enqueue(Request("two"));

        Assert.Equal(2, queue.PendingCount);

        gate.SetResult();
        await queue.CompleteAsync();

        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Start_IsIdempotent()
    {
        var encoder = new StubEncoder();
        await using var queue = new EncodeBacklog(encoder);

        queue.Start();
        queue.Start();

        Assert.True(queue.IsRunning);

        queue.Enqueue(Request("one"));
        await queue.CompleteAsync();

        Assert.Single(encoder.Encoded);
        Assert.Equal(1, encoder.PeakConcurrency);
    }

    /// <summary>
    /// <c>await using</c> disposes on scope exit even when the queue was already shut down
    /// explicitly, so a second disposal must be a no-op rather than a cancelled-token throw.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var queue = new EncodeBacklog(new StubEncoder());

        queue.Start();
        await queue.DisposeAsync();
        await queue.DisposeAsync();

        Assert.False(queue.Enqueue(Request("late")));
    }

    [Fact]
    public async Task DisposeAsync_WithoutCompleting_AbandonsTheBacklogRatherThanHanging()
    {
        var gate = new TaskCompletionSource();
        var encoder = new StubEncoder { OnEncode = _ => gate.Task };
        var queue = new EncodeBacklog(encoder);

        queue.Start();
        queue.Enqueue(Request("stuck"));
        queue.Enqueue(Request("never-started"));

        await WaitFor(() => queue.PendingCount == 2, "both items to be queued");

        // Disposal cannot interrupt the encode already running — ffmpeg is a child process,
        // and killing it mid-write would leave a corrupt file — so it waits for that one and
        // starts nothing further.
        var disposal = queue.DisposeAsync().AsTask();
        gate.SetResult();

        await disposal.WaitAsync(Patience);

        Assert.DoesNotContain(encoder.Encoded, path => path.Contains("never-started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_AfterDisposalThrows()
    {
        var queue = new EncodeBacklog(new StubEncoder());

        await queue.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(queue.Start);
    }
}
