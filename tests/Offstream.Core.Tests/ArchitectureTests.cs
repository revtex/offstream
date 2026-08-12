using Xunit;

namespace Offstream.Core.Tests;

/// <summary>
/// Enforces the one structural rule the whole port depends on (plan §3).
/// </summary>
public sealed class ArchitectureTests
{
    /// <summary>
    /// <c>Offstream.Core</c> must not reference WPF or <c>System.Windows</c>.
    /// </summary>
    /// <remarks>
    /// The predecessor passed its form interface into the watcher and recorder, which is why
    /// none of that logic could be tested without a form mock. Core reports through
    /// <see cref="Diagnostics.RecordingProgress"/> instead. A stray <c>using System.Windows</c>
    /// would undo that quietly, so it is asserted rather than trusted.
    /// </remarks>
    [Fact]
    public void CoreDoesNotReferenceWpf()
    {
        var referenced = typeof(OffstreamPaths).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name is not null)
            .ToList();

        string[] forbidden = ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml"];

        var violations = referenced.Where(name =>
            forbidden.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();

        Assert.True(
            violations.Count == 0,
            $"Offstream.Core references UI assemblies: {string.Join(", ", violations)}. " +
            "Core must stay UI-agnostic (plan §3).");
    }

    [Fact]
    public void CoreExposesUiAgnosticProgress()
    {
        var progress = Diagnostics.RecordingProgress.Info("scaffold");

        Assert.Equal(Diagnostics.RecordingStage.Idle, progress.Stage);
        Assert.Equal("scaffold", progress.Message);
    }
}
