using System.Globalization;
using System.Windows.Data;

namespace Offstream.App.Views.Converters;

/// <summary>
/// True when a bound enum equals the parameter — the binding a group of radio buttons needs.
/// </summary>
/// <remarks>
/// <para>
/// The asymmetry is the whole design. Converting forward answers "is this the selected tab?", so
/// every button in the group can watch one property. Converting back only reports the parameter
/// when the button was <i>checked</i>: unchecking is what happens to the outgoing button when
/// another is picked, and writing anything back for it would race the incoming button and settle
/// on whichever the group happened to update last.
/// </para>
/// <para>
/// <see cref="Binding.DoNothing"/> rather than a value is what makes that safe — it leaves the
/// source untouched instead of writing something the user did not choose.
/// </para>
/// </remarks>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && value.Equals(parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? parameter : Binding.DoNothing;
}
