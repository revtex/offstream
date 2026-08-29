using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Offstream.App.Resources;
using Offstream.App.Services;
using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Offstream.Core.Naming;
using Offstream.Core.Recording;
using Offstream.Core.Settings;

namespace Offstream.App.ViewModels;

/// <summary>One row of the filename-token reference.</summary>
/// <param name="Token">The token as it is typed, braces included.</param>
/// <param name="Description">What it renders to.</param>
public sealed record TemplateToken(string Token, string Description);

/// <summary>One of the ready-made layouts offered under the template box.</summary>
/// <param name="Name">The button's label — the layout, not the tokens that spell it.</param>
/// <param name="Template">What clicking the button writes into the template box.</param>
/// <param name="Example">
/// The path this layout produces, rendered by the recorder's own naming code and shown as the
/// button's tooltip. A preset is only picked for its outcome, and the label has room for a
/// name but not for a path.
/// </param>
/// <param name="AutomationId">
/// Stable per layout rather than per position, so a UI test names the preset it means and
/// re-ordering the list does not silently re-point it at a different one.
/// </param>
public sealed record TemplatePreset(string Name, string Template, string Example, string AutomationId);

/// <summary>
/// Backs the Advanced page: file naming, the recording timer, detection and tag options, and
/// the two application settings that have nowhere else to live.
/// </summary>
/// <remarks>
/// <para>
/// The predecessor called this tab's detection controls "spy options". Plan §0 renames them —
/// they decide what counts as a track worth recording, and nothing here observes the user.
/// </para>
/// <para>
/// Shares <see cref="SettingsDocument"/> with <see cref="SettingsViewModel"/>: the two pages
/// edit one file, and each writing its own copy of the whole document would mean the tab saved
/// second silently reverted the other.
/// </para>
/// </remarks>
public sealed partial class AdvancedViewModel : ObservableValidator
{
    /// <summary>The longest timer the six-digit <c>hhmmss</c> field can hold.</summary>
    private static readonly TimeSpan MaximumTimer = new(99, 59, 59);

    /// <inheritdoc cref="SettingsViewModel.DeviceUnavailableFormat"/>
    private static readonly CompositeFormat FfmpegFoundFormat =
        CompositeFormat.Parse(Strings.AdvancedFfmpegFound);

    private static readonly CompositeFormat CounterInvalidFormat =
        CompositeFormat.Parse(Strings.AdvancedCounterInvalid);

    private static readonly CompositeFormat PresetExampleFormat =
        CompositeFormat.Parse(Strings.AdvancedTemplatePresetExample);

    private readonly SettingsDocument _document;
    private readonly IFileSystem _fileSystem;
    private bool _loading;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(AdvancedViewModel), nameof(ValidateTemplate))]
    [NotifyPropertyChangedFor(nameof(TemplatePreview))]
    [NotifyPropertyChangedFor(nameof(UsesCounter))]
    private string _template = FileNameTemplate.Default;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(AdvancedViewModel), nameof(ValidateCounter))]
    [NotifyPropertyChangedFor(nameof(TemplatePreview))]
    private string _fileCounter = "1";

    [ObservableProperty]
    private ExistingFilePolicy _existingFilePolicy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemplatePreview))]
    private bool _isTimerEnabled;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(AdvancedViewModel), nameof(ValidateTimer))]
    private string _timer = "01:00:00";

    [ObservableProperty]
    private RecordSelection _recordSelection;

    [ObservableProperty]
    private bool _writeCounterToTrackNumber;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private ChoiceOption<string?>? _selectedLanguage;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(AdvancedViewModel), nameof(ValidateFfmpegPath))]
    [NotifyPropertyChangedFor(nameof(FfmpegStatus))]
    private string _ffmpegPath = string.Empty;

    /// <summary>Whether the token reference is showing. Not persisted — it is a disclosure, not a setting.</summary>
    [ObservableProperty]
    private bool _isTokenReferenceOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveProblem))]
    private string? _saveProblem;

    public AdvancedViewModel(SettingsDocument document, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _document = document;
        _fileSystem = fileSystem;

        // The two keep-it answers adjacent, so the choice between them reads as the detail it
        // is — what to tell Spotify — rather than as a fourth unrelated policy.
        Policies =
        [
            new(ExistingFilePolicy.Skip, Strings.AdvancedExistingSkip),
            new(ExistingFilePolicy.SkipAndMoveOn, Strings.AdvancedExistingSkipAndMoveOn),
            new(ExistingFilePolicy.Overwrite, Strings.AdvancedExistingOverwrite),
            new(ExistingFilePolicy.Duplicate, Strings.AdvancedExistingDuplicate),
        ];

        // Widest last. The list reads as one scale from strictest to loosest, so the effect of
        // moving down it is the shape of the list rather than something to be read off it.
        Selections =
        [
            new(RecordSelection.KnownTracksOnly, Strings.AdvancedSelectionKnownTracksOnly),
            new(RecordSelection.EverythingExceptAds, Strings.AdvancedSelectionEverythingExceptAds),
            new(RecordSelection.Everything, Strings.AdvancedSelectionEverything),
        ];

        // Language names are written in the language they name. "Français" translated into
        // English would be no help at all to the person looking for it.
        Languages =
        [
            new(null, Strings.AdvancedLanguageSystem),
            new("en", "English"),
            new("fr", "Français"),
        ];

        Tokens = BuildTokenReference();

        Load();

        _document.Changed += OnDocumentChanged;
    }

    /// <summary>What to do when the destination file already exists.</summary>
    public IReadOnlyList<ChoiceOption<ExistingFilePolicy>> Policies { get; }

    /// <summary>How much of what Spotify plays is worth saving.</summary>
    /// <remarks>
    /// One list where there were three switches. The switches encoded these same three
    /// outcomes, but two of them did nothing unless the third allowed it, so the card had to
    /// grey controls out to explain itself and still read as three contradictory answers to
    /// one question. A question with one answer is a list.
    /// </remarks>
    public IReadOnlyList<ChoiceOption<RecordSelection>> Selections { get; }

    /// <summary>UI languages, plus following Windows.</summary>
    public IReadOnlyList<ChoiceOption<string?>> Languages { get; }

    /// <summary>The tokens a template may use, with what each renders to.</summary>
    public IReadOnlyList<TemplateToken> Tokens { get; }

    /// <summary>Whether the template uses <c>{count}</c>, and so whether the counter matters.</summary>
    public bool UsesCounter => FileNameTemplate.UsesCounter(Template);

    /// <summary>Whether <see cref="SaveProblem"/> has anything worth showing.</summary>
    public bool HasSaveProblem => !string.IsNullOrWhiteSpace(SaveProblem);

    /// <summary>
    /// What the next recording would be called, rendered through the real naming code.
    /// </summary>
    /// <remarks>
    /// <see cref="OutputPaths.BuildFromTemplate"/> rather than a preview-only renderer, so the
    /// preview cannot drift from the recorder — including the 260-character budgeting, which is
    /// the part that surprises people and the part a hand-rolled preview would omit.
    /// </remarks>
    public string TemplatePreview => Render(Template, withOutputFolder: true);

    /// <summary>The ready-made layouts offered as buttons under the template box.</summary>
    /// <remarks>
    /// <para>
    /// Data rather than a command and a button apiece, which is what the first two were: a new
    /// layout should cost a row here, not a command, a resource key, a button and a tooltip.
    /// </para>
    /// <para>
    /// Rebuilt on every read, because each example carries the output format's extension and the
    /// Settings page can change that underneath this one. Five short-lived records beats tracking
    /// which of them went stale.
    /// </para>
    /// </remarks>
    public IReadOnlyList<TemplatePreset> Presets =>
    [
        Preset("Default", Strings.AdvancedTemplateDefault, FileNameTemplate.Default),
        Preset("Grouped", Strings.AdvancedTemplateGrouped, FileNameTemplate.Grouped),
        Preset("Numbered", Strings.AdvancedTemplateNumbered, @"{artist}\{album} ({year})\{track:00} {title}"),
        Preset("ByYear", Strings.AdvancedTemplateByYear, @"{artist}\({year}) {album}\{track:00} {title}"),
        Preset("ByDay", Strings.AdvancedTemplateByDay, @"{date:yyyy-MM-dd}\{artist} - {title}"),
    ];

    /// <summary>Which ffmpeg the current path resolves to, or that it resolves to none.</summary>
    public string FfmpegStatus
    {
        get
        {
            var locator = new FFmpegLocator(_fileSystem, AppContext.BaseDirectory);

            return locator.TryLocate(Trimmed(FfmpegPath), out var location)
                ? string.Format(CultureInfo.CurrentCulture, FfmpegFoundFormat, location.ExecutablePath)
                : Strings.AdvancedFfmpegMissing;
        }
    }

    private void Load()
    {
        _loading = true;

        try
        {
            var settings = _document.Current;

            Template = settings.Output.Template;
            FileCounter = settings.Output.CurrentFileCounter.ToString(CultureInfo.CurrentCulture);
            ExistingFilePolicy = settings.Output.ExistingFilePolicy;
            RecordSelection = settings.Recording.RecordSelection;
            WriteCounterToTrackNumber = settings.Metadata.WriteCounterToTrackNumber;
            MinimizeToTray = settings.App.MinimizeToTray;
            FfmpegPath = settings.App.FfmpegPath ?? string.Empty;

            SelectedLanguage =
                Languages.FirstOrDefault(option => option.Value == settings.App.Language) ?? Languages[0];

            LoadTimer(settings.Recording.Timer);
        }
        finally
        {
            _loading = false;
        }

        ValidateAllProperties();
    }

    /// <summary>
    /// Splits the stored <c>hhmmss</c> into the checkbox and the field.
    /// </summary>
    /// <remarks>
    /// A disabled timer keeps whatever duration was last typed rather than resetting the field
    /// to zero, so turning it back on does not mean typing it again.
    /// </remarks>
    private void LoadTimer(string? stored)
    {
        if (stored is not { Length: 6 } || !stored.All(char.IsAsciiDigit) || stored == "000000")
        {
            IsTimerEnabled = false;
            return;
        }

        Timer = $"{stored[..2]}:{stored[2..4]}:{stored[4..]}";
        IsTimerEnabled = true;
    }

    private static IReadOnlyList<TemplateToken> BuildTokenReference()
    {
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["artist"] = Strings.AdvancedTokenArtist,
            ["title"] = Strings.AdvancedTokenTitle,
            ["album"] = Strings.AdvancedTokenAlbum,
            ["album_artist"] = Strings.AdvancedTokenAlbumArtist,
            ["year"] = Strings.AdvancedTokenYear,
            ["track"] = Strings.AdvancedTokenTrack,
            ["disc"] = Strings.AdvancedTokenDisc,
            ["count"] = Strings.AdvancedTokenCount,
            ["date"] = Strings.AdvancedTokenDate,
            ["time"] = Strings.AdvancedTokenTime,
        };

        // Driven from KnownTokens rather than from the dictionary, so a token added to the
        // renderer and not described here is a missing key rather than a silently absent row.
        return
        [
            .. FileNameTemplate.KnownTokens.Select(token =>
                new TemplateToken($"{{{token}}}", descriptions.GetValueOrDefault(token, string.Empty))),
        ];
    }

    /// <summary>The track the preview is rendered from.</summary>
    /// <remarks>
    /// Deliberately ordinary values: every field a token can reach is populated, so no token
    /// renders empty in the preview and then behaves differently on a real track.
    /// </remarks>
    private static Track SampleTrack() => new()
    {
        Artist = Strings.AdvancedSampleArtist,
        Title = Strings.AdvancedSampleTitle,
        Album = Strings.AdvancedSampleAlbum,
        AlbumArtists = [Strings.AdvancedSampleArtist],
        Year = 2026,
        AlbumPosition = 4,
        Disc = 1,
        Playing = true,
    };

    /// <summary>Writes a preset's layout into the template box.</summary>
    /// <remarks>
    /// Null-tolerant because the parameter arrives from a data template: a button whose item is
    /// gone mid-rebuild would otherwise take the page down for a click nobody meant.
    /// </remarks>
    [RelayCommand]
    private void UsePreset(TemplatePreset? preset)
    {
        if (preset is not null) Template = preset.Template;
    }

    private TemplatePreset Preset(string key, string name, string template) =>
        new(
            name,
            template,
            string.Format(
                CultureInfo.CurrentCulture,
                PresetExampleFormat,
                Render(template, withOutputFolder: false)),
            $"AdvancedTemplatePreset{key}");

    /// <summary>
    /// Renders a template against the sample track, through the recorder's own naming code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OutputPaths.BuildFromTemplate"/> rather than a preview-only renderer, so what
    /// is shown cannot drift from what is written — including the 260-character budgeting, which
    /// is the part that surprises people and the part a hand-rolled preview would omit.
    /// </para>
    /// <para>
    /// The output folder is dropped for a preset's example and kept for the live preview. The
    /// preview answers "where does the next recording go", which needs the root; a preset answers
    /// "what shape are the names", where the root is the same for all five and only costs the
    /// tooltip the width its answer needs. It is still passed to the renderer either way, so the
    /// length budgeting sees the real path.
    /// </para>
    /// </remarks>
    private string Render(string template, bool withOutputFolder)
    {
        var settings = _document.Current.ToRecordingSettings();
        settings.OutputTemplate = template;
        settings.InternalOrderNumber = int.TryParse(
            FileCounter, NumberStyles.Integer, CultureInfo.CurrentCulture, out var counter)
            ? counter
            : 1;

        try
        {
            var (folders, fileName) = OutputPaths.BuildFromTemplate(SampleTrack(), settings, DateTime.Now);
            var extension = EncodingProfiles.For(settings.MediaFormat).Extension;
            string[] root = withOutputFolder ? [settings.OutputPath ?? string.Empty] : [];

            return _fileSystem.Path.Combine([.. root, .. folders, $"{fileName}.{extension}"]);
        }
        catch (Exception ex) when (ex is UnrecognizedTrackException or InvalidOperationException
                                       or ArgumentException)
        {
            // A template can be well-formed and still render to nothing usable. The field's own
            // validation says why; the preview just declines to invent a file name.
            return Strings.AdvancedPreviewUnavailable;
        }
    }

    partial void OnTemplateChanged(string value) => Persist();

    partial void OnFileCounterChanged(string value) => Persist();

    partial void OnExistingFilePolicyChanged(ExistingFilePolicy value) => Persist();

    partial void OnIsTimerEnabledChanged(bool value) => Persist();

    partial void OnTimerChanged(string value) => Persist();

    partial void OnRecordSelectionChanged(RecordSelection value) => Persist();

    partial void OnWriteCounterToTrackNumberChanged(bool value) => Persist();

    partial void OnMinimizeToTrayChanged(bool value) => Persist();

    partial void OnSelectedLanguageChanged(ChoiceOption<string?>? value) => Persist();

    partial void OnFfmpegPathChanged(string value) => Persist();

    /// <inheritdoc cref="SettingsViewModel.Persist"/>
    private void Persist()
    {
        if (_loading) return;

        ValidateAllProperties();

        if (HasErrors)
        {
            SaveProblem = null;
            return;
        }

        SaveProblem = _document.Update(settings => settings with
        {
            Output = settings.Output with
            {
                Template = Template.Trim(),
                ExistingFilePolicy = ExistingFilePolicy,
                CurrentFileCounter = int.Parse(FileCounter, CultureInfo.CurrentCulture),
            },
            Recording = settings.Recording with
            {
                RecordSelection = RecordSelection,
                Timer = IsTimerEnabled ? ToStoredTimer(Timer) : null,
            },
            Metadata = settings.Metadata with { WriteCounterToTrackNumber = WriteCounterToTrackNumber },
            App = settings.App with
            {
                MinimizeToTray = MinimizeToTray,
                Language = SelectedLanguage?.Value,
                FfmpegPath = Trimmed(FfmpegPath),
            },
        });
    }

    /// <summary>The preview quotes the output folder, which the Settings page owns.</summary>
    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TemplatePreview));

        // The presets quote it too: their examples end in the output format's extension.
        OnPropertyChanged(nameof(Presets));
    }

    private static string? Trimmed(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Renders <c>hh:mm:ss</c> back to the six digits the schema stores.</summary>
    private static string? ToStoredTimer(string text) =>
        TryParseTimer(text, out var duration)
            ? $"{(int)duration.TotalHours:00}{duration.Minutes:00}{duration.Seconds:00}"
            : null;

    private static bool TryParseTimer(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;

        return !string.IsNullOrWhiteSpace(text)
               && TimeSpan.TryParseExact(
                   text.Trim(),
                   [@"h\:mm\:ss", @"hh\:mm\:ss", @"m\:ss"],
                   CultureInfo.InvariantCulture,
                   out duration)
               && duration > TimeSpan.Zero
               && duration <= MaximumTimer;
    }

    /// <summary>
    /// Passes <see cref="FileNameTemplate.Validate"/>'s own reason through.
    /// </summary>
    /// <remarks>
    /// It names the offending token — "Unknown token(s): {artistt}" — which is the whole value
    /// of the message. Replacing it with a translated generic would be a worse error in every
    /// language.
    /// </remarks>
    public static ValidationResult? ValidateTemplate(string? value, ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return FileNameTemplate.Validate(value) is { } problem
            ? new ValidationResult(problem)
            : ValidationResult.Success;
    }

    /// <summary>
    /// The counter must fit the padding the template asks for.
    /// </summary>
    /// <remarks>
    /// <c>{count:000}</c> tops out at 999, and a counter past it renders wider than the mask —
    /// which breaks both sort order and the "have I recorded this already?" check.
    /// </remarks>
    public static ValidationResult? ValidateCounter(string? value, ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var maximum = FileNameTemplate.GetCounterMax(((AdvancedViewModel)context.ObjectInstance).Template);

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var counter)
               && counter >= 1
               && counter <= maximum
            ? ValidationResult.Success
            : new ValidationResult(string.Format(
                CultureInfo.CurrentCulture, CounterInvalidFormat, maximum));
    }

    public static ValidationResult? ValidateTimer(string? value, ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // An unusable duration only matters while the timer is on; the field keeps its text
        // when it is off so that turning it back on does not mean typing it again.
        return !((AdvancedViewModel)context.ObjectInstance).IsTimerEnabled || TryParseTimer(value, out _)
            ? ValidationResult.Success
            : new ValidationResult(Strings.AdvancedTimerInvalid);
    }

    /// <summary>
    /// An ffmpeg path that resolves to nothing is an error; an empty one is not.
    /// </summary>
    /// <remarks>
    /// Empty means "use the bundled copy or PATH", which <see cref="FFmpegLocator"/> handles.
    /// A wrong explicit path is never a fallback (see that class): it stops recording, so it is
    /// worth stopping the save too.
    /// </remarks>
    public static ValidationResult? ValidateFfmpegPath(string? value, ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(value)) return ValidationResult.Success;

        var viewModel = (AdvancedViewModel)context.ObjectInstance;
        var locator = new FFmpegLocator(viewModel._fileSystem, AppContext.BaseDirectory);

        return locator.TryLocate(value.Trim(), out _)
            ? ValidationResult.Success
            : new ValidationResult(Strings.AdvancedFfmpegNotFound);
    }
}
