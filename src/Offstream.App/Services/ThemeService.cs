using Wpf.Ui.Appearance;

namespace Offstream.App.Services;

/// <summary>The theme the user asked for, which is not the same thing as the theme applied.</summary>
public enum ShellTheme
{
    /// <summary>Follow whatever Windows is set to.</summary>
    System = 0,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>
/// Resolves and applies the shell's Fluent theme.
/// </summary>
/// <remarks>
/// <para>
/// Split into a pure <see cref="Resolve"/> and an <see cref="Apply(ShellTheme)"/> that touches
/// WPF-UI, because the interesting part is the mapping and the mapping is where the mistakes
/// are. <see cref="ApplicationThemeManager.GetSystemTheme"/> returns eleven values, not two:
/// beyond Light and Dark there are four high-contrast schemes and the personalisation themes
/// (Glow, CapturedMotion, Sunrise, Flow). Treating anything non-Dark as Light would render the
/// four high-contrast schemes as an ordinary light theme and silently undo the user's
/// accessibility setting, so those map to <see cref="ApplicationTheme.HighContrast"/> instead.
/// </para>
/// <para>
/// There is no theme key in <c>settings.json</c> yet — plan §6's schema predates this — so the
/// shell starts on <see cref="ShellTheme.System"/>. Adding the setting is a Phase 6 PR 3
/// concern, and needs no schema bump: a record parameter default covers an omitted key.
/// </para>
/// </remarks>
public static class ThemeService
{
    /// <summary>Maps a user preference onto the theme WPF-UI should actually apply.</summary>
    public static ApplicationTheme Resolve(ShellTheme preference) => preference switch
    {
        ShellTheme.Light => ApplicationTheme.Light,
        ShellTheme.Dark => ApplicationTheme.Dark,
        _ => FromSystem(ApplicationThemeManager.GetSystemTheme()),
    };

    /// <summary>Maps what Windows reports onto the three themes WPF-UI can render.</summary>
    public static ApplicationTheme FromSystem(SystemTheme systemTheme) => systemTheme switch
    {
        SystemTheme.Light => ApplicationTheme.Light,
        SystemTheme.HCWhite or SystemTheme.HCBlack or SystemTheme.HC1 or SystemTheme.HC2 =>
            ApplicationTheme.HighContrast,

        // Dark, the personalisation themes (Glow, CapturedMotion, Sunrise, Flow), Custom and
        // Unknown. Dark is Offstream's default look and the safe answer for anything the
        // enum grows later - a too-dark window is a preference, a too-light one is a flash.
        _ => ApplicationTheme.Dark,
    };

    /// <summary>Applies <paramref name="preference"/> to the running application.</summary>
    public static void Apply(ShellTheme preference) =>
        ApplicationThemeManager.Apply(Resolve(preference));
}
