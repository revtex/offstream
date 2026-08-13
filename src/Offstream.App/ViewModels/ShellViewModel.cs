using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Offstream.App.Resources;
using Offstream.App.Services;
using Offstream.Core.Diagnostics;

namespace Offstream.App.ViewModels;

/// <summary>
/// Backs the shell window itself — the title bar, the tray icon, and anything that outlives a
/// page.
/// </summary>
/// <remarks>
/// <para>
/// The pages own their own state and their own ViewModels; the shell's job is the chrome around
/// them. What lands here is what has no page to belong to: the startup warning, because a
/// settings file that would not load is a whole-application condition, and the tray, because it
/// is the app's only face while the window is hidden.
/// </para>
/// <para>
/// The tray reads recording state from <see cref="RecordingController"/> directly rather than
/// from <see cref="RecordViewModel"/>. Both are singletons so either would work, but the
/// controller is the source of truth and the Record page is a peer reading the same events —
/// chaining one ViewModel off another would mean the tray silently stopped updating whenever
/// the page's own logic changed.
/// </para>
/// <para>
/// <see cref="Record"/> is the deliberate exception to that. The transport button lives in the
/// shell header rather than on the page, but it is still the Record page's transport — same
/// commands, same busy state, same refusal text — so it binds the very ViewModel that owns them
/// instead of a second copy that would have to be kept in step. What the rule above forbids is
/// reading a peer's <i>state</i> through a chain; this is the page's own control, relocated.
/// </para>
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <remarks>
    /// Cached because CA1863 asks for it, and safe to freeze at first use for the same reason
    /// the settings pages' formats are: changing the UI language takes a restart.
    /// </remarks>
    private static readonly CompositeFormat RecordingFormat =
        CompositeFormat.Parse(Strings.TrayRecording);

    private readonly RecordingController _controller;
    private readonly SettingsDocument _settings;

    /// <summary>Which tab is showing. The content host and the tab strip both bind this.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordTab))]
    [NotifyPropertyChangedFor(nameof(IsSettingsTab))]
    [NotifyPropertyChangedFor(nameof(IsAdvancedTab))]
    private ShellTab _tab = ShellTab.Record;

    /// <summary>The track the last progress report named, or null when nothing is playing.</summary>
    private string? _track;

    /// <summary>
    /// Why settings could not be read, or null when they loaded. Set once during startup.
    /// </summary>
    /// <remarks>
    /// <see cref="Offstream.Core.Settings.SettingsStore.LoadOrDefault"/> hands back the reason
    /// rather than throwing, so the app still opens on defaults. Showing that reason is the
    /// other half of the bargain: silently starting on defaults would look identical to a first
    /// run and leave the user wondering where their settings went.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartupWarning))]
    private string? _startupWarning;

    /// <summary>Whether a session is running, which is what colours the tray icon.</summary>
    [ObservableProperty]
    private bool _isRecording;

    /// <summary>The tray icon's tooltip — the only status readout while the window is hidden.</summary>
    [ObservableProperty]
    private string _trayTooltip = Strings.TrayIdle;

    /// <summary>
    /// Whether the window is currently hidden in the tray.
    /// </summary>
    /// <remarks>
    /// The tray icon exists only while this is true. An icon that is always there is a
    /// permanent tray citizen the user never asked for, and the setting is called "minimise to
    /// tray" — so the icon is what the window turned into, not a second copy of it.
    /// </remarks>
    [ObservableProperty]
    private bool _isInTray;

    public ShellViewModel(RecordingController controller, SettingsDocument settings, RecordViewModel record)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(settings);

        _controller = controller;
        _settings = settings;
        Record = record ?? throw new ArgumentNullException(nameof(record));

        controller.StateChanged += OnStateChanged;
        controller.Progress += OnProgress;
    }

    /// <summary>Raised when the user asks for the window back — tray click, menu, or second launch.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>Raised when the user picks Exit from the tray menu.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// The Record page's ViewModel, for the transport button in the header.
    /// </summary>
    /// <remarks>
    /// The button is only offered on the Record tab, so it is the page's control in every sense
    /// except where it is drawn. See the class remarks for why this chaining is allowed where the
    /// tray's is not.
    /// </remarks>
    public RecordViewModel Record { get; }

    /// <summary>
    /// Which tab is showing, one property per tab.
    /// </summary>
    /// <remarks>
    /// The host keeps all three pages loaded and toggles their visibility, so each needs its own
    /// boolean to bind. <see cref="IsRecordTab"/> earns a second job: it is also what offers the
    /// transport button, which belongs to the Record tab alone.
    /// </remarks>
    public bool IsRecordTab => Tab == ShellTab.Record;

    /// <inheritdoc cref="IsRecordTab"/>
    public bool IsSettingsTab => Tab == ShellTab.Settings;

    /// <inheritdoc cref="IsRecordTab"/>
    public bool IsAdvancedTab => Tab == ShellTab.Advanced;

    /// <summary>Whether <see cref="StartupWarning"/> has anything worth showing.</summary>
    public bool HasStartupWarning => !string.IsNullOrWhiteSpace(StartupWarning);

    /// <summary>
    /// Whether minimising should hide the window instead, read fresh from the settings.
    /// </summary>
    /// <remarks>
    /// Read on each minimise rather than cached, so toggling it on the Advanced page takes
    /// effect on the very next minimise instead of at the next launch.
    /// </remarks>
    public bool MinimizeToTray => _settings.Current.App.MinimizeToTray;

    /// <summary>Brings the window back, from the tray icon or a second launch.</summary>
    [RelayCommand]
    public void Show() => UiThread.Dispatch(() => ShowRequested?.Invoke(this, EventArgs.Empty));

    /// <summary>
    /// Quits from the tray menu.
    /// </summary>
    /// <remarks>
    /// The tray menu needs its own way out. Without one, an app hidden in the tray can only be
    /// quit by restoring it first — and the predecessor, whose tray icon had no menu at all, is
    /// exactly where that papercut came from.
    /// </remarks>
    [RelayCommand]
    public void Exit() => UiThread.Dispatch(() => ExitRequested?.Invoke(this, EventArgs.Empty));

    private void OnStateChanged(object? sender, EventArgs e) => UiThread.Dispatch(() =>
    {
        IsRecording = _controller.IsRunning;

        // A stopped session leaves no track playing, and a tooltip still naming one would
        // claim a recording that has finished.
        if (!IsRecording)
        {
            _track = null;
        }

        UpdateTooltip();
    });

    private void OnProgress(object? sender, RecordingProgress progress) => UiThread.Dispatch(() =>
    {
        _track = progress.Track;
        UpdateTooltip();
    });

    private void UpdateTooltip() =>
        TrayTooltip = (IsRecording, _track) switch
        {
            (false, _) => Strings.TrayIdle,
            (true, null or "") => Strings.TrayWaiting,
            (true, var track) => string.Format(
                System.Globalization.CultureInfo.CurrentCulture, RecordingFormat, track),
        };
}
