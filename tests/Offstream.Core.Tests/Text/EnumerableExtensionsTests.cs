using Offstream.Core.Text;
using Xunit;

namespace Offstream.Core.Tests.Text;

/// <summary>Ported from the reference suite's <c>LinqExtensionsTest</c>.</summary>
public sealed class EnumerableExtensionsTests
{
    [Fact]
    public void Median_ThrowsForEmptySequence()
    {
        var value = Array.Empty<double>();
        Assert.Throws<InvalidOperationException>(() => value.Median());
    }

    [Fact]
    public void Median_AveragesMiddlePairForEvenCount()
    {
        double[] value = [2.8, 1.4, 1.1, 0.8, -0.4, 1.1, 2.4, 7.77];
        Assert.Equal(1.25, value.Median());
    }

    [Fact]
    public void Median_ReturnsMiddleForOddCount()
    {
        double[] value = [5.5, 0.9, 1.1, 0.8, -0.4, 1.11, 0.004, 2.4, 7.77];
        Assert.Equal(1.1, value.Median());
    }

    [Fact]
    public void Median_WorksForIntegers()
    {
        int[] value = [5, 3, 6, 0, -1, 1, 0, 2, 7];
        Assert.Equal(2, value.Median());
    }

    [Fact]
    public void Median_WorksWithSelector()
    {
        var value = new[] { new Sample(5.5), new Sample(0.9), new Sample(1.6) };
        Assert.Equal(1.6, value.Median(x => x.Value));
    }

    private sealed record Sample(double Value);
}
