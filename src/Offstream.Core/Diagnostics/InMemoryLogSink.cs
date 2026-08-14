using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Offstream.Core.Diagnostics;

/// <summary>A log line as the in-app console pane consumes it.</summary>
/// <param name="Timestamp">When it was written.</param>
/// <param name="Level">Serilog level, for filtering and colouring.</param>
/// <param name="Message">Rendered message.</param>
public sealed record LogLine(DateTimeOffset Timestamp, LogEventLevel Level, string Message);

/// <summary>
/// A bounded, in-memory Serilog sink backing the app's console pane.
/// </summary>
/// <remarks>
/// The predecessor kept its console output in a settings string, which grew without bound
/// and was written back to disk on every change (plan §6). This keeps the last
/// <see cref="Capacity"/> lines in memory for display only — the durable copy is the
/// rotating file sink — so the pane can never become a storage problem.
/// </remarks>
public sealed class InMemoryLogSink : ILogEventSink
{
    /// <summary>Lines retained for the console pane. Older lines are dropped.</summary>
    public const int Capacity = 2000;

    private readonly ConcurrentQueue<LogLine> _lines = new();

    /// <summary>Raised on every line, so a view model can append without polling.</summary>
    public event EventHandler<LogLine>? LineWritten;

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var line = new LogLine(
            logEvent.Timestamp,
            logEvent.Level,
            Describe(logEvent));

        _lines.Enqueue(line);
        while (_lines.Count > Capacity && _lines.TryDequeue(out _))
        {
            // Trim to capacity.
        }

        LineWritten?.Invoke(this, line);
    }

    /// <summary>Snapshot of the retained lines, oldest first.</summary>
    public IReadOnlyList<LogLine> Snapshot() => [.. _lines];

    /// <summary>
    /// The rendered message, with the exception's type and message appended when there is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this the pane silently discards every reason.</b> <c>RenderMessage</c> expands the
    /// template and nothing else, so <c>Log.Warning(ex, "X failed")</c> displayed as "X failed" and
    /// the exception — the only part saying <em>why</em> — went nowhere. Reported exactly that way:
    /// repeated warnings "with no reason on why", while the answer sat in the file sink all along.
    /// </para>
    /// <para>
    /// Type and message only, never the stack trace. This is a one-line-per-entry list beside a
    /// recording, and a trace would push everything else off the screen; the file keeps the full
    /// detail for whoever needs it. The type name earns its place because it is often the whole
    /// diagnosis on its own — <c>ArgumentException</c> against <c>HttpRequestException</c>
    /// separates a bug in Offstream from the network being down.
    /// </para>
    /// </remarks>
    private static string Describe(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage(formatProvider: null);

        return logEvent.Exception is { } error
            ? $"{message} — {error.GetType().Name}: {error.Message}"
            : message;
    }

    public void Clear()
    {
        while (_lines.TryDequeue(out _))
        {
            // Drain.
        }
    }
}
