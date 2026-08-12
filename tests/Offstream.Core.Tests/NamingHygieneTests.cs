using System.Reflection;
using Xunit;

namespace Offstream.Core.Tests;

/// <summary>
/// Regression suite 7 (plan §9.2): Offstream owns every identifier.
/// </summary>
/// <remarks>
/// Code carries over from the predecessor; names never do (plan §0). This is the cheap
/// mechanical guard that stops an inherited identifier surviving a copy-paste — the single
/// failure mode the whole naming convention exists to prevent. It deliberately checks
/// compiled output and file names, not file *contents*: documentation is allowed to discuss
/// the predecessor by name, and does.
/// </remarks>
public sealed class NamingHygieneTests
{
    private static readonly string[] ForbiddenIdentifiers =
    [
        "EspionSpotify",
        "Spytify",
        "spy-spotify",
    ];

    public static TheoryData<Assembly> ProductAssemblies => new()
    {
        typeof(OffstreamPaths).Assembly,
        typeof(global::Offstream.App.App).Assembly,
    };

    [Theory]
    [MemberData(nameof(ProductAssemblies))]
    public void AssemblyNameIsOffstreamOwned(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        Assert.NotNull(name);
        AssertClean(name, $"assembly name '{name}'");
    }

    [Theory]
    [MemberData(nameof(ProductAssemblies))]
    public void TypeNamesAreOffstreamOwned(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            AssertClean(type.FullName ?? type.Name, $"type '{type.FullName}'");
        }
    }

    [Theory]
    [MemberData(nameof(ProductAssemblies))]
    public void ResourceNamesAreOffstreamOwned(Assembly assembly)
    {
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            AssertClean(resource, $"embedded resource '{resource}'");
        }
    }

    [Fact]
    public void RepositoryPathsAreOffstreamOwned()
    {
        var root = RepositoryRoot();

        var offenders = Directory
            .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(path => !IsIgnored(root, path))
            .Where(path => ForbiddenIdentifiers.Any(bad =>
                Path.GetFileName(path).Contains(bad, StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These paths carry an identifier inherited from the predecessor (plan §0):{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders));
    }

    private static bool IsIgnored(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment =>
            segment is "bin" or "obj" or ".git" or ".vs" or ".idea" or "packages" or ".remember");
    }

    /// <summary>Walks up from the test binaries until the solution file appears.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !IsRepositoryRoot(directory))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private static bool IsRepositoryRoot(DirectoryInfo directory) =>
        File.Exists(Path.Combine(directory.FullName, "Offstream.slnx"))
        || File.Exists(Path.Combine(directory.FullName, "Offstream.sln"));

    private static void AssertClean(string value, string description)
    {
        foreach (var forbidden in ForbiddenIdentifiers)
        {
            Assert.False(
                value.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"{description} contains '{forbidden}', an identifier inherited from the predecessor (plan §0).");
        }
    }
}
