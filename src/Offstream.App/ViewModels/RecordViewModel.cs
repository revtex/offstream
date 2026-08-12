using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Offstream.App.Resources;
using Offstream.Core.Diagnostics;

namespace Offstream.App.ViewModels;

/// <summary>
/// Backs the Record page: status line and the activity log.
/// </summary>
/// <remarks>
/// The console-log metaphor carries over from the predecessor deliberately (plan §11) — it is
/// how the app explains itself while it runs. What does not carry over is where the text lives:
/// the old app kept its console contents in a settings string, so the log was persisted user
/// configuration. Here the lines come from the Serilog sink and the durable copy is a rotating
/// file under <see cref="Offstream.Core.OffstreamPaths.LogDirectory"/>.
/// </remarks>
public sealed partial class RecordViewModel : ObservableObject
{
    private readonly InMemoryLogSink _logSink;

    [ObservableProperty]
    private string _status = Strings.RecordStatusIdle;

    public RecordViewModel(InMemoryLogSink logSink)
    {
        ArgumentNullException.ThrowIfNull(logSink);
        _logSink = logSink;

        // Startup logs before this page is ever shown, so replay what is already there before
        // subscribing - otherwise the first thing the user sees is an empty log.
        foreach (var line in logSink.Snapshot()) Append(line);
        logSink.LineWritten += OnLineWritten;
    }

    /// <summary>Lines shown in the activity log.</summary>
    public ObservableCollection<string> LogLines { get; } = [];

    [RelayCommand]
    private void ClearLog()
    {
        _logSink.Clear();
        LogLines.Clear();
    }

    private void OnLineWritten(object? sender, LogLine line)
    {
        // Serilog writes from whatever thread logged; marshal to the UI thread.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Append(line);
            return;
        }

        dispatcher.BeginInvoke(() => Append(line));
    }

    private void Append(LogLine line) =>
        LogLines.Add($"{line.Timestamp:HH:mm:ss} [{line.Level}] {line.Message}");
}
