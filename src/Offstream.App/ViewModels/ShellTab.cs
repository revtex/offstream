namespace Offstream.App.ViewModels;

/// <summary>
/// The three tabs, in the order they appear.
/// </summary>
/// <remarks>
/// An enum rather than a page type or a string, because it is what both halves of the shell bind
/// to — the tab strip sets it, the content host reads it — and neither should have to know about
/// the other's vocabulary. Three tabs is a fixed structural decision (plan §11), not a list that
/// grows, so nothing is lost by naming them here.
/// </remarks>
public enum ShellTab
{
    /// <summary>Transport, display, and the activity log. The predecessor's "Spy" tab.</summary>
    Record,

    /// <summary>Where recordings go, and what they sound like.</summary>
    Settings,

    /// <summary>Naming, detection, tags, and the application's own options.</summary>
    Advanced,
}
