using System.Text.Json.Serialization;
using Offstream.Core.Metadata;
using Offstream.Core.Naming;
using Offstream.Core.Recording;

namespace Offstream.Core.Settings;

/// <summary>
/// Everything Offstream persists, as it is shaped on disk.
/// </summary>
/// <remarks>
/// <para>
/// <b>Grouped sections, not the predecessor's flat keys.</b> Plan §6 designs this schema for
/// Offstream rather than transcribing <c>settings_*</c> / <c>advanced_*</c> prefixes out of the
/// old <c>user.config</c>. There is no importer and no legacy key vocabulary anywhere in here.
/// </para>
/// <para>
/// <b>Every record here uses a primary constructor with parameter defaults, and that is
/// load-bearing.</b> System.Text.Json's source generator does not run property initializers for
/// properties absent from the JSON — <c>{ get; init; } = 320;</c> silently yields <c>0</c> when
/// the file omits the key, not 320. Constructor parameter defaults <em>are</em> honoured, so
/// they are what this schema uses. Verified directly against the generator rather than assumed;
/// see <see cref="SettingsJsonContext"/>. Adding a property with a parameter default is
/// therefore backward-compatible and needs no <see cref="CurrentSchemaVersion"/> bump.
/// </para>
/// <para>
/// <b>Separate from <see cref="RecordingSettings"/> on purpose.</b> That type is the recording
/// pipeline's working view: flat, full of computed properties, and mutated while a session runs
/// (the file counter increments per recording). Persisting it directly would put derived values
/// like <c>orderNumberMax</c> in the file and force the on-disk shape to follow whatever the
/// pipeline finds convenient. This is the on-disk contract; <see cref="ToRecordingSettings"/>
/// and <see cref="CaptureRuntimeState"/> bridge the two.
/// </para>
/// <para>
/// <b>No log text lives here</b> (plan §6). Logs are Serilog's, under
/// <see cref="OffstreamPaths.LogDirectory"/>.
/// </para>
/// </remarks>
public sealed record OffstreamSettings(
    [property: JsonPropertyName("schemaVersion")]
    int SchemaVersion = OffstreamSettings.CurrentSchemaVersion,
    OutputSettings? Output = null,
    RecordingOptions? Recording = null,
    MetadataSettings? Metadata = null,
    AppSettings? App = null)
{
    /// <summary>
    /// The only schema version this build understands.
    /// </summary>
    /// <remarks>
    /// Bump when a change cannot be read by the previous shape. Adding an optional property
    /// with a constructor default does not need a bump — an older file simply omits it and the
    /// default applies.
    /// </remarks>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Where recordings go and what they are encoded as.</summary>
    /// <remarks>
    /// Nullable in the constructor so an omitted <c>"output"</c> section is representable, and
    /// non-null here so nothing downstream has to null-check a section. <c>SettingsStore</c>
    /// fills omitted sections on load; this initializer covers everyone else.
    /// </remarks>
    [JsonPropertyName("output")]
    public OutputSettings Output { get; init; } = Output ?? new OutputSettings();

    /// <inheritdoc cref="OutputSettings"/>
    [JsonPropertyName("recording")]
    public RecordingOptions Recording { get; init; } = Recording ?? new RecordingOptions();

    /// <inheritdoc cref="OutputSettings"/>
    [JsonPropertyName("metadata")]
    public MetadataSettings Metadata { get; init; } = Metadata ?? new MetadataSettings();

    /// <inheritdoc cref="OutputSettings"/>
    [JsonPropertyName("app")]
    public AppSettings App { get; init; } = App ?? new AppSettings();

    /// <summary>
    /// The defaults a first run starts from — chosen so the app is usable before the user
    /// opens Settings at all (plan §6).
    /// </summary>
    /// <remarks>
    /// <see cref="OutputSettings.Path"/> is filled in here rather than defaulted on the
    /// constructor because it is computed from the current user's Music folder, and a
    /// constructor default must be a compile-time constant.
    /// </remarks>
    public static OffstreamSettings CreateDefault() => new()
    {
        Output = new OutputSettings(Path: OffstreamPaths.DefaultOutputDirectory),
    };

    /// <summary>Projects the persisted shape onto the recording pipeline's working view.</summary>
    public RecordingSettings ToRecordingSettings() => new()
    {
        OutputPath = Output.Path,
        OutputTemplate = Output.Template,
        MediaFormat = Output.Format,
        BitrateKbps = Output.BitrateKbps,
        ExistingFilePolicy = Output.ExistingFilePolicy,
        MinimumRecordedLengthSeconds = Recording.MinimumLengthSeconds,
        MuteAdsEnabled = Recording.MuteAds,
        RecordEverythingEnabled = Recording.RecordEverything,
        RecordAdsEnabled = Recording.RecordAds,
        RecordingTimer = Recording.Timer,
        InternalOrderNumber = Output.CurrentFileCounter,
        OrderNumberInMediaTagEnabled = Metadata.WriteCounterToTrackNumber,
    };

    /// <summary>
    /// Copies back the one thing the pipeline changes while it runs: the file counter.
    /// </summary>
    /// <remarks>
    /// <see cref="RecordingSession"/> increments it per saved recording, so a session that ends
    /// without this being persisted would restart numbering and overwrite files on the next run.
    /// </remarks>
    public OffstreamSettings CaptureRuntimeState(RecordingSettings runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        return this with { Output = Output with { CurrentFileCounter = runtime.InternalOrderNumber } };
    }
}

/// <summary>Where recordings go and what they are encoded as.</summary>
/// <param name="Path">
/// Output root. Null means "not configured" and fails validation — <see cref="OffstreamSettings.CreateDefault"/>
/// supplies the real default, since it depends on the current user's profile.
/// </param>
/// <param name="Template">Filename template; see <see cref="FileNameTemplate"/>.</param>
/// <param name="BitrateKbps">Target bitrate for lossy formats; ignored by FLAC and WAV.</param>
/// <param name="CurrentFileCounter">
/// The running counter behind the <c>{count}</c> template token, persisted so numbering
/// continues across restarts rather than overwriting yesterday's files.
/// </param>
public sealed record OutputSettings(
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("template")] string Template = FileNameTemplate.Default,
    [property: JsonPropertyName("format")] MediaFormat Format = MediaFormat.Mp3,
    [property: JsonPropertyName("bitrateKbps")] int BitrateKbps = 320,
    [property: JsonPropertyName("existingFilePolicy")] ExistingFilePolicy ExistingFilePolicy = ExistingFilePolicy.Skip,
    [property: JsonPropertyName("currentFileCounter")] int CurrentFileCounter = 1);

/// <summary>What gets recorded, and for how long.</summary>
/// <param name="MinimumLengthSeconds">Recordings shorter than this are discarded.</param>
/// <param name="MuteAds">Mute Spotify's advertisements instead of recording them.</param>
/// <param name="RecordEverything">
/// Record anything that plays, including titles with no "artist - title" shape.
/// </param>
/// <param name="RecordAds">Include advertisements when <paramref name="RecordEverything"/> is on.</param>
/// <param name="Timer">Stop after this long, as six digits <c>hhmmss</c>; "000000" disables it.</param>
/// <param name="AudioEndpointDeviceId">Render endpoint to capture, or null for the system default.</param>
public sealed record RecordingOptions(
    [property: JsonPropertyName("minimumLengthSeconds")] int MinimumLengthSeconds = 30,
    [property: JsonPropertyName("muteAds")] bool MuteAds = true,
    [property: JsonPropertyName("recordEverything")] bool RecordEverything = false,
    [property: JsonPropertyName("recordAds")] bool RecordAds = false,
    [property: JsonPropertyName("timer")] string? Timer = null,
    [property: JsonPropertyName("audioEndpointDeviceId")] string? AudioEndpointDeviceId = null);

/// <summary>Where track metadata comes from, and what is written into tags.</summary>
/// <param name="SpotifyClientId">
/// The Client ID from the user's own Spotify Developer Dashboard app. Not a secret, and
/// deliberately not protected: a PKCE public client's ID is sent in the clear on every
/// authorize request and is not a credential on its own.
/// </param>
/// <param name="SpotifyRefreshToken">
/// The Spotify refresh token, DPAPI-protected on the way to disk.
/// <b>This is where plan §6's "client secret" protection actually applies.</b> That plan
/// predates the Phase 4 decision to use PKCE, which has no client secret at all — a public
/// desktop app could never keep one confidential regardless of how it was stored. What PKCE
/// does produce is a long-lived refresh token granting API access on the user's behalf, so that
/// is what <see cref="SettingsStore"/> runs through <see cref="ISecretProtector"/> before
/// writing. Never write a plaintext token here directly; go through the store.
/// </param>
/// <param name="LastFmApiKey">
/// The user's own Last.fm API key, from https://www.last.fm/api/account/create. Not a secret in
/// the DPAPI sense — it is sent as a query parameter on every request — but it is per-user, and
/// deliberately so: the predecessor shipped three of its own keys hard-coded in its source and
/// picked one at random per run. Offstream does not borrow another project's credentials, and a
/// key that belongs to the user cannot be revoked or rate-limited out from under them by someone
/// else's traffic.
/// </param>
/// <param name="WriteCounterToTrackNumber">
/// Write the file counter into the track-number tag as well as the name.
/// </param>
public sealed record MetadataSettings(
    [property: JsonPropertyName("provider")] MetadataProvider Provider = MetadataProvider.LastFm,
    [property: JsonPropertyName("lastFmApiKey")] string? LastFmApiKey = null,
    [property: JsonPropertyName("spotifyClientId")] string? SpotifyClientId = null,
    [property: JsonPropertyName("spotifyRefreshToken")] string? SpotifyRefreshToken = null,
    [property: JsonPropertyName("writeCounterToTrackNumber")] bool WriteCounterToTrackNumber = false);

/// <summary>Shell behaviour that is not about recording.</summary>
/// <param name="Language">UI language as a culture name ("en", "fr"), or null to follow the system.</param>
/// <param name="FfmpegPath">
/// An explicit ffmpeg path, overriding the bundled copy and <c>PATH</c>. Resolution order, and
/// why a wrong value here is an error rather than a fallback, are in
/// <see cref="Encoding.FFmpegLocator"/>.
/// </param>
public sealed record AppSettings(
    [property: JsonPropertyName("minimizeToTray")] bool MinimizeToTray = true,
    [property: JsonPropertyName("language")] string? Language = null,
    [property: JsonPropertyName("ffmpegPath")] string? FfmpegPath = null);
