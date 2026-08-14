using Offstream.Core.Naming;
using Xunit;

namespace Offstream.Core.Tests.Naming;

/// <summary>
/// The <c>\\?\</c> prefix rules. Pure string work, and worth pinning precisely: the prefix turns
/// off path normalisation, so applying it to the wrong shape of path changes meaning rather than
/// just capacity.
/// </summary>
public sealed class LongPathTests
{
    private static string PathOfLength(int length) =>
        @"C:\" + new string('a', Math.Max(length - 3, 0));

    /// <summary>A short path keeps its ordinary behaviour, normalisation included.</summary>
    [Theory]
    [InlineData(@"C:\music\track.mp3")]
    [InlineData(@"C:\a\b\c")]
    public void AShortPath_IsLeftAlone(string path) => Assert.Equal(path, LongPath.Extended(path));

    [Fact]
    public void APathBelowTheLegacyLimit_IsLeftAlone()
    {
        var path = PathOfLength(LongPath.LegacyMaxLength - 1);

        Assert.Equal(path, LongPath.Extended(path));
    }

    [Fact]
    public void APathAtTheLegacyLimit_IsPrefixed()
    {
        var extended = LongPath.Extended(PathOfLength(LongPath.LegacyMaxLength));

        Assert.StartsWith(@"\\?\C:\", extended, StringComparison.Ordinal);
    }

    /// <summary>Prefixing twice would produce a path naming a directory called "?".</summary>
    [Fact]
    public void AnAlreadyPrefixedPath_IsNotPrefixedAgain()
    {
        var already = @"\\?\C:\" + new string('a', LongPath.LegacyMaxLength);

        Assert.Equal(already, LongPath.Extended(already));
    }

    /// <summary>Device paths are a different namespace and mean something else entirely.</summary>
    [Fact]
    public void ADevicePath_IsLeftAlone()
    {
        var device = @"\\.\PIPE\" + new string('a', LongPath.LegacyMaxLength);

        Assert.Equal(device, LongPath.Extended(device));
    }

    /// <summary>A UNC path takes the UNC form, not a second pair of leading slashes.</summary>
    [Fact]
    public void AUncPath_TakesTheUncForm()
    {
        var unc = @"\\server\share\" + new string('a', LongPath.LegacyMaxLength);

        var extended = LongPath.Extended(unc);

        Assert.StartsWith(@"\\?\UNC\server\share\", extended, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\\?\\\", extended, StringComparison.Ordinal);
    }

    /// <summary>
    /// A relative path cannot be prefixed — the prefix stops Windows resolving it, so the result
    /// would name something that does not exist rather than the file the caller meant.
    /// </summary>
    [Fact]
    public void ARelativePath_IsLeftAlone()
    {
        var relative = @"music\" + new string('a', LongPath.LegacyMaxLength);

        Assert.Equal(relative, LongPath.Extended(relative));
    }

    /// <summary>
    /// Normalisation happens before the prefix goes on, because afterwards Windows will not do
    /// it: a surviving <c>..</c> becomes a literal directory name the filesystem rejects.
    /// </summary>
    [Fact]
    public void ATraversalSegment_IsResolvedBeforePrefixing()
    {
        var messy = @"C:\music\sub\..\" + new string('a', LongPath.LegacyMaxLength);

        var extended = LongPath.Extended(messy);

        Assert.StartsWith(@"\\?\C:\music\a", extended, StringComparison.Ordinal);
        Assert.DoesNotContain("..", extended, StringComparison.Ordinal);
    }

    /// <summary>Forward slashes stop being separators once prefixed, so they are converted first.</summary>
    [Fact]
    public void ForwardSlashes_AreConvertedBeforePrefixing()
    {
        var extended = LongPath.Extended("C:/music/" + new string('a', LongPath.LegacyMaxLength));

        Assert.StartsWith(@"\\?\C:\music\", extended, StringComparison.Ordinal);
        Assert.DoesNotContain("/", extended, StringComparison.Ordinal);
    }

    [Fact]
    public void Extended_RejectsNull() => Assert.Throws<ArgumentNullException>(() => LongPath.Extended(null!));

    /// <summary>
    /// The component limit is not lifted by the prefix. Documented here because it is the one
    /// people assume goes away, and the budgeting in OutputPaths depends on it not having.
    /// </summary>
    [Fact]
    public void TheComponentLimit_IsUnchangedByExtendedPaths()
    {
        Assert.Equal(255, LongPath.MaxComponentLength);
        Assert.True(LongPath.MaxLength > LongPath.LegacyMaxLength);
    }
}
