using Offstream.Core;
using Xunit;

namespace Offstream.Core.Tests;

/// <summary>
/// The rules behind <see cref="OffstreamPaths.AppData"/>.
/// </summary>
/// <remarks>
/// Exercised through the internal overload rather than the property. The property resolves the
/// environment once, in a static initialiser, so a test that set the variable would be testing
/// whichever value happened to win the race to first use — and would leak that answer into
/// every other test in the assembly.
/// </remarks>
public sealed class OffstreamPathsTests
{
    private const string Roaming = @"C:\Users\someone\AppData\Roaming";

    [Fact]
    public void ResolveAppData_WithNoOverride_UsesAppDataSubfolder()
    {
        var resolved = OffstreamPaths.ResolveAppData(home: null, Roaming);

        Assert.Equal(Path.Combine(Roaming, "Offstream"), resolved);
    }

    [Fact]
    public void ResolveAppData_WithAbsoluteOverride_UsesItVerbatim()
    {
        var resolved = OffstreamPaths.ResolveAppData(@"D:\offstream-test-run", Roaming);

        Assert.Equal(@"D:\offstream-test-run", resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // Relative paths are refused rather than resolved: honouring one would put settings
    // wherever the app was launched from, which reads to a user as settings resetting at random.
    [InlineData(@"relative\path")]
    [InlineData("offstream")]
    public void ResolveAppData_WithUnusableOverride_FallsBackToAppData(string home)
    {
        var resolved = OffstreamPaths.ResolveAppData(home, Roaming);

        Assert.Equal(Path.Combine(Roaming, "Offstream"), resolved);
    }

    [Fact]
    public void InstanceMutex_IsScopedToTheLogonSession()
    {
        // Global\ would let the first user to log in lock every other user out of the app,
        // with no way to signal them why. See the remarks on the property.
        Assert.StartsWith(@"Local\", OffstreamPaths.InstanceMutex, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveInstanceMutex_WithNoOverride_IsThePlainName() =>
        Assert.Equal(@"Local\Offstream", OffstreamPaths.ResolveInstanceMutex(home: null));

    [Fact]
    public void ResolveInstanceMutex_WithAnOverride_IsItsOwnClaim()
    {
        var relocated = OffstreamPaths.ResolveInstanceMutex(@"D:\offstream-test-run");

        // Two installs with separate settings files are not the same application in the sense
        // the guard cares about - and this is what lets the UI suite drive a window while the
        // developer's own Offstream is running.
        Assert.NotEqual(@"Local\Offstream", relocated);
        Assert.StartsWith(@"Local\Offstream.", relocated, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveInstanceMutex_IsStableForTheSameDirectory() =>
        // A name that changed between launches would let a portable install run twice over one
        // settings file, which is exactly what the guard exists to prevent.
        Assert.Equal(
            OffstreamPaths.ResolveInstanceMutex(@"D:\offstream-test-run"),
            OffstreamPaths.ResolveInstanceMutex(@"D:\offstream-test-run\"));

    [Fact]
    public void ResolveInstanceMutex_SeparatesDifferentDirectories() =>
        Assert.NotEqual(
            OffstreamPaths.ResolveInstanceMutex(@"D:\one"),
            OffstreamPaths.ResolveInstanceMutex(@"D:\two"));

    [Fact]
    public void ResolveInstanceMutex_NamesNothingTheKernelWouldRefuse()
    {
        var name = OffstreamPaths.ResolveInstanceMutex(@"D:\offstream-test-run");

        // Everything after the namespace prefix has to be backslash-free and inside the 260
        // character cap, so the digest is not decoration.
        Assert.DoesNotContain(@"\", name[@"Local\".Length..], StringComparison.Ordinal);
        Assert.True(name.Length <= 260);
    }
}
