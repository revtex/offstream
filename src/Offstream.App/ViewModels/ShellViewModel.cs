using CommunityToolkit.Mvvm.ComponentModel;

namespace Offstream.App.ViewModels;

/// <summary>
/// Backs the shell window itself — the title bar and anything that outlives a page.
/// </summary>
/// <remarks>
/// Thin on purpose. The pages own their own state and their own ViewModels; the shell's job is
/// the chrome around them. The one thing it does carry is the startup warning, because a
/// settings file that would not load is a whole-application condition and there is no page it
/// belongs to.
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>
    /// Why settings could not be read, or null when they loaded. Set once during startup.
    /// </summary>
    /// <remarks>
    /// <see cref="Offstream.Core.Settings.SettingsStore.LoadOrDefault"/> hands back the reason
    /// rather than throwing, so the app still opens on defaults. Showing that reason is the
    /// other half of the bargain: silently starting on defaults would look identical to a first
    /// run and leave the user wondering where their settings went.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartupWarning))]
    private string? _startupWarning;

    /// <summary>Whether <see cref="StartupWarning"/> has anything worth showing.</summary>
    public bool HasStartupWarning => !string.IsNullOrWhiteSpace(StartupWarning);
}
