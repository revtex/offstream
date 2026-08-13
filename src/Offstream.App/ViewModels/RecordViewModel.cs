using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Offstream.App.Resources;
using Offstream.App.Services;
using Offstream.Core.Audio;
using Offstream.Core.Diagnostics;
using Serilog;
using Serilog.Events;

namespace Offstream.App.ViewModels;

/// <summary>How much of the activity log to show.</summary>
/// <remarks>
/// Three choices rather than one per Serilog level. The levels below Information are debugging
/// detail and the two above Warning differ only in how badly the same thing went wrong, so
/// offering six options would be offering five ways to ask the same question.
/// </remarks>
public enum LogFilter
{
    /// <summary>Warnings and errors.</summary>
    Problems,

    /// <summary>What the app is doing. The default.</summary>
    Activity,

    /// <summary>Including the debug detail.</summary>
    All,
}

/// <summary>One line in the activity log, formatted for display.</summary>
/// <param name="Level">Kept alongside the text so the view can colour problems without parsing.</param>
/// <param name="Text">Timestamp, level and message, as shown.</param>
public sealed record LogEntry(LogEventLevel Level, string Text);

/// <summary>One entry in the log filter dropdown.</summary>
public sealed record LogFilterOption(LogFilter Value, string Name);

/// <summary>
/// Backs the Record page: transport, now-playing, level, and the activity log.
/// </summary>
/// <remarks>
/// <para>
/// The console-log metaphor carries over from the predecessor deliberately (plan §11) — it is how
/// the app explains itself while it runs. What does not carry over is where the text lives: the
/// old app kept its console contents in a settings string, so the log was persisted user
/// configuration. Here the lines come from the Serilog sink and the durable copy is a rotating
/// file under <see cref="Offstream.Core.OffstreamPaths.LogDirectory"/>.
/// </para>
/// <para>
/// Everything the pipeline says arrives twice over: once as a <see cref="RecordingProgress"/>
/// report, which drives the status line and the elapsed counter, and once through Serilog, which
/// fills the log pane. That is not duplication — the two have different lifetimes. A progress
/// report describes right now and is overwritten by the next one; a log line is history and stays.
/// </para>
/// </remarks>
public sealed partial class RecordViewModel : ObservableObject
{
    private readonly InMemoryLogSink _logSink;
    private readonly RecordingController _controller;

    /// <summary>Every line received, before filtering. The pane shows a subset of this.</summary>
    private readonly List<LogLine> _received = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = Strings.RecordStatusIdle;

    [ObservableProperty]
    private string _nowPlaying = Strings.RecordNothingPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedText))]
    private TimeSpan _elapsed;

    /// <summary>
    /// The running session's meter, handed to the waveform. Null while nothing is running.
    /// </summary>
    [ObservableProperty]
    private AudioLevelMeter? _level;

    [ObservableProperty]
    private LogFilter _filter = LogFilter.Activity;

    public RecordViewModel(InMemoryLogSink logSink, RecordingController controller)
    {
        ArgumentNullException.ThrowIfNull(logSink);
        ArgumentNullException.ThrowIfNull(controller);

        _logSink = logSink;
        _controller = controller;

        // Startup logs before this page is ever shown, so replay what is already there before
        // subscribing - otherwise the first thing the user sees is an empty log.
        _received.AddRange(logSink.Snapshot());
        Rebuild();

        logSink.LineWritten += OnLineWritten;

        controller.Progress += OnProgress;
        controller.StateChanged += OnStateChanged;
    }

    /// <summary>Lines currently shown, after <see cref="Filter"/>.</summary>
    public ObservableCollection<LogEntry> LogLines { get; } = [];

    /// <summary>The filter dropdown's items.</summary>
    public IReadOnlyList<LogFilterOption> FilterOptions { get; } =
    [
        new(LogFilter.Problems, Strings.RecordFilterProblems),
        new(LogFilter.Activity, Strings.RecordFilterActivity),
        new(LogFilter.All, Strings.RecordFilterAll),
    ];

    /// <summary>Elapsed time on the current track, as the page shows it.</summary>
    /// <remarks>
    /// Minutes and seconds until a track passes an hour, because a leading <c>0:</c> on every
    /// three-minute song is a column of noise. Tracks that long exist — live sets, mixes — so the
    /// hour is not dropped, only omitted until it means something.
    /// </remarks>
    public string ElapsedText => Elapsed.ToString(
        Elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss",
        CultureInfo.CurrentCulture);

    /// <summary>
    /// The inverse of <see cref="IsRecording"/>, so the two transport buttons can swap places
    /// without a converter.
    /// </summary>
    public bool IsIdle => !IsRecording;

    private bool CanStart => !IsRecording && !IsBusy;

    private bool CanStop => IsRecording && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        IsBusy = true;

        try
        {
            var refusal = await _controller.StartAsync();

            // A refusal is the app declining, not failing: it says what to fix and leaves the
            // page exactly as it was, so the next press is the whole retry.
            Status = refusal ?? Strings.RecordStatusWaiting;
        }
        finally
        {
            IsBusy = false;
        }

        Sync();
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        IsBusy = true;
        Status = Strings.RecordStatusStopping;

        try
        {
            await _controller.StopAsync();
        }
        finally
        {
            IsBusy = false;
        }

        Elapsed = TimeSpan.Zero;
        NowPlaying = Strings.RecordNothingPlaying;
        Status = Strings.RecordStatusIdle;

        Sync();
    }

    /// <summary>Copies what is on screen — the filter is part of the selection.</summary>
    /// <remarks>
    /// Someone copying the log is about to paste it into a bug report. Copying the hidden lines
    /// too would hand them something other than what they were looking at.
    /// </remarks>
    [RelayCommand]
    private void CopyLog()
    {
        if (LogLines.Count == 0) return;

        var text = string.Join(Environment.NewLine, LogLines.Select(entry => entry.Text));

        try
        {
            Clipboard.SetText(text);
            Log.Information(Strings.RecordLogCopied, LogLines.Count);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // The Windows clipboard is a single shared lock and any process can hold it. Losing
            // that race must not take down the page the user is reading.
            Log.Warning(ex, "The clipboard was unavailable.");
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        _logSink.Clear();
        _received.Clear();
        LogLines.Clear();
    }

    partial void OnFilterChanged(LogFilter value) => Rebuild();

    /// <summary>Reapplies <see cref="Filter"/> to everything received so far.</summary>
    /// <remarks>
    /// Rebuilt rather than filtered in place: the sink is capped at
    /// <see cref="InMemoryLogSink.Capacity"/> lines, so the whole list is bounded and this costs
    /// less than maintaining a live view — and it only runs when the dropdown changes.
    /// </remarks>
    private void Rebuild()
    {
        LogLines.Clear();

        foreach (var line in _received.Where(Passes)) LogLines.Add(Format(line));
    }

    private bool Passes(LogLine line) => line.Level >= Minimum(Filter);

    private static LogEventLevel Minimum(LogFilter filter) => filter switch
    {
        LogFilter.Problems => LogEventLevel.Warning,
        LogFilter.Activity => LogEventLevel.Information,
        _ => LogEventLevel.Verbose,
    };

    private static LogEntry Format(LogLine line) =>
        new(line.Level, $"{line.Timestamp:HH:mm:ss} [{line.Level}] {line.Message}");

    /// <summary>
    /// Appends one line, dropping the oldest once the buffer is full so the pane scrolls
    /// rather than grows.
    /// </summary>
    /// <remarks>
    /// <b>Both collections have to be trimmed, not just the backing one.</b> Trimming
    /// <see cref="_received"/> alone left <see cref="LogLines"/> — the collection the ListBox is
    /// actually bound to — growing for the life of the session, so a long recording night ended
    /// with a pane holding far more lines than the sink had retained and a scrollbar that kept
    /// shrinking. <see cref="LogLines"/> is a filtered projection of <see cref="_received"/> in
    /// the same order, so the line falling out of the buffer is its first entry whenever that
    /// line was shown at all.
    /// </remarks>
    private void OnLineWritten(object? sender, LogLine line) => Dispatch(() =>
    {
        _received.Add(line);

        if (_received.Count > InMemoryLogSink.Capacity)
        {
            var dropped = _received[0];
            _received.RemoveAt(0);

            if (Passes(dropped) && LogLines.Count > 0) LogLines.RemoveAt(0);
        }

        if (Passes(line)) LogLines.Add(Format(line));
    });

    /// <summary>
    /// Applies a progress report to the status line, now-playing and the elapsed counter.
    /// </summary>
    /// <remarks>
    /// These arrive around fourteen times a second while a track plays, which is what makes the
    /// counter smooth and what makes this method's cost matter. Every assignment here is an
    /// <see cref="ObservableObject"/> property that raises nothing when the value is unchanged,
    /// so a report that repeats the previous one costs three comparisons and no layout.
    /// </remarks>
    private void OnProgress(object? sender, RecordingProgress progress) => Dispatch(() =>
    {
        NowPlaying = progress.Track ?? Strings.RecordNothingPlaying;
        Elapsed = progress.Elapsed ?? TimeSpan.Zero;

        if (IsBusy) return;

        Status = progress.Stage switch
        {
            RecordingStage.WaitingForTrack => Strings.RecordStatusWaiting,
            RecordingStage.Recording => Strings.RecordStatusRecording,
            RecordingStage.Stopped or RecordingStage.Idle => Strings.RecordStatusIdle,
            _ => Status,
        };
    });

    private void OnStateChanged(object? sender, EventArgs e) => Dispatch(Sync);

    /// <summary>Pulls the controller's state onto the page.</summary>
    private void Sync()
    {
        IsRecording = _controller.IsRunning;
        Level = _controller.Level;
    }

    /// <summary>Runs an update on the UI thread; see <see cref="UiThread"/>.</summary>
    private static void Dispatch(Action update) => UiThread.Dispatch(update);
}
