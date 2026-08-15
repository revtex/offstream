// System.IO explicitly: the throwaway project WPF builds first to resolve XAML type references
// does not inherit ImplicitUsings, so a type this file reaches through them fails to compile
// there while compiling fine in the real assembly.
using System.IO;
using System.Reflection;
using System.Text;

namespace Offstream.App.Services;

/// <summary>
/// The licence and attribution text the app shows, and the version string that identifies the
/// build showing it.
/// </summary>
/// <remarks>
/// <para>
/// <c>LICENSE</c> and <c>NOTICE</c> are embedded into the executable rather than read from a
/// path beside it. Both are obligations — the MIT licence Offstream inherits from the
/// predecessor requires its copyright notice travel with the software, and the bundled ffmpeg
/// is LGPL — and a file read from disk is an obligation that a portable zip unpacked
/// selectively, or a run from <c>bin\Debug</c>, quietly fails to meet. Embedded, the text is
/// present wherever the executable is, and is the same text the repository holds because it is
/// literally that file.
/// </para>
/// <para>
/// This is App-layer, not Core: it exists to fill a window.
/// </para>
/// </remarks>
public static class ThirdPartyNotices
{
    private const string LicenseResource = "Offstream.App.LICENSE";
    private const string NoticeResource = "Offstream.App.NOTICE";

    /// <summary>Enough of a commit hash to identify a build, and no more.</summary>
    private const int ShortHashLength = 7;

    private static readonly Lazy<string> LazyText = new(BuildText);
    private static readonly Lazy<string> LazyVersion = new(BuildVersion);

    /// <summary>
    /// The full notices document: Offstream's own licence, then everything it is built on.
    /// </summary>
    public static string Text => LazyText.Value;

    /// <summary>
    /// The running build, as <c>1.2.3</c> or <c>0.1.0-dev+abc1234</c>.
    /// </summary>
    /// <remarks>
    /// From <see cref="AssemblyInformationalVersionAttribute"/>, which is the only version that
    /// carries the prerelease suffix — <c>AssemblyVersion</c> is numeric-only and reports
    /// <c>1.2.3.0</c> for every prerelease of 1.2.3. The SDK appends the full 40-character
    /// commit hash; a bug report needs enough of it to find the commit, so it is cut to seven
    /// rather than shown whole or dropped.
    /// </remarks>
    public static string Version => LazyVersion.Value;

    private static string BuildText()
    {
        var license = ReadResource(LicenseResource);
        var notice = ReadResource(NoticeResource);

        // NOTICE opens by pointing at LICENSE for the full text, so LICENSE comes first: the
        // reader meets the reference after the thing it refers to.
        return license.TrimEnd() + Environment.NewLine + Environment.NewLine + notice.TrimEnd();
    }

    private static string BuildVersion()
    {
        var assembly = typeof(ThirdPartyNotices).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            // Only reachable if the attribute is stripped; AssemblyVersion always exists.
            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return informational;
        }

        var hash = informational[(plus + 1)..];
        return hash.Length <= ShortHashLength
            ? informational
            : string.Concat(informational.AsSpan(0, plus + 1), hash.AsSpan(0, ShortHashLength));
    }

    private static string ReadResource(string name)
    {
        var assembly = typeof(ThirdPartyNotices).Assembly;

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"'{name}' is not embedded in {assembly.GetName().Name}. It is added by an " +
                "EmbeddedResource item in Offstream.App.csproj; distributing the app without " +
                "it would drop a licence notice the app is obliged to carry.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
