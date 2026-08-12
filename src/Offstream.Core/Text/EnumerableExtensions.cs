namespace Offstream.Core.Text;

/// <summary>Statistics helpers used by silence analysis.</summary>
public static class EnumerableExtensions
{
    /// <summary>
    /// Median of a sequence. Even-length sequences average the two middle values.
    /// </summary>
    /// <exception cref="InvalidOperationException">The sequence is empty.</exception>
    public static double Median(this IEnumerable<double> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var sorted = source.Order().ToList();
        if (sorted.Count == 0) throw new InvalidOperationException("Cannot compute median for an empty set.");

        var middle = sorted.Count / 2;

        return sorted.Count % 2 == 0
            ? (sorted[middle] + sorted[middle - 1]) / 2
            : sorted[middle];
    }

    /// <inheritdoc cref="Median(IEnumerable{double})"/>
    public static double Median(this IEnumerable<int> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Select(value => (double)value).Median();
    }

    /// <inheritdoc cref="Median(IEnumerable{double})"/>
    public static double Median<T>(this IEnumerable<T> source, Func<T, double> selector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);

        return source.Select(selector).Median();
    }
}
