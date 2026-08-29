using System.Text.Json;
using Offstream.Core.Metadata;
using Offstream.Core.Naming;
using Offstream.Core.Recording;
using Offstream.Core.Settings;
using Xunit;

namespace Offstream.Core.Tests.Settings;

/// <summary>
/// Guards the reason every settings record uses primary-constructor parameter defaults instead
/// of the more natural <c>{ get; init; } = value;</c>.
/// </summary>
/// <remarks>
/// <para>
/// System.Text.Json's <em>source generator</em> does not run property initializers for
/// properties missing from the JSON: with an initializer, an omitted <c>bitrateKbps</c>
/// deserializes as <c>0</c>, not <c>320</c>. Reflection-based deserialization does honour them,
/// so this is invisible in a REPL and invisible in code review — it only shows up as a settings
/// file that quietly loads as zeroes.
/// </para>
/// <para>
/// These tests deserialize deliberately sparse JSON straight through the real context. Anyone
/// refactoring these records back to property initializers will fail here rather than shipping
/// a settings layer that silently discards defaults.
/// </para>
/// </remarks>
public sealed class SettingsSchemaDefaultsTests
{
    private static OffstreamSettings Deserialize(string json) =>
        JsonSerializer.Deserialize(json, TestSettingsJsonContext.Default.OffstreamSettings)!;

    [Fact]
    public void OmittedScalars_KeepTheirDefaults()
    {
        var settings = Deserialize("""{"schemaVersion": 1, "output": {"format": "Wav"}}""");

        Assert.Equal(MediaFormat.Wav, settings.Output.Format);
        Assert.Equal(320, settings.Output.BitrateKbps);
        Assert.Equal(FileNameTemplate.Default, settings.Output.Template);
        Assert.Equal(1, settings.Output.CurrentFileCounter);
        Assert.Equal(ExistingFilePolicy.Skip, settings.Output.ExistingFilePolicy);
    }

    [Fact]
    public void OmittedBooleans_KeepTheirDefaultsIncludingTrueOnes()
    {
        var settings = Deserialize("""{"schemaVersion": 1, "recording": {"minimumLengthSeconds": 45}}""");

        Assert.Equal(45, settings.Recording.MinimumLengthSeconds);
        Assert.True(settings.App.MinimizeToTray);

        // The strictest selection is both the default and the zero value, so this one would
        // survive a zero-value default. minimizeToTray above is the case that would not.
        Assert.Equal(RecordSelection.KnownTracksOnly, settings.Recording.RecordSelection);
    }

    [Fact]
    public void OmittedSections_AreNeverNull()
    {
        var settings = Deserialize("""{"schemaVersion": 1}""");

        Assert.NotNull(settings.Output);
        Assert.NotNull(settings.Recording);
        Assert.NotNull(settings.Metadata);
        Assert.NotNull(settings.App);
    }

    [Fact]
    public void ExplicitlyNullSections_AreNeverNull()
    {
        var settings = Deserialize(
            """{"schemaVersion": 1, "output": null, "recording": null, "metadata": null, "app": null}""");

        Assert.NotNull(settings.Output);
        Assert.NotNull(settings.Recording);
        Assert.NotNull(settings.Metadata);
        Assert.NotNull(settings.App);
    }

    [Fact]
    public void OmittedEnums_KeepTheirDefaults()
    {
        var settings = Deserialize("""{"schemaVersion": 1, "metadata": {"spotifyClientId": "id"}}""");

        Assert.Equal(MetadataProvider.LastFm, settings.Metadata.Provider);
        Assert.Equal("id", settings.Metadata.SpotifyClientId);
    }

    /// <summary>
    /// The schema version itself defaults, so a file that omits it is read as the current
    /// version rather than as 0 — which would otherwise fail the version check with a confusing
    /// "schemaVersion 0" message.
    /// </summary>
    [Fact]
    public void OmittedSchemaVersion_ReadsAsCurrent()
    {
        Assert.Equal(OffstreamSettings.CurrentSchemaVersion, Deserialize("{}").SchemaVersion);
    }
}
