using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Offstream.Core.Audio;

public sealed record SessionInfo(
    int ProcessId, string ProcessName, string DisplayName, bool Muted, float Volume, AudioSessionState State);

/// <summary>
/// Per-application session control: the mute and volume half of the Phase 0 acceptance run.
/// </summary>
public static class AudioSessions
{
    public static IReadOnlyList<SessionInfo> List(string? deviceId = null)
    {
        using var device = AudioEndpoints.Resolve(deviceId);
        var sessions = device.AudioSessionManager.Sessions;

        var results = new List<SessionInfo>();
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            var pid = (int)session.GetProcessID;

            results.Add(new SessionInfo(
                pid,
                ProcessName(pid),
                string.IsNullOrWhiteSpace(session.DisplayName) ? "(no display name)" : session.DisplayName,
                session.SimpleAudioVolume.Mute,
                session.SimpleAudioVolume.Volume,
                session.State));
        }

        return results;
    }

    /// <summary>Sets mute on every session belonging to a process. Returns how many were changed.</summary>
    public static int SetMute(int processId, bool mute, string? deviceId = null) =>
        ForEachSessionOf(processId, deviceId, session => session.SimpleAudioVolume.Mute = mute);

    /// <summary>Sets volume (0.0-1.0) on every session belonging to a process.</summary>
    public static int SetVolume(int processId, float volume, string? deviceId = null) =>
        ForEachSessionOf(processId, deviceId, session => session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f));

    private static int ForEachSessionOf(int processId, string? deviceId, Action<AudioSessionControl> apply)
    {
        using var device = AudioEndpoints.Resolve(deviceId);
        var sessions = device.AudioSessionManager.Sessions;

        var touched = 0;
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if ((int)session.GetProcessID != processId) continue;

            apply(session);
            touched++;
        }

        return touched;
    }

    public static string ProcessName(int processId)
    {
        if (processId == 0) return "(system)";

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "(exited)";
        }
        catch (InvalidOperationException)
        {
            return "(exited)";
        }
    }

    /// <summary>Finds the process id of a running process by name, if any.</summary>
    public static int? FindProcessId(string name) =>
        Process.GetProcessesByName(name).Select(p => (int?)p.Id).FirstOrDefault();
}
