namespace Offstream.App.ViewModels;

/// <summary>
/// The tabs, in the order they appear.
/// </summary>
/// <remarks>
/// An enum rather than a page type or a string, because it is what both halves of the shell bind
/// to — the tab strip sets it, the content host reads it — and neither should have to know about
/// the other's vocabulary. Still a fixed structural decision (plan §11) rather than a list that
/// grows, so nothing is lost by naming them here.
/// </remarks>
public enum ShellTab
{
    /// <summary>Transport, display, and what the session has produced. The predecessor's "Spy" tab.</summary>
    Record,

    /// <summary>Where recordings go, and what they sound like.</summary>
    Settings,

    /// <summary>Naming, detection, tags, and the application's own options.</summary>
    Advanced,

    /// <summary>The activity log. Last because it is where you go when something is wrong.</summary>
    Logs,
}
