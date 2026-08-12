using Offstream.App.Services;
using Wpf.Ui.Appearance;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// The <see cref="SystemTheme"/> to <see cref="ApplicationTheme"/> mapping.
/// </summary>
/// <remarks>
/// Worth a test because <see cref="SystemTheme"/> has twelve members and
/// <see cref="ApplicationTheme"/> has four: the obvious <c>== SystemTheme.Dark ? Dark : Light</c>
/// compiles, reads correctly, and renders the four high-contrast schemes as an ordinary light
/// theme — undoing an accessibility setting with no visible error.
/// </remarks>
public sealed class ThemeServiceTests
{
    [Theory]
    [InlineData(ShellTheme.Light, ApplicationTheme.Light)]
    [InlineData(ShellTheme.Dark, ApplicationTheme.Dark)]
    public void Resolve_ExplicitPreference_IgnoresTheSystem(ShellTheme preference, ApplicationTheme expected) =>
        Assert.Equal(expected, ThemeService.Resolve(preference));

    [Theory]
    [InlineData(SystemTheme.HCWhite)]
    [InlineData(SystemTheme.HCBlack)]
    [InlineData(SystemTheme.HC1)]
    [InlineData(SystemTheme.HC2)]
    public void FromSystem_HighContrast_StaysHighContrast(SystemTheme systemTheme) =>
        Assert.Equal(ApplicationTheme.HighContrast, ThemeService.FromSystem(systemTheme));

    [Fact]
    public void FromSystem_Light_IsLight() =>
        Assert.Equal(ApplicationTheme.Light, ThemeService.FromSystem(SystemTheme.Light));

    [Theory]
    [InlineData(SystemTheme.Dark)]
    [InlineData(SystemTheme.Glow)]
    [InlineData(SystemTheme.CapturedMotion)]
    [InlineData(SystemTheme.Sunrise)]
    [InlineData(SystemTheme.Flow)]
    [InlineData(SystemTheme.Custom)]
    [InlineData(SystemTheme.Unknown)]
    public void FromSystem_EverythingElse_IsDark(SystemTheme systemTheme) =>
        Assert.Equal(ApplicationTheme.Dark, ThemeService.FromSystem(systemTheme));

    /// <summary>
    /// A member added to <see cref="SystemTheme"/> by a WPF-UI upgrade must not land on an
    /// unhandled branch. The switch's default arm covers it; this asserts every member is
    /// mapped to something renderable rather than falling through to <c>Unknown</c>.
    /// </summary>
    [Fact]
    public void FromSystem_MapsEveryMember()
    {
        foreach (var systemTheme in Enum.GetValues<SystemTheme>())
        {
            var resolved = ThemeService.FromSystem(systemTheme);

            Assert.True(
                Enum.IsDefined(resolved) && resolved != ApplicationTheme.Unknown,
                $"{systemTheme} resolved to {resolved}, which WPF-UI cannot apply.");
        }
    }
}
