using Offstream.App.Resources;
using Offstream.App.Services;
using Offstream.App.ViewModels;
using Offstream.Core.Diagnostics;
using Serilog;
using Serilog.Core;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// The Record page: transport state, the elapsed counter, and the activity log.
/// </summary>
/// <remarks>
/// Runs without a <see cref="System.Windows.Application"/>, which is the point of the null check
/// in <c>Dispatch</c> — no dispatcher means the update happens inline. These tests therefore
/// exercise the same code path a report already on the UI thread takes.
/// </remarks>
public sealed class RecordViewModelTests
{
    [Fact]
    public void Constructor_ReplaysLinesLoggedBeforeThePageExisted()
    {
        var sink = new InMemoryLogSink();
        using var logger = LoggerFor(sink);

        logger.Information("Offstream starting.");

        var viewModel = ViewModelFor(sink);

        // Startup logs before the shell resolves anything; without the replay the user's first
        // sight of the activity log is an empty box.
        var line = Assert.Single(viewModel.LogLines);
        Assert.Contains("Offstream starting.", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void LineWritten_AppendsInOrder()
    {
        var sink = new InMemoryLogSink();
        using var logger = LoggerFor(sink);
        var viewModel = ViewModelFor(sink);

        logger.Information("First");
        logger.Warning("Second");

        Assert.Equal(2, viewModel.LogLines.Count);
        Assert.Contains("First", viewModel.LogLines[0].Text, StringComparison.Ordinal);
        Assert.Contains("Second", viewModel.LogLines[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_CarriesTheLevel()
    {
        var sink = new InMemoryLogSink();
        using var logger = LoggerFor(sink);
        var viewModel = ViewModelFor(sink);

        logger.Error("Encoder failed");

        var entry = Assert.Single(viewModel.LogLines);

        Assert.Contains("[Error]", entry.Text, StringComparison.Ordinal);

        // Carried alongside the text as well, so the view colours the line instead of matching a
        // substring back out of it.
        Assert.Equal(Serilog.Events.LogEventLevel.Error, entry.Level);
    }

    [Fact]
    public void ClearLog_EmptiesBothTheViewAndTheSink()
    {
        var sink = new InMemoryLogSink();
        using var logger = LoggerFor(sink);
        var viewModel = ViewModelFor(sink);

        logger.Information("Something happened");
        viewModel.ClearLogCommand.Execute(null);

        Assert.Empty(viewModel.LogLines);

        // The sink has to be cleared too, or the next page that reads it replays what the user
        // just dismissed.
        Assert.Empty(sink.Snapshot());
    }

    [Fact]
    public void Filter_DefaultsToActivity()
    {
        var sink = new InMemoryLogSink();
        using var logger = LoggerFor(sink);
        var viewModel = ViewModelFor(sink);

        logger.Debug("Polled Spotify");
        logger.Information("Recording started");

        // Debug detail is for a log file, not for the pane someone watches while recording.
        var entry = Assert.Single(viewModel.LogLines);
        Assert.Contains("Recording started", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_Problems_HidesOrdinaryActivity()
    {
        var sink = new InMemoryLogSink();
        using var logger = LoggerFor(sink);
        var viewModel = ViewModelFor(sink);

        logger.Information("Recording started");
        logger.Warning("Track was too short");
        logger.Error("Encoder failed");

        viewModel.Filter = LogFilter.Problems;

        Assert.Equal(2, viewModel.LogLines.Count);
        Assert.DoesNotContain(viewModel.LogLines, entry => entry.Text.Contains("Recording started", StringComparison.Ordinal));
    }

    [Fact]
    public void Filter_All_ShowsWhatWasHiddenBefore()
    {
        var sink = new InMemoryLogSink();
        using var logger = LoggerFor(sink);
        var viewModel = ViewModelFor(sink);

        logger.Debug("Polled Spotify");

        Assert.Empty(viewModel.LogLines);

        // The filter is a view over everything received, not a subscription that starts when it
        // changes - widening it has to reveal history, not just future lines.
        viewModel.Filter = LogFilter.All;

        Assert.Single(viewModel.LogLines);
    }

    [Fact]
    public void Filter_AppliesToLinesThatArriveAfterItChanges()
    {
        var sink = new InMemoryLogSink();
        using var logger = LoggerFor(sink);
        var viewModel = ViewModelFor(sink);

        viewModel.Filter = LogFilter.Problems;

        logger.Information("Recording started");
        logger.Error("Encoder failed");

        Assert.Single(viewModel.LogLines);
    }

    [Fact]
    public void Status_StartsIdle() => Assert.Equal(Strings.RecordStatusIdle, ViewModelFor().Status);

    [Fact]
    public void NowPlaying_StartsEmptyHanded() =>
        Assert.Equal(Strings.RecordNothingPlaying, ViewModelFor().NowPlaying);

    [Fact]
    public void ElapsedText_StartsAtZero() => Assert.Equal("0:00", ViewModelFor().ElapsedText);

    [Theory]
    [InlineData(0, 0, 7, "0:07")]
    [InlineData(0, 3, 42, "3:42")]
    [InlineData(0, 12, 5, "12:05")]
    [InlineData(1, 4, 9, "1:04:09")]
    public void ElapsedText_ShowsTheHourOnlyWhenThereIsOne(int hours, int minutes, int seconds, string expected)
    {
        var viewModel = ViewModelFor();

        viewModel.Elapsed = new TimeSpan(hours, minutes, seconds);

        Assert.Equal(expected, viewModel.ElapsedText);
    }

    [Fact]
    public void IsIdle_TracksIsRecording()
    {
        var viewModel = ViewModelFor();

        Assert.True(viewModel.IsIdle);

        viewModel.IsRecording = true;

        // The two transport buttons swap on this, so a stale value leaves both visible or neither.
        Assert.False(viewModel.IsIdle);
    }

    [Fact]
    public void StopCommand_CannotRunBeforeAnythingStarts()
    {
        var viewModel = ViewModelFor();

        Assert.True(viewModel.StartCommand.CanExecute(null));
        Assert.False(viewModel.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartCommand_RunsTheControllerAndSwapsTheTransport()
    {
        var viewModel = ViewModelFor();

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsRecording);
        Assert.False(viewModel.StartCommand.CanExecute(null));
        Assert.True(viewModel.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartCommand_ExposesTheLevelMeterToTheWaveform()
    {
        var viewModel = ViewModelFor();

        Assert.Null(viewModel.Level);

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.Level);
    }

    [Fact]
    public async Task StartCommand_WhenRefused_ShowsTheReasonAndStaysIdle()
    {
        var factory = new FakeSessionFactory
        {
            Failure = new Offstream.Core.Encoding.FFmpegNotFoundException("nowhere"),
        };

        var viewModel = ViewModelFor(factory: factory);

        await viewModel.StartCommand.ExecuteAsync(null);

        // A refusal leaves the page exactly as it was, so pressing start again is the whole retry.
        Assert.Equal(Strings.RecordCannotStartFfmpeg, viewModel.Status);
        Assert.False(viewModel.IsRecording);
        Assert.True(viewModel.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task StopCommand_ClearsWhatWasPlaying()
    {
        var viewModel = ViewModelFor();

        await viewModel.StartCommand.ExecuteAsync(null);

        viewModel.NowPlaying = "Someone - Something";
        viewModel.Elapsed = TimeSpan.FromMinutes(2);

        await viewModel.StopCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsRecording);
        Assert.Equal(Strings.RecordNothingPlaying, viewModel.NowPlaying);
        Assert.Equal(TimeSpan.Zero, viewModel.Elapsed);
        Assert.Equal(Strings.RecordStatusIdle, viewModel.Status);
        Assert.Null(viewModel.Level);
    }

    [Fact]
    public void FilterOptions_OfferOneChoicePerFilter() =>
        Assert.Equal(
            Enum.GetValues<LogFilter>().Order(),
            ViewModelFor().FilterOptions.Select(option => option.Value).Order());

    [Fact]
    public void Constructor_RejectsNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new RecordViewModel(null!, ControllerFor()));
        Assert.Throws<ArgumentNullException>(() => new RecordViewModel(new InMemoryLogSink(), null!));
    }

    private static RecordViewModel ViewModelFor(
        InMemoryLogSink? sink = null,
        FakeSessionFactory? factory = null) =>
        new(sink ?? new InMemoryLogSink(), ControllerFor(factory));

    private static RecordingController ControllerFor(FakeSessionFactory? factory = null) =>
        new(factory ?? new FakeSessionFactory(), RecordingFakes.Store());

    private static Logger LoggerFor(InMemoryLogSink sink) =>
        new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
}
