using NativeProcess = System.Diagnostics.Process;

namespace Offstream.Core.Interop;

/// <summary>
/// <see cref="IProcessManager"/> over <see cref="System.Diagnostics.Process"/>.
/// </summary>
/// <remarks>
/// Every call is defensive. Process enumeration races with process exit constantly: a
/// handle can go stale between being listed and being read, which throws from property
/// getters rather than from the call that produced it. The recorder polls this on a timer,
/// so a throw here would surface as a spurious recording failure.
/// </remarks>
public sealed class ProcessManager : IProcessManager
{
    public IProcessInfo? GetCurrentProcess()
    {
        try
        {
            using var process = NativeProcess.GetCurrentProcess();
            return Describe(process);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public IReadOnlyList<IProcessInfo> GetProcesses()
    {
        NativeProcess[] processes;

        try
        {
            processes = NativeProcess.GetProcesses();
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        return DescribeAll(processes);
    }

    public IReadOnlyList<IProcessInfo> GetProcessesByName(string processName)
    {
        NativeProcess[] processes;

        try
        {
            processes = NativeProcess.GetProcessesByName(processName);
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        return DescribeAll(processes);
    }

    public IProcessInfo? GetProcessById(int processId)
    {
        try
        {
            using var process = NativeProcess.GetProcessById(processId);
            return Describe(process);
        }
        catch (ArgumentException)
        {
            // No such process: it exited between being observed and being read.
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static List<IProcessInfo> DescribeAll(NativeProcess[] processes)
    {
        var results = new List<IProcessInfo>(processes.Length);

        foreach (var process in processes)
        {
            using (process)
            {
                var described = Describe(process);
                if (described is not null) results.Add(described);
            }
        }

        return results;
    }

    private static ProcessInfo? Describe(NativeProcess process)
    {
        try
        {
            return new ProcessInfo(
                process.Id,
                process.ProcessName,
                process.MainWindowTitle,
                process.MainWindowHandle);
        }
        catch (InvalidOperationException)
        {
            // Exited while being described.
            return null;
        }
    }
}
