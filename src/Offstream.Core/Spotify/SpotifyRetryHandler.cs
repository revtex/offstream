using System.Globalization;
using System.Net;
using Serilog;
using SpotifyAPI.Web.Http;

namespace Offstream.Core.Spotify;

/// <summary>
/// Retries a Spotify request that was rate-limited or met a transient server fault, waiting the
/// interval Spotify asks for rather than one of our choosing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The SDK ships no retry policy by default.</b> <c>SpotifyClientConfig.CreateDefault()</c>
/// leaves <c>RetryHandler</c> null, so before this existed a 429 surfaced as an exception, fell
/// through <see cref="Offstream.Core.Metadata.TrackEnricher"/>'s catch-all and the track recorded
/// untagged — the one outcome rate limiting is supposed to be recoverable from.
/// </para>
/// <para>
/// <b>429 is not backed off, it is obeyed.</b> Spotify returns a <c>Retry-After</c> header saying
/// exactly how long to wait, and guessing shorter is what gets an application throttled harder.
/// Exponential backoff applies to the cases with no such instruction: a 429 that arrived without
/// the header, and the 5xx family, where the doubling is there to stop a struggling backend being
/// asked <see cref="DefaultMaximumRetries"/> times in as many milliseconds.
/// </para>
/// <para>
/// <b>Nothing here needs its own overall timeout.</b> Enrichment already runs under
/// <see cref="Offstream.Core.Metadata.TrackEnricher.DefaultDeadline"/>, so a <c>Retry-After</c>
/// longer than the deadline cancels the lookup and the recording continues untagged. Waiting out
/// a long throttle and losing the tags is the correct trade; ignoring it and hammering the API is
/// not.
/// </para>
/// </remarks>
public sealed class SpotifyRetryHandler : IRetryHandler
{
    /// <summary>The first backoff step, doubled on each subsequent attempt.</summary>
    public static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromSeconds(1);

    /// <summary>A ceiling on any single wait, however long the doubling or the header asks for.</summary>
    public static readonly TimeSpan DefaultMaximumDelay = TimeSpan.FromSeconds(60);

    /// <summary>Retries after the first attempt, so a request is made at most five times.</summary>
    public const int DefaultMaximumRetries = 4;

    /// <summary>
    /// The statuses worth trying again. 429 is handled separately; these are the transient server
    /// faults. 4xx codes other than 429 are the caller's fault and repeating them changes nothing.
    /// </summary>
    private static readonly HttpStatusCode[] TransientFaults =
    [
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    ];

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger? _logger;

    /// <param name="delay">
    /// How to wait. Injectable so tests can assert the schedule without spending it — the
    /// recording pipeline always takes the default.
    /// </param>
    /// <param name="logger">
    /// Where to report throttling. Injectable for the same reason as <paramref name="delay"/>:
    /// these lines are a feature, so they are asserted, and reassigning the static
    /// <see cref="Log.Logger"/> from a test would leak into every other test running beside it.
    /// </param>
    public SpotifyRetryHandler(Func<TimeSpan, CancellationToken, Task>? delay = null, ILogger? logger = null)
    {
        _delay = delay ?? Task.Delay;
        _logger = logger;
    }

    /// <summary>
    /// Resolved per call rather than captured, so the handler picks up whatever Serilog is
    /// configured with by the time a request runs, not whatever existed when it was built.
    /// </summary>
    private ILogger Logger => _logger ?? Log.Logger;

    /// <summary>The first backoff step. Doubles per attempt.</summary>
    public TimeSpan InitialBackoff { get; init; } = DefaultInitialBackoff;

    /// <summary>A ceiling on any single wait.</summary>
    public TimeSpan MaximumDelay { get; init; } = DefaultMaximumDelay;

    /// <summary>How many times to try again before giving the last response back.</summary>
    public int MaximumRetries { get; init; } = DefaultMaximumRetries;

    /// <inheritdoc />
    public async Task<IResponse> HandleRetry(
        IRequest request,
        IResponse response,
        IRetryHandler.RetryFunc retry,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(retry);

        var waited = TimeSpan.Zero;

        for (var attempt = 1; attempt <= MaximumRetries; attempt++)
        {
            var wait = DelayFor(response, attempt);

            if (wait is not { } interval) return response;

            Announce(response, interval, attempt);
            waited += interval;

            await _delay(interval, cancel);

            response = await retry(request, cancel);
        }

        // The budget is spent. If the last answer is one we would still have retried, the caller
        // is about to see a failure caused by a condition that outlasted us, and that is worth
        // saying plainly rather than leaving them to infer it from a bare status code.
        if (IsRateLimited(response) || IsTransientFault(response)) AnnounceGivingUp(response, waited);

        return response;
    }

    /// <summary>
    /// Says that a wait is happening, and why.
    /// </summary>
    /// <remarks>
    /// <b>Rate limiting is a warning, not a debug line.</b> It is a real condition with a real
    /// consequence — tags missing from files the user is about to keep — and the Record page's
    /// activity log shows Information and above by default, so anything quieter is invisible to
    /// everyone who has not gone looking for it. Transient server faults stay at Information: they
    /// are Spotify having a moment, they usually clear on the next attempt, and promoting them
    /// would make the Problems filter noisy enough to stop being read.
    /// </remarks>
    private void Announce(IResponse response, TimeSpan interval, int attempt)
    {
        if (IsRateLimited(response))
        {
            Logger.Warning(
                "Spotify is rate-limiting Offstream. Waiting {Seconds:F0}s as instructed before "
                + "retry {Attempt} of {Total}.",
                interval.TotalSeconds,
                attempt,
                MaximumRetries);

            return;
        }

        Logger.Information(
            "Spotify answered {Status}. Waiting {Seconds:F0}s before retry {Attempt} of {Total}.",
            (int)response.StatusCode,
            interval.TotalSeconds,
            attempt,
            MaximumRetries);
    }

    private void AnnounceGivingUp(IResponse response, TimeSpan waited)
    {
        if (IsRateLimited(response))
        {
            Logger.Warning(
                "Spotify is still rate-limiting Offstream after {Retries} retries and {Seconds:F0}s "
                + "of waiting. This lookup is being abandoned; recordings will keep working but may "
                + "go untagged until the limit clears.",
                MaximumRetries,
                waited.TotalSeconds);

            return;
        }

        Logger.Warning(
            "Spotify is still answering {Status} after {Retries} retries and {Seconds:F0}s of "
            + "waiting. This lookup is being abandoned.",
            (int)response.StatusCode,
            MaximumRetries,
            waited.TotalSeconds);
    }

    /// <summary>How long to wait before <paramref name="attempt"/>, or null to stop retrying.</summary>
    private TimeSpan? DelayFor(IResponse response, int attempt)
    {
        if (IsRateLimited(response))
        {
            // Spotify's own instruction wins. It is measured in seconds and can legitimately be
            // large; the caller's deadline is what bounds the total, not a number invented here.
            return Cap(RetryAfter(response) ?? Backoff(attempt));
        }

        return IsTransientFault(response) ? Cap(Backoff(attempt)) : null;
    }

    private static bool IsRateLimited(IResponse response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests;

    private static bool IsTransientFault(IResponse response) =>
        Array.IndexOf(TransientFaults, response.StatusCode) >= 0;

    /// <summary>Doubles from <see cref="InitialBackoff"/>: 1s, 2s, 4s, 8s.</summary>
    private TimeSpan Backoff(int attempt) => InitialBackoff * Math.Pow(2, attempt - 1);

    private TimeSpan Cap(TimeSpan delay) => delay > MaximumDelay ? MaximumDelay : delay;

    /// <summary>
    /// Reads <c>Retry-After</c>, which Spotify sends as whole seconds.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively: HTTP header names are case-insensitive and the SDK hands them
    /// over in whatever casing the wire used, so an ordinal lookup would silently miss and drop us
    /// back to guessing.
    /// </remarks>
    private static TimeSpan? RetryAfter(IResponse response)
    {
        if (response.Headers is not { } headers) return null;

        foreach (var header in headers)
        {
            if (!string.Equals(header.Key, "Retry-After", StringComparison.OrdinalIgnoreCase)) continue;

            return int.TryParse(header.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                && seconds >= 0
                ? TimeSpan.FromSeconds(seconds)
                : null;
        }

        return null;
    }
}
