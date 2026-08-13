using Offstream.Core.Diagnostics;
using Serilog;
using Xunit;

namespace Offstream.Core.Tests;

public sealed class InMemoryLogSinkTests
{
    [Fact]
    public void CapturesWrittenLines()
    {
        var sink = new InMemoryLogSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        logger.Information("recording {Track}", "Artist - Title");

        var line = Assert.Single(sink.Snapshot());
        Assert.Contains("Artist - Title", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RaisesLineWritten()
    {
        var sink = new InMemoryLogSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        LogLine? observed = null;
        sink.LineWritten += (_, line) => observed = line;

        logger.Warning("device removed");

        Assert.NotNull(observed);
        Assert.Equal("device removed", observed.Message);
    }

    /// <summary>
    /// The predecessor stored console output in a settings string that grew without bound
    /// (plan §6). The pane must never become a storage problem again.
    /// </summary>
    [Fact]
    public void DropsOldestLinesBeyondCapacity()
    {
        var sink = new InMemoryLogSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        for (var i = 0; i < InMemoryLogSink.Capacity + 50; i++) logger.Information("line {Index}", i);

        var lines = sink.Snapshot();
        Assert.Equal(InMemoryLogSink.Capacity, lines.Count);
        Assert.DoesNotContain(lines, l => l.Message.EndsWith("line 0", StringComparison.Ordinal));
    }

    [Fact]
    public void ClearEmptiesTheBuffer()
    {
        var sink = new InMemoryLogSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        logger.Information("something");
        sink.Clear();

        Assert.Empty(sink.Snapshot());
    }

    /// <summary>
    /// The pane used to render the template and drop the exception, so a warning logged with a
    /// cause displayed without one. Reported from the field as repeated failures "with no reason
    /// on why" — the reason was in the log file the whole time and never on screen.
    /// </summary>
    [Fact]
    public void CarriesTheReasonWhenThereIsAnException()
    {
        var sink = new InMemoryLogSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        logger.Warning(
            new ArgumentException("String is empty or null (Parameter 'refreshToken')"),
            "{Provider} failed for {Track}; recording it untagged.",
            "Spotify",
            "Artist - Title");

        var line = Assert.Single(sink.Snapshot());

        Assert.Contains("recording it untagged", line.Message, StringComparison.Ordinal);
        Assert.Contains("ArgumentException", line.Message, StringComparison.Ordinal);
        Assert.Contains("refreshToken", line.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Type and message only. The pane is one line per entry beside a running recording, and a
    /// stack trace would push everything else off the screen; the file sink keeps the full detail.
    /// </summary>
    [Fact]
    public void DoesNotCarryTheStackTrace()
    {
        var sink = new InMemoryLogSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        Exception thrown;

        try
        {
            throw new InvalidOperationException("deliberate");
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        logger.Error(thrown, "something went wrong");

        var line = Assert.Single(sink.Snapshot());

        Assert.Contains("InvalidOperationException: deliberate", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", line.Message, StringComparison.Ordinal);
        Assert.Single(line.Message.Split('\n'));
    }

    /// <summary>A line with no exception gains no punctuation for one.</summary>
    [Fact]
    public void LeavesAnOrdinaryLineUntouched()
    {
        var sink = new InMemoryLogSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        logger.Information("recording {Track}", "Artist - Title");

        Assert.Equal("recording \"Artist - Title\"", Assert.Single(sink.Snapshot()).Message);
    }
}
