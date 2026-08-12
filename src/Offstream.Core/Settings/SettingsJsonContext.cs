using System.Text.Json.Serialization;

namespace Offstream.Core.Settings;

/// <summary>
/// The source-generated serializer for <see cref="OffstreamSettings"/>.
/// </summary>
/// <remarks>
/// <para>
/// Source generation rather than reflection, per plan §8. It also keeps this working under a
/// trimmed publish — Offstream does not trim today (CLAUDE.md forbids it for the routing
/// interop's sake), but reflection-based serialization would be a second, quieter reason it
/// could never be turned on.
/// </para>
/// <para>
/// <b>The generator ignores property initializers, and that difference is silent.</b> Given
/// <c>public int BitrateKbps { get; init; } = 320;</c>, reflection-based deserialization of
/// JSON that omits the key yields 320; <em>this</em> generator yields 0. Same for reference
/// types, which come back null rather than their initialized value. Every record in
/// <see cref="OffstreamSettings"/> therefore declares its defaults as <b>primary constructor
/// parameter defaults</b>, which the generator does honour. Measured against this exact
/// configuration rather than taken on faith, because the failure mode is a settings file that
/// quietly loads as zeroes.
/// </para>
/// <para>
/// <b>Enums are written as strings</b> so <c>settings.json</c> stays human-editable and a
/// diff shows <c>"format": "Flac"</c> rather than <c>3</c> — the ordinal would also silently
/// change meaning if a member were ever inserted mid-enum.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(OffstreamSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
