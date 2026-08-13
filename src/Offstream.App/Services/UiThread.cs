using System.Windows;

namespace Offstream.App.Services;

/// <summary>
/// Marshals updates onto the UI thread.
/// </summary>
/// <remarks>
/// Recording state reaches the UI from wherever it happened — Serilog writes on the thread
/// that logged, progress arrives on the title poller, and the tray's activation signal comes
/// off the thread pool. Every one of those ends up setting an observable property, which WPF
/// will only tolerate from the dispatcher.
/// </remarks>
internal static class UiThread
{
    /// <summary>Runs <paramref name="update"/> on the UI thread, inline if already there.</summary>
    /// <remarks>
    /// With no <see cref="Application"/> at all — which is how the unit tests run — the update
    /// happens inline, exercising the same path a call already on the UI thread takes.
    /// </remarks>
    public static void Dispatch(Action update)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            update();
            return;
        }

        dispatcher.BeginInvoke(update);
    }
}
