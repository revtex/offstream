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
}
