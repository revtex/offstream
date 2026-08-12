using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Offstream.App.Resources;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// Key parity between the neutral resources and the French satellite (plan §6 Phase 6 exit).
/// </summary>
/// <remarks>
/// <para>
/// Reads the compiled resource sets rather than the .resx XML on purpose. A missing translation
/// is only half the failure mode this guards against; the other half is a satellite assembly
/// that never got built or copied, which looks identical to a French user — English text, no
/// error. Loading <c>fr</c> with <c>tryParents: false</c> fails on both.
/// </para>
/// <para>
/// A key present only in French is a failure too, not a harmless leftover: nothing in the app
/// can reach it, because <c>x:Static</c> binds to the generated class and the generated class
/// is built from the neutral file.
/// </para>
/// </remarks>
public sealed class StringsTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    [Fact]
    public void French_HasEveryNeutralKey()
    {
        var neutral = KeysOf(CultureInfo.InvariantCulture);
        var french = KeysOf(French);

        Assert.Empty(neutral.Except(french, StringComparer.Ordinal));
    }

    [Fact]
    public void French_HasNoKeyTheAppCannotReach()
    {
        var neutral = KeysOf(CultureInfo.InvariantCulture);
        var french = KeysOf(French);

        Assert.Empty(french.Except(neutral, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("fr")]
    public void EveryValueIsPresent(string culture)
    {
        var resolved = CultureInfo.GetCultureInfo(culture);

        foreach (var key in KeysOf(CultureInfo.InvariantCulture))
        {
            var value = Strings.ResourceManager.GetString(key, resolved);

            Assert.False(
                string.IsNullOrWhiteSpace(value),
                $"'{key}' is empty in '{culture}'; an empty resource renders as a blank control.");
        }
    }

    /// <summary>
    /// The generated class is the only way XAML reaches a string, so a key that never made it
    /// into a property is a key the shell cannot show.
    /// </summary>
    [Fact]
    public void GeneratedClass_ExposesEveryNeutralKey()
    {
        var exposed = typeof(Strings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in KeysOf(CultureInfo.InvariantCulture))
        {
            Assert.Contains(key, exposed);
        }
    }

    /// <summary>
    /// Plan §0: resource keys are re-keyed for Offstream. The predecessor's were named after the
    /// WinForms control they filled (<c>lblSpy</c>, <c>tipStartSpying</c>), which is both an
    /// inherited identifier and a name that stops meaning anything once the control is gone.
    /// </summary>
    [Fact]
    public void Keys_CarryNoInheritedIdentifier()
    {
        string[] forbidden = ["EspionSpotify", "Spytify", "spy-spotify", "Spy"];

        foreach (var key in KeysOf(CultureInfo.InvariantCulture))
        {
            foreach (var bad in forbidden)
            {
                Assert.False(
                    key.Contains(bad, StringComparison.OrdinalIgnoreCase),
                    $"Resource key '{key}' contains '{bad}', inherited from the predecessor (plan §0).");
            }
        }
    }

    private static HashSet<string> KeysOf(CultureInfo culture)
    {
        // tryParents: false — with it on, a missing French satellite silently returns the
        // neutral set and this whole suite passes while French users see English.
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);

        Assert.NotNull(set);

        return [.. set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key)];
    }
}
