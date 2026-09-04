using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using Offstream.Core.Naming;

namespace Offstream.Core.Settings;

/// <summary>Settings on disk could not be read, or would not be valid if written.</summary>
public sealed class SettingsException : Exception
{
    public SettingsException()
    {
    }

    public SettingsException(string message) : base(message)
    {
    }

    public SettingsException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Reads and writes <c>%APPDATA%\Offstream\settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writes are atomic.</b> The file is written to a sibling temp file, flushed, and then
/// moved over the destination (plan §6). A crash or a pulled power cord mid-save therefore
/// leaves either the old file or the new one, never a half-written file — which for a settings
/// file is the difference between losing the last change and losing every preference the user
/// has.
/// </para>
/// <para>
/// <b>Loading never throws for a missing file, and always throws for a broken one.</b> Absent
/// means first run, which is a normal state with good defaults behind it (plan §6). Malformed
/// JSON, an unknown schema version, or values outside their valid range are all reported as a
/// <see cref="SettingsException"/> naming what is wrong, rather than being silently replaced
/// with defaults — quietly discarding a user's configuration because one field went bad is
/// worse than refusing to start.
/// </para>
/// </remarks>
public sealed class SettingsStore(
    IFileSystem fileSystem,
    ISecretProtector secretProtector,
    string? settingsPath = null)
{
    /// <summary>Where settings live.</summary>
    public string Path { get; } = settingsPath ?? OffstreamPaths.SettingsFile;

    /// <summary>Whether a settings file exists — false on a first run.</summary>
    public bool Exists => fileSystem.File.Exists(Path);

    /// <summary>
    /// Loads settings, or returns first-run defaults when no file exists.
    /// </summary>
    /// <exception cref="SettingsException">
    /// The file is malformed, written against an unknown schema version, or holds values
    /// outside their valid range. The message says which.
    /// </exception>
    public OffstreamSettings Load()
    {
        if (!Exists) return OffstreamSettings.CreateDefault();

        string json;

        try
        {
            json = fileSystem.File.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsException($"Could not read settings from '{Path}': {ex.Message}", ex);
        }

        OffstreamSettings? settings;

        try
        {
            settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.OffstreamSettings);
        }
        catch (JsonException ex)
        {
            throw new SettingsException(
                $"'{Path}' is not valid settings JSON (line {ex.LineNumber}, position {ex.BytePositionInLine}): " +
                $"{ex.Message} Fix or delete the file to start from defaults.",
                ex);
        }

        if (settings is null)
        {
            throw new SettingsException(
                $"'{Path}' contains no settings object. Fix or delete the file to start from defaults.");
        }

        if (settings.SchemaVersion != OffstreamSettings.CurrentSchemaVersion)
        {
            // Loudly, per plan §10 Phase 5. A file from a newer build may use fields this one
            // would drop on the next save, so guessing is worse than stopping.
            throw new SettingsException(
                $"'{Path}' has schemaVersion {settings.SchemaVersion}, but this build of Offstream " +
                $"understands only version {OffstreamSettings.CurrentSchemaVersion}. " +
                "It was most likely written by a newer version.");
        }

        settings = ApplyRuntimeDefaults(settings);

        var problems = Validate(settings);
        if (problems.Count > 0)
        {
            throw new SettingsException(
                $"'{Path}' has invalid settings:{Environment.NewLine}" +
                string.Join(Environment.NewLine, problems.Select(p => $"  - {p}")));
        }

        return Reveal(settings);
    }

    /// <summary>
    /// Loads settings, falling back to defaults when the file cannot be used.
    /// </summary>
    /// <remarks>
    /// For the shell's startup path, which must put a window on screen either way. The reason
    /// is handed back so it can be surfaced in the log and the UI rather than swallowed.
    /// </remarks>
    public OffstreamSettings LoadOrDefault(out string? problem)
    {
        try
        {
            problem = null;
            return Load();
        }
        catch (SettingsException ex)
        {
            problem = ex.Message;
            return OffstreamSettings.CreateDefault();
        }
    }

    /// <summary>Writes settings atomically, protecting secrets on the way out.</summary>
    /// <exception cref="SettingsException">The settings are invalid, or the write failed.</exception>
    public void Save(OffstreamSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var problems = Validate(settings);
        if (problems.Count > 0)
        {
            throw new SettingsException(
                $"Refusing to save invalid settings:{Environment.NewLine}" +
                string.Join(Environment.NewLine, problems.Select(p => $"  - {p}")));
        }

        var json = JsonSerializer.Serialize(Conceal(settings), SettingsJsonContext.Default.OffstreamSettings);

        var directory = fileSystem.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) fileSystem.Directory.CreateDirectory(directory);

        // Sibling, not %TEMP%: File.Move across volumes is a copy, which is not atomic and
        // would reintroduce exactly the torn-write window this exists to close.
        var temporaryPath = $"{Path}.tmp";

        try
        {
            fileSystem.File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            fileSystem.File.Move(temporaryPath, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemporary(temporaryPath);
            throw new SettingsException($"Could not save settings to '{Path}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Everything wrong with <paramref name="settings"/>, in terms a user could act on.
    /// </summary>
    /// <remarks>
    /// Exposed so a settings screen can validate as the user types (plan §10 Phase 6 wants
    /// inline validation, not modal dialogs) using the same rules that guard the file.
    /// </remarks>
    public static IReadOnlyList<string> Validate(OffstreamSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(settings.Output.Path))
            problems.Add("output.path must not be empty.");

        // FileNameTemplate.Validate already explains itself ("Unknown token(s): {foo}"), so its
        // reason is passed through rather than flattened into a generic "invalid template".
        if (FileNameTemplate.Validate(settings.Output.Template) is { } templateProblem)
            problems.Add($"output.template is not usable: {templateProblem}");

        if (!Enum.IsDefined(settings.Output.Format))
            problems.Add($"output.format '{settings.Output.Format}' is not a known media format.");

        // Bounded by what the encoders will actually accept rather than by taste: below 8 kbps
        // no lossy encoder here produces anything usable, and above 320 is past every profile's
        // ceiling in EncodingProfiles.
        if (settings.Output.BitrateKbps is < 8 or > 320)
            problems.Add($"output.bitrateKbps must be between 8 and 320; found {settings.Output.BitrateKbps}.");

        if (!Enum.IsDefined(settings.Output.BitrateMode))
            problems.Add($"output.bitrateMode '{settings.Output.BitrateMode}' is not a known bitrate mode.");

        if (!Enum.IsDefined(settings.Output.ExistingFilePolicy))
            problems.Add($"output.existingFilePolicy '{settings.Output.ExistingFilePolicy}' is not a known policy.");

        if (settings.Output.CurrentFileCounter < 1)
            problems.Add($"output.currentFileCounter must be 1 or greater; found {settings.Output.CurrentFileCounter}.");

        if (settings.Recording.MinimumLengthSeconds < 0)
            problems.Add(
                $"recording.minimumLengthSeconds must not be negative; found {settings.Recording.MinimumLengthSeconds}.");

        if (settings.Recording.Timer is { Length: > 0 } timer && !IsValidTimer(timer))
            problems.Add($"recording.timer '{timer}' must be six digits in hhmmss form.");

        if (!Enum.IsDefined(settings.Metadata.Provider))
            problems.Add($"metadata.provider '{settings.Metadata.Provider}' is not a known provider.");

        return problems;
    }

    /// <summary>
    /// Same six-digit <c>hhmmss</c> rule <see cref="RecordingSettings.HasRecordingTimerEnabled"/>
    /// applies, checked here so a bad value is rejected at the file boundary rather than
    /// silently behaving as "no timer" at runtime.
    /// </summary>
    private static bool IsValidTimer(string timer) =>
        timer.Length == 6 && timer.All(char.IsAsciiDigit);

    /// <summary>
    /// Supplies defaults that cannot be constructor parameter defaults because they are
    /// computed at runtime.
    /// </summary>
    /// <remarks>
    /// Only the output path qualifies: its default is derived from the current user's Music
    /// folder, and a C# parameter default must be a compile-time constant. Without this, one
    /// field would behave differently from every other — omit <c>bitrateKbps</c> and you get
    /// 320, but omit <c>path</c> and the file is rejected. <b>Null means "not specified"</b> and
    /// gets the default; an explicit <c>""</c> is a value the user did set, and still fails
    /// validation rather than being silently overridden.
    /// </remarks>
    private static OffstreamSettings ApplyRuntimeDefaults(OffstreamSettings settings) =>
        settings.Output.Path is null
            ? settings with
            {
                Output = settings.Output with { Path = OffstreamPaths.DefaultOutputDirectory },
            }
            : settings;

    /// <summary>Encrypts secrets for storage.</summary>
    private OffstreamSettings Conceal(OffstreamSettings settings) =>
        settings.Metadata.SpotifyRefreshToken is { Length: > 0 } token
            ? settings with
            {
                Metadata = settings.Metadata with { SpotifyRefreshToken = secretProtector.Protect(token) },
            }
            : settings;

    /// <summary>
    /// Decrypts secrets after loading. A token that will not decrypt becomes null — see
    /// <see cref="DpapiSecretProtector"/> for why that is a normal outcome and not an error.
    /// </summary>
    private OffstreamSettings Reveal(OffstreamSettings settings) =>
        settings.Metadata.SpotifyRefreshToken is { Length: > 0 } token
            ? settings with
            {
                Metadata = settings.Metadata with { SpotifyRefreshToken = secretProtector.Unprotect(token) },
            }
            : settings;

    private void TryDeleteTemporary(string temporaryPath)
    {
        try
        {
            if (fileSystem.File.Exists(temporaryPath)) fileSystem.File.Delete(temporaryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The save already failed; a leftover .tmp is not worth masking that with.
        }
    }
}
