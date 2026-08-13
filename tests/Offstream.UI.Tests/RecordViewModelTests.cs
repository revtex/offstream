using System.ComponentModel;
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
    public void ElapsedText_StartsAtZero() => Assert.Equal("00:00:00", ViewModelFor().ElapsedText);

    /// <summary>
    /// The counter is a fixed-width field on an instrument face, so every part is padded and
    /// none is dropped - a number that changes width partway through a session is one the eye
    /// has to find again.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 7, "00:00:07")]
    [InlineData(0, 3, 42, "00:03:42")]
    [InlineData(0, 12, 5, "00:12:05")]
    [InlineData(1, 4, 9, "01:04:09")]
    public void ElapsedText_IsAlwaysTheSameWidth(int hours, int minutes, int seconds, string expected)
    {
        var viewModel = ViewModelFor();

        viewModel.Elapsed = new TimeSpan(hours, minutes, seconds);

        Assert.Equal(expected, viewModel.ElapsedText);
    }

    /// <summary>
    /// Hours beyond a day keep counting. A <c>hh</c> format string counts hours within a day and
    /// would roll a long session back to zero - unlikely on one track, but the counter is the
    /// page's proof that time is passing and it must not lie.
    /// </summary>
    [Fact]
    public void ElapsedText_PastADay_KeepsCounting()
    {
        var viewModel = ViewModelFor();

        viewModel.Elapsed = new TimeSpan(26, 1, 2);

        Assert.Equal("26:01:02", viewModel.ElapsedText);
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

    /// <summary>
    /// The pane scrolls; it does not grow. Trimming the backing buffer alone left the bound
    /// collection growing for the life of the session.
    /// </summary>
    [Fact]
    public void LineWritten_PastCapacity_DropsTheOldestShownLine()
    {
        var sink = new InMemoryLogSink();
        using var logger = LoggerFor(sink);
        var viewModel = ViewModelFor(sink);

        for (var index = 0; index < InMemoryLogSink.Capacity + 50; index++)
            logger.Information("Line {Index}", index);

        Assert.Equal(InMemoryLogSink.Capacity, viewModel.LogLines.Count);

        // The window slid: the first fifty lines are gone and the newest is the last written.
        Assert.Contains("Line 50", viewModel.LogLines[0].Text, StringComparison.Ordinal);
        Assert.Contains("Line 2049", viewModel.LogLines[^1].Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bound collection is a filtered projection, so a dropped line it never showed must not
    /// evict a line it did.
    /// </summary>
    [Fact]
    public void LineWritten_PastCapacity_DoesNotEvictOnBehalfOfAFilteredLine()
    {
        var sink = new InMemoryLogSink();
        using var logger = LoggerFor(sink);
        var viewModel = ViewModelFor(sink);

        viewModel.Filter = LogFilter.Problems;

        // Fill the buffer with traffic the Problems filter hides, then log the one thing it shows.
        for (var index = 0; index < InMemoryLogSink.Capacity; index++)
            logger.Debug("Chatter {Index}", index);

        logger.Error("Encoder failed");

        // Every further line evicts a hidden one, so the error must stay put.
        for (var index = 0; index < 100; index++) logger.Debug("More chatter {Index}", index);

        var entry = Assert.Single(viewModel.LogLines);
        Assert.Contains("Encoder failed", entry.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The display's indicator field. "Running" and "writing audio" are different states and the
    /// transport buttons cannot tell them apart - both show Stop.
    /// </summary>
    [Theory]
    [InlineData(RecordingStage.Recording, true)]
    [InlineData(RecordingStage.WaitingForTrack, false)]
    [InlineData(RecordingStage.Idle, false)]
    [InlineData(RecordingStage.Stopped, false)]
    public async Task Progress_SetsTheTransportIndicator(RecordingStage stage, bool capturing)
    {
        var factory = new FakeSessionFactory();
        var controller = ControllerFor(factory);
        var viewModel = new RecordViewModel(new InMemoryLogSink(), controller);

        var expected = stage switch
        {
            RecordingStage.Recording => Strings.RecordTransportRecording,
            RecordingStage.WaitingForTrack => Strings.RecordTransportWaiting,
            _ => Strings.RecordTransportStopped,
        };

        await controller.StartAsync();
        await ReportAsync(factory, viewModel, new RecordingProgress(stage), () => viewModel.Transport == expected);

        Assert.Equal(capturing, viewModel.IsCapturing);
        Assert.Equal(expected, viewModel.Transport);
    }

    /// <summary>
    /// Armed is running-but-not-writing, which is what the indicator blinks on. Both it and
    /// capturing show Stop on the buttons, so nothing else on the page distinguishes them.
    /// </summary>
    [Fact]
    public async Task IsArmed_IsRunningWithoutCapturing()
    {
        var factory = new FakeSessionFactory();
        var controller = ControllerFor(factory);
        var viewModel = new RecordViewModel(new InMemoryLogSink(), controller);

        // Not running at all is not armed - a stopped display must not blink.
        Assert.False(viewModel.IsArmed);

        await viewModel.StartCommand.ExecuteAsync(null);
        await ReportAsync(
            factory,
            viewModel,
            new RecordingProgress(RecordingStage.WaitingForTrack),
            () => viewModel.IsArmed);

        Assert.True(viewModel.IsArmed);

        await ReportAsync(
            factory,
            viewModel,
            new RecordingProgress(RecordingStage.Recording),
            () => viewModel.IsCapturing);

        // Solid, not blinking, the moment audio starts reaching the encoder.
        Assert.False(viewModel.IsArmed);
        Assert.True(viewModel.IsCapturing);
    }

    /// <summary>
    /// The blink is driven by a trigger on this, so it has to raise when either half changes -
    /// a computed property with a missing notification leaves the indicator stuck.
    /// </summary>
    [Fact]
    public void IsArmed_RaisesWhenEitherHalfChanges()
    {
        var viewModel = ViewModelFor();
        var raised = 0;

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RecordViewModel.IsArmed)) raised++;
        };

        viewModel.IsRecording = true;
        Assert.Equal(1, raised);

        viewModel.IsCapturing = true;
        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task StopCommand_ResetsTheTransportIndicator()
    {
        var factory = new FakeSessionFactory();
        var controller = ControllerFor(factory);
        var viewModel = new RecordViewModel(new InMemoryLogSink(), controller);

        await viewModel.StartCommand.ExecuteAsync(null);
        await ReportAsync(
            factory,
            viewModel,
            new RecordingProgress(RecordingStage.Recording),
            () => viewModel.IsCapturing);

        await viewModel.StopCommand.ExecuteAsync(null);

        // A block left inverted after Stop says the app is still writing a file.
        Assert.False(viewModel.IsCapturing);
        Assert.Equal(Strings.RecordTransportStopped, viewModel.Transport);
    }

    /// <summary>
    /// The format line describes what pressing Start would produce, so it has to say something
    /// before anything is running - an empty field on an idle display reads as a fault.
    /// </summary>
    [Fact]
    public void FormatText_IsPopulatedBeforeAnythingStarts()
    {
        var text = ViewModelFor().FormatText;

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Equal(text, text.ToUpperInvariant());
    }

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
        new(factory ?? new FakeSessionFactory(), RecordingFakes.Document());

    private static Logger LoggerFor(InMemoryLogSink sink) =>
        new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

    /// <summary>
    /// Reports progress and waits for the view model to have acted on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IProgress{T}.Report"/> is asynchronous. <see cref="Progress{T}"/> captures the
    /// <see cref="SynchronizationContext"/> current when it is constructed, and a unit test has
    /// none — so the callback is posted to the thread pool and <c>Report</c> returns before the
    /// view model has changed anything. Asserting on the next line is a race, and this suite lost
    /// it often enough to make a green run meaningless.
    /// </para>
    /// <para>
    /// Waiting on <see cref="INotifyPropertyChanged.PropertyChanged"/> rather than sleeping keeps
    /// the tests both deterministic and fast: the same shape <c>ShellViewModelTests.TooltipAfter</c>
    /// already used for the same reason. The condition is re-checked on every notification because
    /// one report can raise several, and the interesting one is not always first.
    /// </para>
    /// </remarks>
    private static async Task ReportAsync(
        FakeSessionFactory factory,
        RecordViewModel viewModel,
        RecordingProgress progress,
        Func<bool> settled)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (settled()) changed.TrySetResult();
        }

        viewModel.PropertyChanged += OnPropertyChanged;

        try
        {
            factory.Progress!.Report(progress);

            if (settled()) return;

            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            viewModel.PropertyChanged -= OnPropertyChanged;
        }
    }
}
