using Offstream.App.Services;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// The licence text the app shows, and that it is the same text the repository holds.
/// </summary>
/// <remarks>
/// These are licence obligations rather than features: the MIT licence inherited from the
/// predecessor requires its copyright notice travel with the software, and releases bundle an
/// LGPL ffmpeg. Both are met by <c>LICENSE</c> and <c>NOTICE</c> being embedded in the
/// executable — which is exactly the kind of build-time wiring that goes missing in a project
/// file edit and fails silently, because a missing licence breaks nothing a user can see.
/// </remarks>
public sealed class ThirdPartyNoticesTests
{
    [Fact]
    public void Text_IsTheRepositoryLicenceAndNotice()
    {
        var root = RepositoryRoot();

        var license = Normalise(File.ReadAllText(Path.Combine(root, "LICENSE")));
        var notice = Normalise(File.ReadAllText(Path.Combine(root, "NOTICE")));

        var shown = Normalise(ThirdPartyNotices.Text);

        // Contains rather than equals: the window joins the two with a blank line between them,
        // and pinning the exact joining would fail on a formatting change that breaks nothing.
        Assert.Contains(license.TrimEnd(), shown, StringComparison.Ordinal);
        Assert.Contains(notice.TrimEnd(), shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The four obligations, named. Each is here because dropping it would be a licence breach
    /// that compiles, and none of them is visible in a passing build otherwise.
    /// </summary>
    [Theory]
    [InlineData("MIT")]           // Offstream's own licence, and the predecessor's.
    [InlineData("ffmpeg")]        // Bundled, LGPL, with a source offer.
    [InlineData("LGPL-3.0")]      // Which LGPL the bundled build is under.
    [InlineData("TagLibSharp")]   // Linked, LGPL-2.1-only.
    [InlineData("VB-CABLE")]      // Not bundled, but named and credited.
    public void Text_CarriesEveryNoticeTheAppOwes(string expected) =>
        Assert.Contains(expected, ThirdPartyNotices.Text, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Version_IdentifiesTheBuild()
    {
        var version = ThirdPartyNotices.Version;

        Assert.False(string.IsNullOrWhiteSpace(version));

        // The SDK appends the full 40-character commit hash; the displayed form cuts it to seven
        // so the line stays readable next to a label. A regression here is a wall of hex.
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            Assert.True(
                version.Length - plus - 1 <= 7,
                $"'{version}' carries an untrimmed commit hash.");
        }
    }

    private static string Normalise(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Walks up from the test binaries until the solution file appears.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Offstream.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
