using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using Offstream.Core.Metadata;
using Offstream.Core.Recording;
using Offstream.Core.Settings;
using Xunit;

namespace Offstream.Core.Tests.Settings;

/// <summary>
/// Regression suite 4 (plan §9.2): settings round-trip, validation, schema versioning, secret
/// protection, and the atomic write.
/// </summary>
/// <remarks>
/// This suite replaces the migration suite the plan dropped with §6 — there is no importer, so
/// what needs guarding is the new schema's own behaviour rather than a translation from the
/// predecessor's keys.
/// </remarks>
public sealed class SettingsStoreTests
{
    private const string SettingsPath = @"C:\appdata\Offstream\settings.json";

    /// <summary>
    /// Reversible, inspectable, and nothing like real DPAPI — the point is to prove the store
    /// protects and reveals at the right moments, which a real keystore would only obscure.
    /// <see cref="DpapiSecretProtectorTests"/> covers the actual DPAPI implementation.
    /// </summary>
    private sealed class ReversibleProtector : ISecretProtector
    {
        public const string Prefix = "protected:";

        public string Protect(string plaintext) => Prefix + plaintext;

        public string? Unprotect(string protectedValue) =>
            protectedValue.StartsWith(Prefix, StringComparison.Ordinal)
                ? protectedValue[Prefix.Length..]
                : null;
    }

    private static (SettingsStore Store, MockFileSystem FileSystem) Build(ISecretProtector? protector = null)
    {
        var fileSystem = new MockFileSystem();
        return (new SettingsStore(fileSystem, protector ?? new ReversibleProtector(), SettingsPath), fileSystem);
    }

    private static OffstreamSettings Customised() => OffstreamSettings.CreateDefault() with
    {
        Output = new OutputSettings
        {
            Path = @"D:\Recordings",
            Template = @"{artist}\{album}\{count:000} {title}",
            Format = MediaFormat.Flac,
            BitrateKbps = 192,
            ExistingFilePolicy = ExistingFilePolicy.Duplicate,
            CurrentFileCounter = 42,
        },
        Recording = new RecordingOptions
        {
            MinimumLengthSeconds = 15,
            MuteAds = false,
            RecordEverything = true,
            RecordAds = true,
            Timer = "013000",
            AudioEndpointDeviceId = "{0.0.0.00000000}.{device-id}",
        },
        Metadata = new MetadataSettings
        {
            Provider = MetadataProvider.Spotify,
            LastFmApiKey = "last-fm-api-key",
            SpotifyClientId = "client-id",
            SpotifyRefreshToken = "the-refresh-token",
            WriteCounterToTrackNumber = true,
        },
        App = new AppSettings { MinimizeToTray = false, Language = "fr", FfmpegPath = @"D:\tools\ffmpeg.exe" },
    };

    [Fact]
    public void Load_WithNoFile_ReturnsFirstRunDefaults()
    {
        var (store, _) = Build();

        var settings = store.Load();

        Assert.False(store.Exists);
        Assert.Equal(OffstreamSettings.CreateDefault(), settings);
    }

    /// <summary>The whole point of §6's first-run design: usable before Settings is ever opened.</summary>
    [Fact]
    public void Defaults_AreUsableWithoutAnyUserInput()
    {
        var defaults = OffstreamSettings.CreateDefault();

        Assert.Empty(SettingsStore.Validate(defaults));
        Assert.False(string.IsNullOrWhiteSpace(defaults.Output.Path));
        Assert.Equal(MediaFormat.Mp3, defaults.Output.Format);
        Assert.Equal(1, defaults.Output.CurrentFileCounter);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEverySetting()
    {
        var (store, _) = Build();
        var original = Customised();

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(original, loaded);
    }

    [Fact]
    public void Save_WritesGroupedSectionsAndReadableEnums()
    {
        var (store, fileSystem) = Build();

        store.Save(Customised());
        var json = fileSystem.File.ReadAllText(SettingsPath);

        Assert.Contains("\"output\"", json, StringComparison.Ordinal);
        Assert.Contains("\"recording\"", json, StringComparison.Ordinal);
        Assert.Contains("\"metadata\"", json, StringComparison.Ordinal);
        Assert.Contains("\"app\"", json, StringComparison.Ordinal);

        // Names, not ordinals: an inserted enum member must not silently change what a saved
        // file means.
        Assert.Contains("\"Flac\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Duplicate\"", json, StringComparison.Ordinal);
    }

    /// <summary>Plan §0: no identifier inherited from the predecessor, including in the file it writes.</summary>
    [Fact]
    public void Save_WritesNoInheritedKeyNames()
    {
        var (store, fileSystem) = Build();

        store.Save(Customised());
        var json = fileSystem.File.ReadAllText(SettingsPath);

        foreach (var inherited in new[] { "settings_", "advanced_", "EspionSpotify", "Spytify" })
        {
            Assert.DoesNotContain(inherited, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Save_ProtectsTheRefreshTokenButNotTheClientId()
    {
        var (store, fileSystem) = Build();

        store.Save(Customised());
        var json = fileSystem.File.ReadAllText(SettingsPath);

        // Checked as a whole JSON value: the protected form legitimately *contains* the
        // plaintext as a suffix, so a bare substring check would pass on a plaintext write too.
        Assert.DoesNotContain("\"the-refresh-token\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"{ReversibleProtector.Prefix}the-refresh-token\"", json, StringComparison.Ordinal);

        // The Client ID of a PKCE public client is not a secret and is deliberately readable.
        Assert.Contains("client-id", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RevealsTheProtectedRefreshToken()
    {
        var (store, _) = Build();

        store.Save(Customised());

        Assert.Equal("the-refresh-token", store.Load().Metadata.SpotifyRefreshToken);
    }

    /// <summary>
    /// A token protected by a different Windows user, or on a different machine, will not
    /// decrypt. That costs one browser sign-in; refusing to load settings at all would cost the
    /// user every other preference they have.
    /// </summary>
    [Fact]
    public void Load_WithAnUndecryptableToken_KeepsEveryOtherSetting()
    {
        var (store, fileSystem) = Build();

        store.Save(Customised());

        var json = fileSystem.File.ReadAllText(SettingsPath)
            .Replace(ReversibleProtector.Prefix + "the-refresh-token", "not-decryptable", StringComparison.Ordinal);
        fileSystem.File.WriteAllText(SettingsPath, json);

        var loaded = store.Load();

        Assert.Null(loaded.Metadata.SpotifyRefreshToken);
        Assert.Equal(@"D:\Recordings", loaded.Output.Path);
        Assert.Equal(MetadataProvider.Spotify, loaded.Metadata.Provider);
    }

    [Fact]
    public void Load_WithMalformedJson_ThrowsSomethingActionable()
    {
        var (store, fileSystem) = Build();
        fileSystem.AddFile(SettingsPath, new MockFileData("{ \"output\": { not json"));

        var exception = Assert.Throws<SettingsException>(store.Load);

        Assert.Contains(SettingsPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("delete the file", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public void Load_WithAnEmptyFile_ThrowsRatherThanReturningNull()
    {
        var (store, fileSystem) = Build();
        fileSystem.AddFile(SettingsPath, new MockFileData("null"));

        Assert.Throws<SettingsException>(store.Load);
    }

    /// <summary>
    /// A file from a newer build may use fields this one would drop on the next save, so
    /// guessing is worse than stopping (plan §10 Phase 5: "unknown schemaVersion fails loudly").
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void Load_WithAnUnknownSchemaVersion_FailsLoudly(int version)
    {
        var (store, fileSystem) = Build();
        fileSystem.AddFile(SettingsPath, new MockFileData($$"""{"schemaVersion": {{version}}}"""));

        var exception = Assert.Throws<SettingsException>(store.Load);

        Assert.Contains($"schemaVersion {version}", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"version {OffstreamSettings.CurrentSchemaVersion}", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adding an optional property must not need a schema bump: an older file simply omits it
    /// and the default applies.
    /// </summary>
    /// <remarks>
    /// This is not free. System.Text.Json's source generator does <em>not</em> run property
    /// initializers for properties missing from the JSON, so an omitted section arrives as null
    /// however the record declares it — which crashed on validation until
    /// <c>SettingsStore.FillOmittedSections</c> was added. A hand-pruned settings file is
    /// exactly how a user would hit that.
    /// </remarks>
    [Theory]
    [InlineData("""{"schemaVersion": 1}""")]
    [InlineData("""{"schemaVersion": 1, "output": null, "recording": null, "metadata": null, "app": null}""")]
    [InlineData("""{"schemaVersion": 1, "output": {"format": "Wav"}}""")]
    public void Load_WithSectionsOmittedOrNull_FillsInDefaults(string json)
    {
        var (store, fileSystem) = Build();
        fileSystem.AddFile(SettingsPath, new MockFileData(json));

        var loaded = store.Load();

        Assert.NotNull(loaded.Output);
        Assert.NotNull(loaded.Recording);
        Assert.NotNull(loaded.Metadata);
        Assert.NotNull(loaded.App);
        Assert.Equal(OffstreamPaths.DefaultOutputDirectory, loaded.Output.Path);
        Assert.Equal(30, loaded.Recording.MinimumLengthSeconds);
    }

    /// <summary>A section present but partial keeps its stated value and defaults the rest.</summary>
    [Fact]
    public void Load_WithAPartialSection_KeepsWhatIsThereAndDefaultsTheRest()
    {
        var (store, fileSystem) = Build();
        fileSystem.AddFile(SettingsPath, new MockFileData("""{"schemaVersion": 1, "output": {"format": "Wav"}}"""));

        var loaded = store.Load();

        Assert.Equal(MediaFormat.Wav, loaded.Output.Format);
        Assert.Equal(320, loaded.Output.BitrateKbps);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(321)]
    [InlineData(-1)]
    public void Validate_RejectsAnOutOfRangeBitrate(int bitrate)
    {
        var defaults = OffstreamSettings.CreateDefault();
        var settings = defaults with { Output = defaults.Output with { BitrateKbps = bitrate } };

        Assert.Contains(
            SettingsStore.Validate(settings), p => p.Contains("bitrateKbps", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsAnEmptyOutputPath()
    {
        var defaults = OffstreamSettings.CreateDefault();
        var settings = defaults with { Output = defaults.Output with { Path = "  " } };

        Assert.Contains(SettingsStore.Validate(settings), p => p.Contains("output.path", StringComparison.Ordinal));
    }

    /// <summary>
    /// The reason from <see cref="Naming.FileNameTemplate.Validate"/> is passed through rather
    /// than flattened, so the user is told which token is wrong.
    /// </summary>
    [Fact]
    public void Validate_RejectsAnUnknownTemplateTokenAndSaysWhichOne()
    {
        var defaults = OffstreamSettings.CreateDefault();
        var settings = defaults with { Output = defaults.Output with { Template = "{artist} - {nonsense}" } };

        var problem = Assert.Single(SettingsStore.Validate(settings));

        Assert.Contains("output.template", problem, StringComparison.Ordinal);
        Assert.Contains("{nonsense}", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("01x000")]
    public void Validate_RejectsAMalformedTimer(string timer)
    {
        var defaults = OffstreamSettings.CreateDefault();
        var settings = defaults with { Recording = defaults.Recording with { Timer = timer } };

        Assert.Contains(SettingsStore.Validate(settings), p => p.Contains("recording.timer", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("000000")]
    [InlineData("013000")]
    public void Validate_AcceptsAnAbsentOrWellFormedTimer(string? timer)
    {
        var defaults = OffstreamSettings.CreateDefault();
        var settings = defaults with { Recording = defaults.Recording with { Timer = timer } };

        Assert.Empty(SettingsStore.Validate(settings));
    }

    [Fact]
    public void Validate_RejectsAnUndefinedEnumValue()
    {
        var defaults = OffstreamSettings.CreateDefault();
        var settings = defaults with { Output = defaults.Output with { Format = (MediaFormat)99 } };

        Assert.Contains(SettingsStore.Validate(settings), p => p.Contains("output.format", StringComparison.Ordinal));
    }

    [Fact]
    public void Save_RefusesInvalidSettingsRatherThanWritingThem()
    {
        var (store, fileSystem) = Build();
        var defaults = OffstreamSettings.CreateDefault();
        var invalid = defaults with { Output = defaults.Output with { BitrateKbps = 9999 } };

        var exception = Assert.Throws<SettingsException>(() => store.Save(invalid));

        Assert.Contains("bitrateKbps", exception.Message, StringComparison.Ordinal);
        Assert.False(fileSystem.File.Exists(SettingsPath));
    }

    [Fact]
    public void Load_WithInvalidValuesOnDisk_ThrowsAndNamesThemAll()
    {
        var (store, fileSystem) = Build();
        fileSystem.AddFile(SettingsPath, new MockFileData(
            """{"schemaVersion": 1, "output": {"path": "", "bitrateKbps": 5000}}"""));

        var exception = Assert.Throws<SettingsException>(store.Load);

        Assert.Contains("output.path", exception.Message, StringComparison.Ordinal);
        Assert.Contains("bitrateKbps", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        var (store, fileSystem) = Build();

        store.Save(Customised());

        Assert.DoesNotContain(fileSystem.AllFiles, f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The atomic-write guarantee: a crash between writing the temp file and moving it into
    /// place must leave the <em>previous</em> settings intact, not a truncated file.
    /// </summary>
    [Fact]
    public void Save_CrashingBeforeTheMove_LeavesThePreviousSettingsIntact()
    {
        var (store, fileSystem) = Build();

        store.Save(Customised());
        var before = fileSystem.File.ReadAllText(SettingsPath);

        // Stand in for the crash: the temp file lands, the move never happens. Its contents do
        // not matter — only that a stray .tmp cannot be mistaken for the real file.
        fileSystem.File.WriteAllText(SettingsPath + ".tmp", """{"schemaVersion": 1, "output": {"path": "E:\\Elsewhere"}}""");

        Assert.Equal(before, fileSystem.File.ReadAllText(SettingsPath));
        Assert.Equal(@"D:\Recordings", store.Load().Output.Path);
    }

    [Fact]
    public void Save_OverwritesAnExistingFile()
    {
        var (store, _) = Build();

        store.Save(Customised());
        store.Save(Customised() with { App = new AppSettings { Language = "en" } });

        Assert.Equal("en", store.Load().App.Language);
    }

    [Fact]
    public void LoadOrDefault_WithABrokenFile_ReturnsDefaultsAndExplainsWhy()
    {
        var (store, fileSystem) = Build();
        fileSystem.AddFile(SettingsPath, new MockFileData("{ broken"));

        var settings = store.LoadOrDefault(out var problem);

        Assert.Equal(OffstreamSettings.CreateDefault(), settings);
        Assert.NotNull(problem);
        Assert.Contains(SettingsPath, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadOrDefault_WithAGoodFile_ReportsNoProblem()
    {
        var (store, _) = Build();
        store.Save(Customised());

        var settings = store.LoadOrDefault(out var problem);

        Assert.Null(problem);
        Assert.Equal(@"D:\Recordings", settings.Output.Path);
    }
}
