using Offstream.App.ViewModels;
using Offstream.Core.Diagnostics;
using Serilog;
using Serilog.Core;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// The Record page's activity log: replay, append, clear.
/// </summary>
/// <remarks>
/// Runs without a <see cref="System.Windows.Application"/>, which is the point of the null check
/// in <c>OnLineWritten</c> — no dispatcher means the append happens inline. These tests therefore
/// exercise the same code path a logging call on the UI thread takes.
/// </remarks>
public sealed class RecordViewModelTests
{
    [Fact]
    public void Constructor_ReplaysLinesLoggedBeforeThePageExisted()
    {
        var sink = new InMemoryLogSink();
        using var logger = LoggerFor(sink);

        logger.Information("Offstream starting.");

        var viewModel = new RecordViewModel(sink);

        // Startup logs before the shell resolves anything; without the replay the user's first
        // sight of the activity log is an empty box.
        var line = Assert.Single(viewModel.LogLines);
        Assert.Contains("Offstream starting.", line, StringComparison.Ordinal);
    }

    [Fact]
    public void LineWritten_AppendsInOrder()
    {
        var sink = new InMemoryLogSink();
        using var logger = LoggerFor(sink);
        var viewModel = new RecordViewModel(sink);

        logger.Information("First");
        logger.Warning("Second");

        Assert.Equal(2, viewModel.LogLines.Count);
        Assert.Contains("First", viewModel.LogLines[0], StringComparison.Ordinal);
        Assert.Contains("Second", viewModel.LogLines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Append_CarriesTheLevel()
    {
        var sink = new InMemoryLogSink();
        using var logger = LoggerFor(sink);
        var viewModel = new RecordViewModel(sink);

        logger.Error("Encoder failed");

        Assert.Contains("[Error]", Assert.Single(viewModel.LogLines), StringComparison.Ordinal);
    }

    [Fact]
    public void ClearLog_EmptiesBothTheViewAndTheSink()
    {
        var sink = new InMemoryLogSink();
        using var logger = LoggerFor(sink);
        var viewModel = new RecordViewModel(sink);

        logger.Information("Something happened");
        viewModel.ClearLogCommand.Execute(null);

        Assert.Empty(viewModel.LogLines);

        // The sink has to be cleared too, or the next page that reads it replays what the user
        // just dismissed.
        Assert.Empty(sink.Snapshot());
    }

    [Fact]
    public void Status_StartsIdle() =>
        Assert.Equal(Offstream.App.Resources.Strings.RecordStatusIdle, new RecordViewModel(new InMemoryLogSink()).Status);

    [Fact]
    public void Constructor_RejectsANullSink() =>
        Assert.Throws<ArgumentNullException>(() => new RecordViewModel(null!));

    private static Logger LoggerFor(InMemoryLogSink sink) =>
        new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
}
