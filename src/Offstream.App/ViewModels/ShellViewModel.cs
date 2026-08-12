using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Offstream.Core.Diagnostics;

namespace Offstream.App.ViewModels;

/// <summary>
/// Backs the shell window. Phase 1 scaffold: proves host, DI, MVVM source generation and
/// the log pipeline are wired end to end. The three tabs arrive in Phase 6.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly InMemoryLogSink _logSink;

    [ObservableProperty]
    private string _status = "Phase 1 scaffold — no recording pipeline yet.";

    public ShellViewModel(InMemoryLogSink logSink)
    {
        ArgumentNullException.ThrowIfNull(logSink);
        _logSink = logSink;

        foreach (var line in logSink.Snapshot()) Append(line);
        logSink.LineWritten += OnLineWritten;
    }

    /// <summary>Lines shown in the console pane.</summary>
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
