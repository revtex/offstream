using System.Globalization;
using System.Windows.Data;

namespace Offstream.App.Views.Converters;

/// <summary>
/// Upper-cases a string for display — the section bands, and nothing else so far.
/// </summary>
/// <remarks>
/// Done here rather than in <c>Strings.resx</c> so the resources stay sentence case. They are read
/// by more than the bands (automation names, tooltips), and a translator handed <c>DÉTAILS DE LA
/// PISTE</c> has been told a typographic choice rather than a phrase. Culture-aware, because the
/// mapping is not: Turkish dotless i is the standard example, and it is one property away.
/// </remarks>
public sealed class UpperCaseConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? (culture ?? CultureInfo.CurrentCulture).TextInfo.ToUpper(text) : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
