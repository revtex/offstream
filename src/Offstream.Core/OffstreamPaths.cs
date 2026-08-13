using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Offstream.Core;

/// <summary>
/// Every on-disk location Offstream owns.
/// </summary>
/// <remarks>
/// Centralised so the naming rule (plan §0) is enforced in one place rather than by
/// convention at each call site. Nothing here reads or writes the predecessor's
/// <c>%LOCALAPPDATA%\Spytify\user.config</c> — there is no migration by design (plan §6).
/// </remarks>
public static class OffstreamPaths
{
    /// <summary>
    /// Environment variable that relocates <see cref="AppData"/>, for tests and portable use.
    /// </summary>
    /// <remarks>
    /// The UI suite drives the real executable, which would otherwise read and rewrite the
    /// developer's own <c>settings.json</c> — a test run that changes the format to FLAC and
    /// leaves it there. Pointing each run at its own directory also makes the suite
    /// order-independent, since no test inherits what the last one saved.
    /// </remarks>
    public const string HomeVariable = "OFFSTREAM_HOME";

    /// <summary>The single-instance mutex name.</summary>
    /// <remarks>
    /// <para>
    /// <c>Local\</c>, not <c>Global\</c>: the name is per logon session, so a second Windows
    /// user gets their own instance. A global mutex would let whoever logged in first block
    /// everyone else out of the app entirely, and the activation signal could not cross
    /// sessions to show them why — their instance would simply exit. Each session records its
    /// own audio, so each session gets its own Offstream. (Plan §0 records the name; the
    /// prefix is this PR's correction.)
    /// </para>
    /// <para>
    /// The claim is per data directory, so a relocated <see cref="HomeVariable"/> gets its own.
    /// What the guard is really protecting is one <c>settings.json</c> against two writers;
    /// two installs with separate settings are not the same application in the sense that
    /// matters. It is also what lets the UI suite drive a window while the developer's own
    /// Offstream is running, instead of the test instance exiting on startup.
    /// </para>
    /// </remarks>
    public static string InstanceMutex { get; } =
        ResolveInstanceMutex(Environment.GetEnvironmentVariable(HomeVariable));

    /// <summary><c>%APPDATA%\Offstream</c>, or <see cref="HomeVariable"/> when it is set.</summary>
    public static string AppData { get; } = ResolveAppData();

    /// <summary><c>%APPDATA%\Offstream\settings.json</c>.</summary>
    public static string SettingsFile => Path.Combine(AppData, "settings.json");

    /// <summary><c>%APPDATA%\Offstream\logs</c>.</summary>
    public static string LogDirectory => Path.Combine(AppData, "logs");

    /// <summary>Rotating log path; Serilog appends the date before the extension.</summary>
    public static string LogFile => Path.Combine(LogDirectory, "offstream-.log");

    /// <summary>Default output folder for recordings on a fresh install (plan §6).</summary>
    public static string DefaultOutputDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Offstream");

    /// <summary>Creates the directories the app expects to exist.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(AppData);
        Directory.CreateDirectory(LogDirectory);
    }

    private static string ResolveAppData() =>
        ResolveAppData(
            Environment.GetEnvironmentVariable(HomeVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>
    /// Resolves the data directory, preferring <paramref name="home"/> when it is usable.
    /// </summary>
    /// <remarks>
    /// A relative or malformed override is ignored rather than honoured. Resolving it against
    /// the working directory would scatter settings wherever the app happened to be launched
    /// from, and the failure would look like settings that reset themselves at random.
    /// </remarks>
    /// <param name="home">The override, usually from <see cref="HomeVariable"/>.</param>
    /// <param name="roamingAppData">Where to fall back to, usually <c>%APPDATA%</c>.</param>
    internal static string ResolveAppData(string? home, string roamingAppData) =>
        IsUsableHome(home) ? home : Path.Combine(roamingAppData, "Offstream");

    /// <summary>
    /// Names the single-instance claim, per data directory.
    /// </summary>
    /// <remarks>
    /// The suffix is a digest of the path rather than the path itself: a kernel object name is
    /// capped at 260 characters and cannot contain a backslash beyond the namespace prefix, so a
    /// directory pasted in whole would produce a name that is invalid, truncated, or both. Being
    /// a digest, it is also stable — the same portable install still excludes itself across
    /// launches, which is the whole point of the guard.
    /// </remarks>
    internal static string ResolveInstanceMutex(string? home) =>
        IsUsableHome(home) ? $@"Local\Offstream.{Fingerprint(home)}" : @"Local\Offstream";

    private static bool IsUsableHome([NotNullWhen(true)] string? home) =>
        !string.IsNullOrWhiteSpace(home) && Path.IsPathFullyQualified(home);

    /// <summary>Eight hex characters identifying a directory, case- and separator-insensitively.</summary>
    private static string Fingerprint(string path)
    {
        var normalised = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            .ToUpperInvariant();

        // Qualified: Offstream.Core.Encoding is the ffmpeg namespace, and it wins the lookup here.
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalised));

        return Convert.ToHexString(digest.AsSpan(0, 4));
    }
}
