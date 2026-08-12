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
            logEvent.RenderMessage(formatProvider: null));

        _lines.Enqueue(line);
        while (_lines.Count > Capacity && _lines.TryDequeue(out _))
        {
            // Trim to capacity.
        }

        LineWritten?.Invoke(this, line);
    }

    /// <summary>Snapshot of the retained lines, oldest first.</summary>
    public IReadOnlyList<LogLine> Snapshot() => [.. _lines];

    public void Clear()
    {
        while (_lines.TryDequeue(out _))
        {
            // Drain.
        }
    }
}
