using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Offstream.App.Resources;
using Offstream.App.Services;
using Offstream.Core.Audio;
using Offstream.Core.Encoding;
using Offstream.Core.Metadata;

namespace Offstream.App.ViewModels;

/// <summary>One render endpoint in the device list.</summary>
/// <param name="Id">The endpoint id, or null for "whatever Windows is using".</param>
/// <param name="Name">What the dropdown shows.</param>
/// <param name="IsAvailable">False for a stored device that is not currently connected.</param>
public sealed record AudioDeviceOption(string? Id, string Name, bool IsAvailable = true);

/// <summary>One choice in a dropdown backed by an enum.</summary>
public sealed record ChoiceOption<T>(T Value, string Name);

/// <summary>
/// Backs the Settings page: where recordings go, what they sound like, and where their
/// details come from.
/// </summary>
/// <remarks>
/// <para>
/// Validation is <see cref="ObservableValidator"/>'s, which is <c>INotifyDataErrorInfo</c> —
/// plan §10 Phase 6 asks for inline errors rather than the predecessor's message boxes. The
/// rules mirror <see cref="Offstream.Core.Settings.SettingsStore.Validate"/> deliberately: the
/// page is the friendly half of the same contract that guards the file, so a value the page
/// accepts can always be written.
/// </para>
/// <para>
/// Fields that are naturally numbers are held as text. Binding a <c>TextBox</c> straight to an
/// <c>int</c> makes WPF's type converter the first thing to reject bad input, and its message
/// ("Value could not be converted") is neither translated nor about anything the user typed.
/// Parsing here keeps the wording ours.
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableValidator
{
    /// <summary>Bitrates offered for the lossy formats, in kbps.</summary>
    /// <remarks>
    /// The usual ladder rather than every value ffmpeg accepts. A stored bitrate outside it —
    /// hand-edited into the file — is added to the list rather than silently rounded.
    /// </remarks>
    private static readonly int[] StandardBitrates = [96, 128, 160, 192, 256, 320];

    /// <summary>
    /// The two resource strings with placeholders, parsed once.
    /// </summary>
    /// <remarks>
    /// Caching freezes the wording at first use, which is only correct because changing the UI
    /// language takes a restart (see the Advanced page's own note on that).
    /// </remarks>
    private static readonly CompositeFormat DeviceUnavailableFormat =
        CompositeFormat.Parse(Strings.SettingsDeviceUnavailable);

    private static readonly CompositeFormat MinimumLengthInvalidFormat =
        CompositeFormat.Parse(Strings.SettingsMinimumLengthInvalid);

    private readonly SettingsDocument _document;
    private readonly IAudioDeviceCatalog _catalog;
    private readonly IFolderPicker _folderPicker;

    /// <summary>Suppresses saving while the page is being filled from the document.</summary>
    private bool _loading;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(SettingsViewModel), nameof(ValidateOutputPath))]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private AudioDeviceOption? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SupportsBitrate))]
    private MediaFormat _format;

    [ObservableProperty]
    private int _bitrateKbps;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(SettingsViewModel), nameof(ValidateMinimumLength))]
    private string _minimumLengthSeconds = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpotifyProvider))]
    private MetadataProvider _provider;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(SettingsViewModel), nameof(ValidateSpotifyClientId))]
    private string _spotifyClientId = string.Empty;

    /// <summary>Why the last save was refused, or null. Shown as an inline bar, not a dialog.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveProblem))]
    private string? _saveProblem;

    public SettingsViewModel(
        SettingsDocument document,
        IAudioDeviceCatalog catalog,
        IFolderPicker folderPicker)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(folderPicker);

        _document = document;
        _catalog = catalog;
        _folderPicker = folderPicker;

        Formats =
        [
            .. EncodingProfiles.Known
                .OrderBy(profile => profile.Format)
                .Select(profile => new ChoiceOption<MediaFormat>(profile.Format, FormatName(profile))),
        ];

        Providers =
        [
            new(MetadataProvider.None, Strings.SettingsProviderNone),
            new(MetadataProvider.LastFm, Strings.SettingsProviderLastFm),
            new(MetadataProvider.Spotify, Strings.SettingsProviderSpotify),
        ];

        Load();
    }

    /// <summary>Render endpoints, refreshed on demand.</summary>
    public ObservableCollection<AudioDeviceOption> Devices { get; } = [];

    /// <summary>Bitrates offered, including a hand-edited one that is not on the ladder.</summary>
    public ObservableCollection<int> Bitrates { get; } = [];

    /// <summary>The output formats, in enum order so the list does not reshuffle per language.</summary>
    public IReadOnlyList<ChoiceOption<MediaFormat>> Formats { get; }

    /// <summary>Where track details are looked up.</summary>
    public IReadOnlyList<ChoiceOption<MetadataProvider>> Providers { get; }

    /// <summary>Where the settings file lives, shown so it can be found or deleted.</summary>
    public string SettingsPath => _document.Path;

    /// <summary>Whether the bitrate control means anything for the chosen format.</summary>
    /// <remarks>
    /// FLAC is lossless and WAV is uncompressed, so a bitrate box next to either is a control
    /// that does nothing. It stays visible and disabled rather than disappearing: a field that
    /// vanishes reads as a bug, and its absence is itself the explanation for those two formats.
    /// </remarks>
    public bool SupportsBitrate => EncodingProfiles.For(Format).SupportsBitrate;

    /// <summary>Whether the Spotify-specific fields apply.</summary>
    public bool IsSpotifyProvider => Provider == MetadataProvider.Spotify;

    /// <summary>Whether <see cref="SaveProblem"/> has anything worth showing.</summary>
    public bool HasSaveProblem => !string.IsNullOrWhiteSpace(SaveProblem);

    /// <summary>Fills the page from the document.</summary>
    private void Load()
    {
        _loading = true;

        try
        {
            var settings = _document.Current;

            OutputPath = settings.Output.Path ?? string.Empty;
            Format = settings.Output.Format;
            MinimumLengthSeconds = settings.Recording.MinimumLengthSeconds.ToString(CultureInfo.CurrentCulture);
            Provider = settings.Metadata.Provider;
            SpotifyClientId = settings.Metadata.SpotifyClientId ?? string.Empty;

            LoadBitrates(settings.Output.BitrateKbps);
            LoadDevices(settings.Recording.AudioEndpointDeviceId);
        }
        finally
        {
            _loading = false;
        }

        // Errors are shown from the first frame rather than waiting for the field to be touched:
        // a settings file that is already unusable is exactly what the page exists to fix.
        ValidateAllProperties();
    }

    private void LoadBitrates(int stored)
    {
        Bitrates.Clear();

        foreach (var rate in StandardBitrates.Append(stored).Distinct().Order()) Bitrates.Add(rate);

        BitrateKbps = stored;
    }

    /// <summary>
    /// Rebuilds the device list, keeping a stored device that is not currently connected.
    /// </summary>
    /// <remarks>
    /// Dropping it would silently rewrite the setting to "system default" the first time
    /// headphones were unplugged, and the user would find out by discovering a week of
    /// recordings made from the wrong endpoint.
    /// </remarks>
    private void LoadDevices(string? storedId)
    {
        var wanted = storedId ?? SelectedDevice?.Id;

        Devices.Clear();
        Devices.Add(new AudioDeviceOption(null, Strings.SettingsDeviceDefault));

        foreach (var device in _catalog.ListRender())
        {
            Devices.Add(new AudioDeviceOption(device.Id, device.Name));
        }

        if (wanted is not null && Devices.All(option => option.Id != wanted))
        {
            Devices.Add(new AudioDeviceOption(
                wanted,
                string.Format(CultureInfo.CurrentCulture, DeviceUnavailableFormat, wanted),
                IsAvailable: false));
        }

        SelectedDevice = Devices.FirstOrDefault(option => option.Id == wanted) ?? Devices[0];
    }

    [RelayCommand]
    private void Browse()
    {
        if (_folderPicker.Pick(OutputPath) is { } chosen) OutputPath = chosen;
    }

    /// <summary>
    /// Re-enumerates endpoints, for a device plugged in after the app started.
    /// </summary>
    /// <remarks>
    /// A button rather than a device-notification client: the list is only looked at while this
    /// page is open, and <c>IMMNotificationClient</c> is a COM callback contract to keep alive
    /// for the life of the app in order to refresh a dropdown nobody is looking at.
    /// </remarks>
    [RelayCommand]
    private void RefreshDevices()
    {
        _loading = true;

        try
        {
            LoadDevices(SelectedDevice?.Id);
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnOutputPathChanged(string value) => Persist();

    partial void OnSelectedDeviceChanged(AudioDeviceOption? value) => Persist();

    partial void OnFormatChanged(MediaFormat value) => Persist();

    partial void OnBitrateKbpsChanged(int value) => Persist();

    partial void OnMinimumLengthSecondsChanged(string value) => Persist();

    partial void OnProviderChanged(MetadataProvider value) => Persist();

    partial void OnSpotifyClientIdChanged(string value) => Persist();

    /// <summary>Writes the page's fields back, when every one of them is usable.</summary>
    /// <remarks>
    /// <see cref="ObservableValidator.ValidateAllProperties"/> rather than a trust in
    /// <see cref="ObservableValidator.HasErrors"/>: the generated setters validate the property
    /// that changed <em>after</em> raising its change notification, so reading <c>HasErrors</c>
    /// from a change handler would read the state before this edit. Revalidating is also what
    /// catches a field whose validity depends on another one — the Spotify Client ID becomes
    /// required the moment the provider changes, without the provider knowing that.
    /// </remarks>
    private void Persist()
    {
        if (_loading) return;

        ValidateAllProperties();

        if (HasErrors)
        {
            // The offending field says so itself. A second message repeating it at the top of
            // the page would be noise, and stale as soon as the field is corrected.
            SaveProblem = null;
            return;
        }

        SaveProblem = _document.Update(settings => settings with
        {
            Output = settings.Output with
            {
                Path = OutputPath.Trim(),
                Format = Format,
                BitrateKbps = BitrateKbps,
            },
            Recording = settings.Recording with
            {
                MinimumLengthSeconds = int.Parse(MinimumLengthSeconds, CultureInfo.CurrentCulture),
                AudioEndpointDeviceId = SelectedDevice?.Id,
            },
            Metadata = settings.Metadata with
            {
                Provider = Provider,
                SpotifyClientId = Trimmed(SpotifyClientId),
            },
        });
    }

    /// <summary>Empty text is "not set", which the schema spells as null.</summary>
    private static string? Trimmed(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Format names are not translated — they are what the file will be.</summary>
    private static string FormatName(EncodingProfile profile) =>
        $"{profile.Format.ToString().ToUpperInvariant()} (.{profile.Extension})";

    public static ValidationResult? ValidateOutputPath(string? value, ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(value)) return new ValidationResult(Strings.SettingsOutputPathRequired);

        var trimmed = value.Trim();

        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return new ValidationResult(Strings.SettingsOutputPathInvalid);

        // Rooted, not merely well-formed. A relative path resolves against the process's working
        // directory, which for a shortcut-launched app is wherever the shortcut points - so the
        // same setting would put recordings somewhere different depending on how it was started.
        return Path.IsPathFullyQualified(trimmed)
            ? ValidationResult.Success
            : new ValidationResult(Strings.SettingsOutputPathNotAbsolute);
    }

    public static ValidationResult? ValidateMinimumLength(string? value, ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // An hour, which is longer than any track and short enough that a stray digit is caught.
        const int maximum = 3600;

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var seconds)
               && seconds is >= 0 and <= maximum
            ? ValidationResult.Success
            : new ValidationResult(string.Format(
                CultureInfo.CurrentCulture, MinimumLengthInvalidFormat, maximum));
    }

    public static ValidationResult? ValidateSpotifyClientId(string? value, ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var viewModel = (SettingsViewModel)context.ObjectInstance;

        return !viewModel.IsSpotifyProvider || !string.IsNullOrWhiteSpace(value)
            ? ValidationResult.Success
            : new ValidationResult(Strings.SettingsClientIdRequired);
    }
}
