using System.Text.Json.Serialization;
using Offstream.Core.Settings;

namespace Offstream.Core.Tests.Settings;

/// <summary>
/// A source-generated context over <see cref="OffstreamSettings"/> configured exactly like the
/// production one, so <see cref="SettingsSchemaDefaultsTests"/> can deserialize directly.
/// </summary>
/// <remarks>
/// The real <c>SettingsJsonContext</c> is internal to <c>Offstream.Core</c> and reached only
/// through <see cref="SettingsStore"/>, which validates and would reject the deliberately
/// sparse JSON those tests rely on. Keep the options here identical to the production context's
/// — if they drift, this stops testing what it claims to.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(OffstreamSettings))]
internal sealed partial class TestSettingsJsonContext : JsonSerializerContext;
