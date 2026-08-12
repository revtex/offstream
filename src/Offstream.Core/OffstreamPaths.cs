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
    /// <summary>The single-instance mutex name.</summary>
    public const string InstanceMutex = @"Global\Offstream";

    /// <summary><c>%APPDATA%\Offstream</c>.</summary>
    public static string AppData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Offstream");

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
}
