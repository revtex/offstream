using System.Net;
using Moq;
using Offstream.Core.Spotify;
using SpotifyAPI.Web.Http;
using Xunit;

namespace Offstream.Core.Tests.Spotify;

/// <summary>
/// The rate-limit policy, asserted on the schedule it produces rather than by spending it: the
/// handler takes its delay function as a dependency, so every wait here is recorded and none is
/// actually slept.
/// </summary>
public sealed class SpotifyRetryHandlerTests
{
    private sealed class Harness
    {
        private readonly Queue<IResponse> _answers = new();

        /// <summary>Every interval the handler asked to wait, in order.</summary>
        public List<TimeSpan> Waits { get; } = [];

        /// <summary>How many times the request was actually re-sent.</summary>
        public int Retries { get; private set; }

        public SpotifyRetryHandler Handler { get; private set; } = null!;

        public Harness Answering(params IResponse[] answers)
        {
            foreach (var answer in answers) _answers.Enqueue(answer);
            return this;
        }

        public Harness With(int? maximumRetries = null, TimeSpan? maximumDelay = null)
        {
            Handler = new SpotifyRetryHandler((interval, _) =>
            {
                Waits.Add(interval);
                return Task.CompletedTask;
            })
            {
                MaximumRetries = maximumRetries ?? SpotifyRetryHandler.DefaultMaximumRetries,
                MaximumDelay = maximumDelay ?? SpotifyRetryHandler.DefaultMaximumDelay,
            };

            return this;
        }

        public Task<IResponse> RunAsync(IResponse first) =>
            Handler.HandleRetry(
                Mock.Of<IRequest>(),
                first,
                (_, _) =>
                {
                    Retries++;
                    return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : Ok());
                });
    }

    private static IResponse Response(HttpStatusCode status, params (string Name, string Value)[] headers)
    {
        var response = new Mock<IResponse>();

        response.SetupGet(x => x.StatusCode).Returns(status);
        response
            .SetupGet(x => x.Headers)
            .Returns(headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase));

        return response.Object;
    }

    private static IResponse Ok() => Response(HttpStatusCode.OK);

    private static IResponse RateLimited(int? retryAfterSeconds = null) =>
        retryAfterSeconds is { } seconds
            ? Response(HttpStatusCode.TooManyRequests, ("Retry-After", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            : Response(HttpStatusCode.TooManyRequests);

    /// <summary>
    /// The rule the whole class exists for: Spotify says how long to wait and that number is
    /// obeyed, not shortened to something more convenient.
    /// </summary>
    [Fact]
    public async Task RateLimited_WaitsExactlyTheRetryAfterHeader()
    {
        var harness = new Harness().Answering(Ok()).With();

        var result = await harness.RunAsync(RateLimited(retryAfterSeconds: 7));

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal([TimeSpan.FromSeconds(7)], harness.Waits);
        Assert.Equal(1, harness.Retries);
    }

    /// <summary>Header casing is not ours to rely on — HTTP says it is insensitive.</summary>
    [Theory]
    [InlineData("Retry-After")]
    [InlineData("retry-after")]
    [InlineData("RETRY-AFTER")]
    public async Task RateLimited_FindsTheHeaderWhateverItsCasing(string header)
    {
        var harness = new Harness().Answering(Ok()).With();

        await harness.RunAsync(Response(HttpStatusCode.TooManyRequests, (header, "3")));

        Assert.Equal([TimeSpan.FromSeconds(3)], harness.Waits);
    }

    /// <summary>
    /// A 429 with no usable instruction still must not turn into a tight loop, so it falls back to
    /// the same doubling the server faults get.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    public async Task RateLimited_WithoutAUsableHeader_BacksOffExponentially(string? headerValue)
    {
        var first = headerValue is null
            ? RateLimited()
            : Response(HttpStatusCode.TooManyRequests, ("Retry-After", headerValue));

        var harness = new Harness()
            .Answering(RateLimited(), RateLimited(), RateLimited(), RateLimited())
            .With();

        await harness.RunAsync(first);

        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)],
            harness.Waits);
    }

    /// <summary>Transient server faults double; the point is not to pile onto a struggling backend.</summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task TransientFault_BacksOffExponentially(HttpStatusCode status)
    {
        var harness = new Harness().Answering(Response(status), Ok()).With();

        await harness.RunAsync(Response(status));

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], harness.Waits);
    }

    /// <summary>
    /// Everything else is the caller's fault and repeating it changes nothing — a 404 asked five
    /// times is still a 404, and four needless round trips.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task OtherStatuses_AreReturnedUntouched(HttpStatusCode status)
    {
        var harness = new Harness().With();

        var result = await harness.RunAsync(Response(status));

        Assert.Equal(status, result.StatusCode);
        Assert.Empty(harness.Waits);
        Assert.Equal(0, harness.Retries);
    }

    /// <summary>A throttle that never lifts gives up and hands back the last answer.</summary>
    [Fact]
    public async Task RateLimited_Forever_StopsAtTheRetryLimit()
    {
        var harness = new Harness()
            .Answering(RateLimited(1), RateLimited(1), RateLimited(1), RateLimited(1))
            .With(maximumRetries: 3);

        var result = await harness.RunAsync(RateLimited(1));

        Assert.Equal(HttpStatusCode.TooManyRequests, result.StatusCode);
        Assert.Equal(3, harness.Retries);
        Assert.Equal(3, harness.Waits.Count);
    }

    /// <summary>
    /// A <c>Retry-After</c> of an hour is capped. The enrichment deadline cancels long before the
    /// ceiling matters, but an uncapped wait would park a task for that hour regardless.
    /// </summary>
    [Fact]
    public async Task AnAbsurdRetryAfter_IsCapped()
    {
        var harness = new Harness().Answering(Ok()).With(maximumDelay: TimeSpan.FromSeconds(30));

        await harness.RunAsync(RateLimited(retryAfterSeconds: 3600));

        Assert.Equal([TimeSpan.FromSeconds(30)], harness.Waits);
    }

    /// <summary>Backoff is capped too, so a long retry budget cannot double its way to hours.</summary>
    [Fact]
    public async Task Backoff_IsCapped()
    {
        var harness = new Harness()
            .Answering(RateLimited(), RateLimited(), RateLimited(), RateLimited(), RateLimited())
            .With(maximumRetries: 5, maximumDelay: TimeSpan.FromSeconds(3));

        await harness.RunAsync(RateLimited());

        Assert.Equal(
            [
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(3),
            ],
            harness.Waits);
    }

    /// <summary>The wait is cancellable: a stopping session must not be held by a throttle.</summary>
    [Fact]
    public async Task ACancelledWait_PropagatesRatherThanRetrying()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var handler = new SpotifyRetryHandler((interval, token) => Task.Delay(interval, token));
        var retried = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleRetry(
            Mock.Of<IRequest>(),
            RateLimited(retryAfterSeconds: 30),
            (_, _) =>
            {
                retried = true;
                return Task.FromResult(Ok());
            },
            cancellation.Token));

        Assert.False(retried);
    }
}
