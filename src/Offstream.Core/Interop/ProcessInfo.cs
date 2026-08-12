namespace Offstream.Core.Interop;

/// <summary>A running process, reduced to what track detection needs.</summary>
/// <remarks>
/// An abstraction rather than <see cref="System.Diagnostics.Process"/> so process
/// enumeration can be faked in tests — the reference implementation did the same, and it is
/// why the Spotify detection logic is testable at all.
/// </remarks>
public interface IProcessInfo
{
    int Id { get; }
    string ProcessName { get; }
    string MainWindowTitle { get; }
    nint MainWindowHandle { get; }
}

/// <inheritdoc cref="IProcessInfo"/>
public sealed record ProcessInfo(
    int Id,
    string ProcessName,
    string MainWindowTitle,
    nint MainWindowHandle = 0) : IProcessInfo;

/// <summary>Enumerates running processes.</summary>
public interface IProcessManager
{
    IProcessInfo? GetCurrentProcess();

    IReadOnlyList<IProcessInfo> GetProcesses();

    IReadOnlyList<IProcessInfo> GetProcessesByName(string processName);

    IProcessInfo? GetProcessById(int processId);
}
