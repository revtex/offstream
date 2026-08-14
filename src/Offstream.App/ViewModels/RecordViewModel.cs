using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Offstream.App.Resources;
using Offstream.App.Services;
using Offstream.Core.Audio;
using Offstream.Core.Diagnostics;
using Offstream.Core.Recording;
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

/// <summary>A file this session has written, as the list shows it.</summary>
/// <param name="Title">Artist and title, as the recorder knew them.</param>
/// <param name="Detail">Where it landed, relative to the library root.</param>
/// <param name="Duration">Formatted length, so the view needs no converter.</param>
/// <param name="Path">Full path, for opening it.</param>
public sealed record SavedRecording(string Title, string Detail, string Duration, string Path);

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
    /// <summary>
    /// How many saved recordings the session list keeps.
    /// </summary>
    /// <remarks>
    /// The list answers "what has this session done lately", and the library answers everything
    /// beyond that. An overnight run saves hundreds of tracks and none of them are worth an
    /// unbounded collection on the UI thread.
    /// </remarks>
    private const int SavedLimit = 50;

    /// <summary>Parsed once: these are formatted on every save, and the pattern never changes.</summary>
    private static readonly CompositeFormat TracksFormat =
        CompositeFormat.Parse(Strings.RecordSessionTracks);

    /// <inheritdoc cref="TracksFormat" />
    private static readonly CompositeFormat DurationFormat =
        CompositeFormat.Parse(Strings.RecordSessionDuration);

    private readonly InMemoryLogSink _logSink;
    private readonly RecordingController _controller;

    /// <summary>Every line received, before filtering. The pane shows a subset of this.</summary>
    private readonly List<LogLine> _received = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsArmed))]
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

    /// <summary>The transport word on the display: <c>REC</c>, <c>WAIT</c> or <c>STOP</c>.</summary>
    [ObservableProperty]
    private string _transport = Strings.RecordTransportStopped;

    /// <summary>
    /// Whether audio is being written right now, as opposed to armed and waiting for a track.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="IsRecording"/>, which is true for both: the session is running
    /// from the moment Start succeeds, but between tracks it is listening rather than capturing.
    /// The display inverts its indicator block for one and outlines it for the other, which is
    /// the distinction a recordist reads first and the one the buttons cannot show.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArmed))]
    private bool _isCapturing;

    /// <summary>What the output is, as the display prints it.</summary>
    [ObservableProperty]
    private string _formatText = string.Empty;

    /// <summary>
    /// Why the last Start was refused, or null when nothing is wrong.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Status"/>, which describes the session in words and is not drawn:
    /// the display says what state it is in through the transport block, so the only text worth
    /// interrupting for is a failure. This drives a bar above the panel rather than a line inside
    /// it, which is what keeps a missing ffmpeg from reading as a slightly different idle state.
    /// It survives until the next Start, because "ffmpeg is not installed" is a condition and not
    /// an event.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    [ObservableProperty]
    private LogFilter _filter = LogFilter.Activity;

    /// <summary>
    /// The current track's cover art, already decoded.
    /// </summary>
    /// <remarks>
    /// Decoded eagerly rather than bound to the file path, because the file is a temporary the
    /// encode deletes as soon as it is done with it — often within a second of this being set.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverArt))]
    private ImageSource? _coverArt;

    /// <summary>Album and year for the current track, when the lookup found them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlbum))]
    private string _album = string.Empty;

    /// <summary>Where the track being recorded is going to land.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDestination))]
    private string _destination = string.Empty;

    /// <summary>Files written this session.</summary>
    /// <remarks>
    /// <see cref="SavedCountText"/> has to be notified explicitly. It was not, so the header
    /// read "0 saved" for the life of every session however many files landed — the count itself
    /// was right the whole time and only the text derived from it was stale.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaved))]
    [NotifyPropertyChangedFor(nameof(SavedCountText))]
    private int _savedCount;

    /// <summary>Combined length of everything saved this session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SavedDurationText))]
    private TimeSpan _savedDuration;

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
        controller.TrackSaved += OnTrackSaved;
        controller.TrackEnriched += OnTrackEnriched;

        // Seeds the format line, which describes what pressing Start would produce and so has
        // something to say before anything is running.
        Sync();
    }

    /// <summary>Lines currently shown, after <see cref="Filter"/>.</summary>
    public ObservableCollection<LogEntry> LogLines { get; } = [];

    /// <summary>
    /// What this session has written, newest first.
    /// </summary>
    /// <remarks>
    /// The page's answer to "what has it done?", which the activity log answers only by being
    /// read line by line. Newest first because the interesting entry is the one that just
    /// appeared, and a list that grows downward puts it wherever the scroll happens to be.
    /// </remarks>
    public ObservableCollection<SavedRecording> Saved { get; } = [];

    /// <summary>The filter dropdown's items.</summary>
    public IReadOnlyList<LogFilterOption> FilterOptions { get; } =
    [
        new(LogFilter.Problems, Strings.RecordFilterProblems),
        new(LogFilter.Activity, Strings.RecordFilterActivity),
        new(LogFilter.All, Strings.RecordFilterAll),
    ];

    /// <summary>Elapsed time on the current track, as the display shows it.</summary>
    /// <remarks>
    /// <para>
    /// A fixed-width <c>00:03:42</c> field. It carried unit letters — <c>00H03M42S</c> — until
    /// they were seen at display size: nine glyphs of which three are letters reads as a word, and
    /// the eye has to parse it before it can find the number. Colons are the separator every clock
    /// uses, so the grouping is recognised rather than read, and the digits are then the only
    /// things on the line with any weight.
    /// </para>
    /// <para>
    /// Zero-padded rather than dropping the hours until a track has one: an instrument's counter
    /// that changes width partway through a session has to be found again each time. Two leading
    /// zeroes buy a number that never moves.
    /// </para>
    /// </remarks>
    public string ElapsedText => Clock(Elapsed);

    /// <summary>A duration as a fixed-width <c>00:03:42</c> clock.</summary>
    /// <remarks>
    /// Built from <see cref="TimeSpan.TotalHours"/> rather than formatted with <c>hh</c>, which
    /// counts hours within a day and would roll a very long session back to zero.
    /// </remarks>
    private static string Clock(TimeSpan value) => string.Create(
        CultureInfo.CurrentCulture,
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}");

    /// <summary>
    /// The inverse of <see cref="IsRecording"/>, so the two transport buttons can swap places
    /// without a converter.
    /// </summary>
    public bool IsIdle => !IsRecording;

    public bool HasCoverArt => CoverArt is not null;

    public bool HasAlbum => !string.IsNullOrWhiteSpace(Album);

    public bool HasDestination => !string.IsNullOrWhiteSpace(Destination);

    public bool HasSaved => SavedCount > 0;

    /// <summary>How many files this session has written, labelled.</summary>
    public string SavedCountText =>
        string.Format(CultureInfo.CurrentCulture, TracksFormat, SavedCount);

    /// <summary>
    /// How much audio this session has written, on the same clock as the counter above.
    /// </summary>
    /// <remarks>
    /// It read <c>4m 12s</c>, which is friendlier prose and a worse readout: it sits inches from
    /// a fixed-width <c>00:04:12</c> on the display, and two spellings of a duration on one screen
    /// make the reader convert between them to compare. One clock format, one place to look.
    /// </remarks>
    public string SavedDurationText =>
        string.Format(CultureInfo.CurrentCulture, DurationFormat, Clock(SavedDuration));

    /// <summary>Whether <see cref="Problem"/> has anything worth interrupting for.</summary>
    public bool HasProblem => !string.IsNullOrWhiteSpace(Problem);

    /// <summary>
    /// Running, but between tracks — nothing is being written yet.
    /// </summary>
    /// <remarks>
    /// The display blinks its indicator on this and holds it solid on
    /// <see cref="IsCapturing"/>, which is the convention every recorder uses for standby against
    /// rolling. It is worth a distinct state rather than a third label because it is the one the
    /// user is most likely to misread: an armed session looks exactly like a recording one from
    /// the transport buttons, and a blink says "waiting for something to play" without a word.
    /// </remarks>
    public bool IsArmed => IsRecording && !IsCapturing;

    private bool CanStart => !IsRecording && !IsBusy;

    private bool CanStop => IsRecording && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        IsBusy = true;

        // A session's totals and list are that session's. Carrying the previous run's numbers
        // into a new one makes the strip meaningless the second time it is read.
        Saved.Clear();
        SavedCount = 0;
        SavedDuration = TimeSpan.Zero;

        try
        {
            var refusal = await _controller.StartAsync();

            // A refusal is the app declining, not failing: it says what to fix and leaves the
            // page exactly as it was, so the next press is the whole retry.
            Status = refusal ?? Strings.RecordStatusWaiting;
            Problem = refusal;
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
        Transport = Strings.RecordTransportStopped;
        IsCapturing = false;

        // The saved list and its totals survive a stop — they are what the session produced, and
        // that is worth reading after it ends. What describes a track in flight does not.
        Album = string.Empty;
        Destination = string.Empty;
        CoverArt = null;

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

    /// <summary>
    /// Shows a saved recording in Explorer, selected.
    /// </summary>
    /// <remarks>
    /// <c>/select,</c> rather than opening the folder, so the file the user clicked is the one
    /// highlighted — a folder holding a night of recordings is not an answer to "where did that
    /// one go". The path is passed as a separate argument, never concatenated into a command
    /// string: it is built from a Spotify window title, which is untrusted.
    /// </remarks>
    [RelayCommand]
    private static void ShowInExplorer(SavedRecording? recording)
    {
        if (recording is null || !File.Exists(recording.Path)) return;

        try
        {
            using var explorer = new Process
            {
                StartInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false },
            };

            explorer.StartInfo.ArgumentList.Add("/select,");
            explorer.StartInfo.ArgumentList.Add(recording.Path);
            explorer.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning(ex, "Explorer could not be opened for {Path}.", recording.Path);
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
        IsCapturing = progress.Stage == RecordingStage.Recording;

        Transport = progress.Stage switch
        {
            RecordingStage.Recording => Strings.RecordTransportRecording,
            RecordingStage.WaitingForTrack => Strings.RecordTransportWaiting,
            _ => Strings.RecordTransportStopped,
        };

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

    /// <summary>Adds a finished file to the session list and the totals.</summary>
    private void OnTrackSaved(object? sender, TrackSavedEventArgs e) => Dispatch(() =>
    {
        Saved.Insert(0, new SavedRecording(
            e.Track.ToString(),
            Relative(e.Path),
            string.Create(CultureInfo.CurrentCulture, $"{(int)e.Duration.TotalMinutes}:{e.Duration.Seconds:00}"),
            e.Path));

        // The list is a session view, not an archive: the library itself is the archive, and an
        // unbounded list is one more thing growing for the length of an overnight run.
        while (Saved.Count > SavedLimit) Saved.RemoveAt(Saved.Count - 1);

        SavedCount++;
        SavedDuration += e.Duration;
    });

    /// <summary>
    /// Applies the metadata lookup to the display: art, album and where the file is going.
    /// </summary>
    /// <remarks>
    /// Arrives a second or so into a track rather than at the end of one, which is what makes it
    /// worth showing at all — see <see cref="TrackEnrichedEventArgs"/>.
    /// </remarks>
    private void OnTrackEnriched(object? sender, TrackEnrichedEventArgs e) => Dispatch(() =>
    {
        Album = DescribeAlbum(e.Track);
        Destination = e.Destination is null ? string.Empty : Relative(e.Destination);
        CoverArt = Decode(e.CoverArtPath);
    });

    /// <summary>
    /// Drops the album, art and destination the moment the track changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These three arrive together from <see cref="OnTrackEnriched"/> a second or so into a track,
    /// and used to be cleared only when the session stopped. So they outlived the track they
    /// described: anything that is never enriched — an advertisement, or a lookup that found
    /// nothing — inherited the previous song's cover, album and save path and displayed them
    /// under its own name. An advertisement showing the last song's artwork and the file path it
    /// was written to is not a cosmetic fault; it says a file is being written that is not.
    /// </para>
    /// <para>
    /// Keyed on the displayed name changing rather than on a track-changed event, because that is
    /// exactly the moment the three become wrong, and it costs nothing on the reports that repeat
    /// the same track fourteen times a second — <see cref="ObservableObject"/> raises nothing when
    /// the value is unchanged, so this does not run.
    /// </para>
    /// <para>
    /// The order this depends on is that the progress report naming the new track arrives before
    /// its enrichment does, which the enricher's settle delay and round trip make certain. If it
    /// ever inverted, the cost is one track showing no album — a blank where something unknown
    /// goes, rather than a confident description of the wrong song.
    /// </para>
    /// </remarks>
    partial void OnNowPlayingChanged(string value)
    {
        Album = string.Empty;
        Destination = string.Empty;
        CoverArt = null;
    }

    /// <summary>Album and year, as one line, from whichever of the two the lookup found.</summary>
    private static string DescribeAlbum(Offstream.Core.Metadata.Track track) => (track.Album, track.Year) switch
    {
        ({ } album, { } year) when !string.IsNullOrWhiteSpace(album) =>
            string.Create(CultureInfo.CurrentCulture, $"{album} ({year})"),
        ({ } album, _) when !string.IsNullOrWhiteSpace(album) => album,
        _ => string.Empty,
    };

    /// <summary>
    /// Reads a cover-art temp file into an image that no longer needs it.
    /// </summary>
    /// <remarks>
    /// <see cref="BitmapCacheOption.OnLoad"/> and the explicit stream are both load-bearing.
    /// WPF's default is to keep the source open and decode lazily, which would hold a lock on a
    /// file the encode is about to delete — and then fail to draw once it had been. This reads
    /// the bytes now and lets go.
    /// </remarks>
    private static BitmapImage? Decode(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            using var stream = File.OpenRead(path);

            var image = new BitmapImage();

            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;

            // Decoded to roughly the size it is drawn at. A Spotify cover is 640px square and
            // this panel shows it at 64.
            image.DecodePixelWidth = 128;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
            // The art is decoration. A file already deleted, or bytes that are not an image,
            // costs the thumbnail and nothing else.
            Log.Debug(ex, "Cover art could not be shown.");
            return null;
        }
    }

    /// <summary>Shortens a path to what it is under the library root, since the root never varies.</summary>
    private string Relative(string path)
    {
        var root = _controller.OutputPath;

        return !string.IsNullOrEmpty(root)
               && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? path[root.Length..].TrimStart('\\', '/')
            : path;
    }

    /// <summary>Pulls the controller's state onto the page.</summary>
    private void Sync()
    {
        IsRecording = _controller.IsRunning;
        Level = _controller.Level;
        FormatText = _controller.FormatSummary;
    }

    /// <summary>Runs an update on the UI thread; see <see cref="UiThread"/>.</summary>
    private static void Dispatch(Action update) => UiThread.Dispatch(update);
}
