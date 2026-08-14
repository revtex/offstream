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
using Offstream.Core.Spotify.Auth;
using Serilog;

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
    private readonly ISpotifyAccount _spotifyAccount;

    /// <summary>Suppresses saving while the page is being filled from the document.</summary>
    private bool _loading;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(SettingsViewModel), nameof(ValidateOutputPath))]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private AudioDeviceOption? _selectedDevice;

    /// <summary>
    /// What to say about VB-CABLE, beside the device it would be chosen from.
    /// </summary>
    /// <remarks>
    /// Beside the picker rather than in a section of its own, because the cable is only ever a
    /// device to record: it is not a mode or a feature to turn on, and its absence is only worth
    /// mentioning to somebody in the middle of choosing what to capture. Absence is stated, never
    /// installed — Offstream ships no vendor binaries, for the licence reasons in
    /// <see cref="VirtualCable"/>.
    /// </remarks>
    [ObservableProperty]
    private string _virtualCableStatus = string.Empty;

    /// <summary>Whether to offer the download link alongside <see cref="VirtualCableStatus"/>.</summary>
    [ObservableProperty]
    private bool _isVirtualCableMissing;

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
    [NotifyPropertyChangedFor(nameof(IsLastFmProvider))]
    [NotifyPropertyChangedFor(nameof(ProviderSummary))]
    [NotifyPropertyChangedFor(nameof(NeedsLastFmApiKey))]
    [NotifyCanExecuteChangedFor(nameof(SignInToSpotifyCommand))]
    private MetadataProvider _provider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsLastFmApiKey))]
    private string _lastFmApiKey = string.Empty;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(SettingsViewModel), nameof(ValidateSpotifyClientId))]
    [NotifyCanExecuteChangedFor(nameof(SignInToSpotifyCommand))]
    private string _spotifyClientId = string.Empty;

    /// <summary>Whether a Spotify refresh token is on file, i.e. whether sign-in has happened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpotifyAccountStatus))]
    [NotifyPropertyChangedFor(nameof(SpotifySignInLabel))]
    private bool _isSignedInToSpotify;

    /// <summary>Which Spotify account is signed in, when it could be established.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpotifyAccountName))]
    private string _spotifyAccountName = string.Empty;

    /// <summary>Set while the browser sign-in is open, so the button cannot be pressed twice.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInToSpotifyCommand))]
    private bool _isSigningInToSpotify;

    /// <summary>Why the last save was refused, or null. Shown as an inline bar, not a dialog.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveProblem))]
    private string? _saveProblem;

    public SettingsViewModel(
        SettingsDocument document,
        IAudioDeviceCatalog catalog,
        IFolderPicker folderPicker,
        ISpotifyAccount spotifyAccount)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(folderPicker);
        ArgumentNullException.ThrowIfNull(spotifyAccount);

        _document = document;
        _catalog = catalog;
        _folderPicker = folderPicker;
        _spotifyAccount = spotifyAccount;

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

    /// <summary>Where a user without the cable is sent, for the link's target.</summary>
    public static string VirtualCableUrl => VirtualCable.DownloadUrl;

    /// <summary>Whether the bitrate control means anything for the chosen format.</summary>
    /// <remarks>
    /// FLAC is lossless and WAV is uncompressed, so a bitrate box next to either is a control
    /// that does nothing. It stays visible and disabled rather than disappearing: a field that
    /// vanishes reads as a bug, and its absence is itself the explanation for those two formats.
    /// </remarks>
    public bool SupportsBitrate => EncodingProfiles.For(Format).SupportsBitrate;

    /// <summary>Whether the Spotify-specific fields apply.</summary>
    public bool IsSpotifyProvider => Provider == MetadataProvider.Spotify;

    /// <summary>Whether the Last.fm-specific fields apply.</summary>
    public bool IsLastFmProvider => Provider == MetadataProvider.LastFm;

    /// <summary>What the chosen provider actually contributes, in the tags.</summary>
    /// <remarks>
    /// <para>
    /// The dropdown names three providers and says nothing about how they differ, which leaves
    /// the one question worth asking here — what do I lose by picking this one — answerable only
    /// by recording something and running ffprobe over it. The differences are not small: only
    /// Spotify carries a release date, so <c>{year}</c> in a filename template is empty under
    /// either of the others, and that is invisible until a library comes out unsorted.
    /// </para>
    /// <para>
    /// Each line is written as what the provider <i>adds</i>, because none of them is the floor.
    /// Spotify reports artist, title, album, album artist and track number to the Windows media
    /// session, and Offstream tags those whatever is selected here — "Nothing" included, which
    /// is why that option no longer claims to use the window title alone.
    /// </para>
    /// </remarks>
    public string ProviderSummary => Provider switch
    {
        MetadataProvider.Spotify => Strings.SettingsProviderSummarySpotify,
        MetadataProvider.LastFm => Strings.SettingsProviderSummaryLastFm,
        _ => Strings.SettingsProviderSummaryNone,
    };

    /// <summary>
    /// Last.fm is chosen but has no key, so nothing will be tagged.
    /// </summary>
    /// <remarks>
    /// A warning rather than a validation error, unlike the Spotify Client ID next to it, and the
    /// difference is not an oversight. Last.fm is the default provider, so a fresh install has
    /// this state before the user has touched anything — and because <see cref="Persist"/>
    /// refuses the whole document when any field is in error, making it an error would mean a
    /// first run could not save its output folder until an unrelated API key was pasted in.
    /// Spotify is only ever reached by choosing it.
    /// </remarks>
    public bool NeedsLastFmApiKey => IsLastFmProvider && string.IsNullOrWhiteSpace(LastFmApiKey);

    /// <summary>Signed in, or not — the one thing about the account worth showing.</summary>
    public string SpotifyAccountStatus =>
        IsSignedInToSpotify ? Strings.SettingsSpotifySignedIn : Strings.SettingsSpotifyNotSignedIn;

    /// <summary>
    /// What the sign-in button says, which is not the same thing once there is an account.
    /// </summary>
    /// <remarks>
    /// It read "Sign in to Spotify" beside the words "Signed in", which is the button offering to
    /// do something already done. Pressing it does have a use — it is the only way to move the
    /// install to a different account, which is exactly what someone whose recordings are coming
    /// back untagged needs — so the label names that instead of being disabled or hidden.
    /// </remarks>
    public string SpotifySignInLabel =>
        IsSignedInToSpotify ? Strings.SettingsSpotifySwitchAccount : Strings.SettingsSpotifySignIn;

    /// <summary>Whether <see cref="SpotifyAccountName"/> has anything to show.</summary>
    public bool HasSpotifyAccountName => !string.IsNullOrWhiteSpace(SpotifyAccountName);

    /// <summary>
    /// Refreshes the line naming the signed-in account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Silent about its own failures by design — see
    /// <see cref="ISpotifyAccount.DescribeAccountAsync"/>. An existing install's refresh token
    /// predates the scopes this needs and will answer 403 until the user signs in again, and that
    /// is not a fault worth reporting beside an account that is still tagging recordings.
    /// </para>
    /// <para>
    /// The rotated token is persisted like everywhere else. Asking who is signed in redeems the
    /// refresh token, and Spotify rotates it on redemption, so dropping the replacement here would
    /// break the next lookup — the exact failure the recording path already guards against.
    /// </para>
    /// </remarks>
    private async Task RefreshSpotifyAccountNameAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSignedInToSpotify)
        {
            SpotifyAccountName = string.Empty;
            return;
        }

        try
        {
            var describing = await _spotifyAccount.DescribeAccountAsync(
                _document.Current.Metadata.SpotifyClientId,
                _document.Current.Metadata.SpotifyRefreshToken,
                rotated => _document.Update(settings => settings with
                {
                    Metadata = settings.Metadata with { SpotifyRefreshToken = rotated },
                }),
                cancellationToken);

            SpotifyAccountName = describing ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            // Navigating away. The label simply does not arrive.
        }
#pragma warning disable CA1031 // A label on a settings page is never worth surfacing a fault for.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Debug(ex, "Could not establish which Spotify account is signed in.");
            SpotifyAccountName = string.Empty;
        }
    }

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
            LastFmApiKey = settings.Metadata.LastFmApiKey ?? string.Empty;
            SpotifyClientId = settings.Metadata.SpotifyClientId ?? string.Empty;
            IsSignedInToSpotify = !string.IsNullOrWhiteSpace(settings.Metadata.SpotifyRefreshToken);

            // Not awaited: this is a network round trip, and the page must not wait on it to open.
            // The method swallows its own failures, so nothing is dropped by letting it run on.
            _ = RefreshSpotifyAccountNameAsync();

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

        var endpoints = _catalog.ListRender().ToList();

        foreach (var device in endpoints)
        {
            Devices.Add(new AudioDeviceOption(device.Id, device.Name));
        }

        // Off the same list the dropdown was built from, so the notice and the options a user is
        // choosing between can never disagree.
        IsVirtualCableMissing = !VirtualCable.DetectIn(endpoints).IsInstalled;

        VirtualCableStatus = IsVirtualCableMissing
            ? Strings.SettingsVirtualCableMissing
            : Strings.SettingsVirtualCableFound;

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

    partial void OnLastFmApiKeyChanged(string value) => Persist();

    partial void OnSpotifyClientIdChanged(string value) => Persist();

    /// <summary>Whether a browser sign-in can be started right now.</summary>
    private bool CanSignInToSpotify() =>
        IsSpotifyProvider && !IsSigningInToSpotify && !string.IsNullOrWhiteSpace(SpotifyClientId);

    /// <summary>
    /// Runs the PKCE sign-in in the user's browser and stores the refresh token it produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes the Spotify provider work at all. The Client ID field alone identifies
    /// an app; it grants nothing. Until this has run there is no token for the recording session
    /// to present, so Spotify was selectable but could never tag anything.
    /// </para>
    /// <para>
    /// The token is handed straight to <see cref="SettingsDocument"/>, which runs it through
    /// DPAPI on the way to disk — it is never held in a property, because a property is bindable
    /// and a bindable secret ends up on screen sooner or later.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanSignInToSpotify))]
    private async Task SignInToSpotifyAsync(CancellationToken cancellationToken)
    {
        IsSigningInToSpotify = true;
        SaveProblem = null;

        try
        {
            var refreshToken = await _spotifyAccount.SignInAsync(SpotifyClientId.Trim(), cancellationToken);

            SaveProblem = _document.Update(settings => settings with
            {
                Metadata = settings.Metadata with { SpotifyRefreshToken = refreshToken },
            });

            IsSignedInToSpotify = SaveProblem is null;

            // The sign-in that just ran is the one that carries the scopes this needs, so this is
            // the moment the account line can first be filled in on an install that predates them.
            await RefreshSpotifyAccountNameAsync(cancellationToken);
        }
        catch (SpotifyAuthException ex)
        {
            // Shown on the page rather than logged and forgotten: the user is standing at the
            // browser waiting to find out whether it worked.
            Log.Warning(ex, "Signing in to Spotify did not complete.");
            SaveProblem = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // Navigating away or closing the window while the browser is open.
        }
        finally
        {
            IsSigningInToSpotify = false;
        }
    }

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
                LastFmApiKey = Trimmed(LastFmApiKey),
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
