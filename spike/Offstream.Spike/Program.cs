using System.Runtime.InteropServices;
using NAudio.CoreAudioApi.Interfaces;
using Offstream.Core.Audio;
using Offstream.Core.Interop.Routing;
using Offstream.Spike.Audio;

namespace Offstream.Spike;

/// <summary>
/// Phase 0 retarget spike. Proves the risky parts of the port survive .NET 10 before any
/// restructuring happens: WASAPI loopback under NAudio 2.x, the undocumented
/// IAudioPolicyConfig routing, and per-session mute/volume.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";

        try
        {
            return command switch
            {
                "info" => Info(),
                "devices" => ListDevices(),
                "sessions" => ListSessions(),
                "capture" => await CaptureAsync(args),
                "route" => Route(args),
                "mute" => SetMute(args, true),
                "unmute" => SetMute(args, false),
                "volume" => SetVolume(args),
                "accept" => await AcceptAsync(args),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"FAILED: {ex.GetType().Name}");
            Console.Error.WriteLine(ex.Message);
            if (ex.InnerException is not null) Console.Error.WriteLine($"  inner: {ex.InnerException.Message}");
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            Offstream Phase 0 spike

              info                                   environment + which IAudioPolicyConfig IID binds
              devices                                list active render endpoints
              sessions                               list audio sessions on the default endpoint
              capture [--seconds N] [--device ID] [--out PATH]
              route --pid N --device ID              pin a process to an endpoint
              route --show --pid N                   show the pinned endpoint
              route --reset                          clear every persisted override
              mute --pid N | unmute --pid N
              volume --pid N --level 0-100
              accept [--seconds N] [--process NAME]  full Phase 0 acceptance run

            Notes
              --device takes an endpoint id from `devices`, or a name fragment.
              `accept` defaults to the Spotify process and falls back to any process with audio.
            """);
        return 0;
    }

    // ---- environment -------------------------------------------------------

    private static int Info()
    {
        Console.WriteLine($"OS                 {Environment.OSVersion.VersionString}");
        Console.WriteLine($"Build              {Environment.OSVersion.Version.Build}");
        Console.WriteLine($".NET               {Environment.Version}");
        Console.WriteLine($"Framework          {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"Architecture       {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Elevated           {IsElevated()}");
        Console.WriteLine();

        Console.Write("IAudioPolicyConfig ");
        try
        {
            var router = new AudioRouter();
            Console.WriteLine($"bound: {router.Variant}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ---- devices and sessions ----------------------------------------------

    private static int ListDevices()
    {
        foreach (var device in AudioEndpoints.ListRender())
            Console.WriteLine($"{(device.IsDefault ? "*" : " ")} {device.Name}\n    {device.Id}");

        return 0;
    }

    private static int ListSessions()
    {
        var sessions = AudioSessions.List()
            .OrderByDescending(s => s.State == AudioSessionState.AudioSessionStateActive)
            .ThenBy(s => s.ProcessName);

        Console.WriteLine($"{"PID",-8} {"PROCESS",-24} {"STATE",-10} {"MUTED",-6} VOLUME");
        foreach (var s in sessions)
        {
            var state = s.State.ToString().Replace("AudioSessionState", string.Empty);
            Console.WriteLine($"{s.ProcessId,-8} {Truncate(s.ProcessName, 24),-24} {state,-10} {s.Muted,-6} {s.Volume:P0}");
        }

        return 0;
    }

    // ---- capture -----------------------------------------------------------

    private static async Task<int> CaptureAsync(string[] args)
    {
        var seconds = GetInt(args, "--seconds") ?? 30;
        var deviceId = ResolveDeviceArg(GetValue(args, "--device"));
        var path = GetValue(args, "--out")
                   ?? Path.Combine(Path.GetTempPath(), $"offstream-spike-{DateTime.Now:yyyyMMdd-HHmmss}.wav");

        var useTone = !HasFlag(args, "--no-tone");

        Console.WriteLine($"Capturing {seconds}s to {path}");
        Console.WriteLine(useTone
            ? "Generating a 440 Hz tone so the capture has a known signal."
            : "Play audio now so the capture has something to record.");

        using var tone = useTone ? Tone.Play(deviceId) : null;

        var result = await LoopbackCapture.RecordAsync(
            deviceId, TimeSpan.FromSeconds(seconds), path, CancellationToken.None);

        Report(result);
        return result.AnyNonSilentSample ? 0 : 2;
    }

    private static void Report(CaptureResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"  format      {result.Format}");
        Console.WriteLine($"  sample rate {result.Format.SampleRate} Hz, {result.Format.Channels} ch, {result.Format.BitsPerSample}-bit");
        Console.WriteLine($"  duration    {result.Duration.TotalSeconds:F1}s");
        Console.WriteLine($"  bytes       {result.Bytes:N0}");
        Console.WriteLine($"  signal      {(result.AnyNonSilentSample ? "yes" : "NO — captured pure silence")}");
        Console.WriteLine($"  file        {result.Path}");
    }

    // ---- routing -----------------------------------------------------------

    private static int Route(string[] args)
    {
        var router = new AudioRouter();

        if (HasFlag(args, "--reset"))
        {
            router.ResetAll();
            Console.WriteLine($"Cleared all persisted endpoint overrides via {router.Variant}.");
            return 0;
        }

        var pid = GetInt(args, "--pid") ?? throw new ArgumentException("--pid is required");

        if (HasFlag(args, "--show"))
        {
            var current = router.GetEndpoint(pid);
            Console.WriteLine(current is null
                ? $"pid {pid} ({AudioSessions.ProcessName(pid)}) is not pinned; it follows the system default."
                : $"pid {pid} ({AudioSessions.ProcessName(pid)}) is pinned to {current}");
            return 0;
        }

        var deviceId = ResolveDeviceArg(GetValue(args, "--device"))
                       ?? throw new ArgumentException("--device is required");

        router.SetEndpoint(pid, deviceId);
        Console.WriteLine($"Routed pid {pid} ({AudioSessions.ProcessName(pid)}) to {deviceId}");
        Console.WriteLine($"  via {router.Variant}");

        var verified = router.GetEndpoint(pid);
        Console.WriteLine($"  read back: {verified ?? "(nothing)"}");
        return verified == deviceId ? 0 : 2;
    }

    // ---- session control ---------------------------------------------------

    private static int SetMute(string[] args, bool mute)
    {
        var pid = GetInt(args, "--pid") ?? throw new ArgumentException("--pid is required");
        var touched = AudioSessions.SetMute(pid, mute);

        Console.WriteLine(touched == 0
            ? $"No audio sessions found for pid {pid}."
            : $"{(mute ? "Muted" : "Unmuted")} {touched} session(s) for pid {pid} ({AudioSessions.ProcessName(pid)}).");

        return touched > 0 ? 0 : 2;
    }

    private static int SetVolume(string[] args)
    {
        var pid = GetInt(args, "--pid") ?? throw new ArgumentException("--pid is required");
        var level = GetInt(args, "--level") ?? throw new ArgumentException("--level is required");
        var touched = AudioSessions.SetVolume(pid, level / 100f);

        Console.WriteLine($"Set {touched} session(s) for pid {pid} to {level}%.");
        return touched > 0 ? 0 : 2;
    }

    // ---- acceptance run ----------------------------------------------------

    /// <summary>
    /// Runs the Phase 0 exit criteria end to end and prints a pass/fail table.
    /// </summary>
    private static async Task<int> AcceptAsync(string[] args)
    {
        var seconds = GetInt(args, "--seconds") ?? 30;
        var processName = GetValue(args, "--process") ?? "Spotify";
        var results = new List<(string Check, bool Passed, string Detail)>();

        Console.WriteLine("=== Phase 0 acceptance run ===");
        Console.WriteLine($"{Environment.OSVersion.VersionString} · {RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine();

        // 1. Endpoint enumeration.
        var devices = AudioEndpoints.ListRender();
        results.Add(("Enumerate render endpoints", devices.Count > 0, $"{devices.Count} active"));

        // 2. Bind the undocumented routing interface.
        AudioRouter? router = null;
        try
        {
            router = new AudioRouter();
            results.Add(("Bind IAudioPolicyConfig", true, router.Variant));
        }
        catch (Exception ex)
        {
            results.Add(("Bind IAudioPolicyConfig", false, $"{ex.GetType().Name}: {ex.Message}"));
        }

        // 3. Find a target process that actually has an audio session.
        var target = AudioSessions.FindProcessId(processName);
        var sessions = AudioSessions.List();
        if (target is null)
        {
            var candidate = sessions.FirstOrDefault(s =>
                s.ProcessId != 0 && s.State == AudioSessionState.AudioSessionStateActive);
            target = candidate?.ProcessId;
            if (target is not null)
                Console.WriteLine($"note: {processName} is not running; using {candidate!.ProcessName} (pid {target}) instead.");
        }

        results.Add(("Locate a target process", target is not null,
            target is null ? $"no running {processName} and no active session" : $"{AudioSessions.ProcessName(target.Value)} (pid {target})"));

        // 4. Route it, read it back, then restore.
        if (router is not null && target is not null)
        {
            // Prefer a non-default endpoint so routing is observable, but with one endpoint
            // on the box the round-trip through COM is still worth asserting.
            var destination = devices.FirstOrDefault(d => !d.IsDefault) ?? (devices.Count > 0 ? devices[0] : null);
            if (destination is null)
            {
                results.Add(("Route process to endpoint", false, "no endpoint to route to"));
            }
            else
            {
                try
                {
                    router.SetEndpoint(target.Value, destination.Id);
                    var readBack = router.GetEndpoint(target.Value);
                    var ok = readBack == destination.Id;
                    results.Add(("Route process to endpoint", ok,
                        ok ? $"→ {destination.Name}" : $"set {destination.Id} but read back {readBack ?? "(nothing)"}"));

                    router.SetEndpoint(target.Value, null);
                    var cleared = router.GetEndpoint(target.Value);
                    results.Add(("Restore default endpoint", cleared is null, cleared ?? "cleared"));
                }
                catch (Exception ex)
                {
                    results.Add(("Route process to endpoint", false, $"{ex.GetType().Name}: {ex.Message}"));
                }
            }
        }

        // 5. Mute and unmute a session.
        if (target is not null)
        {
            try
            {
                var before = AudioSessions.List().FirstOrDefault(s => s.ProcessId == target.Value);
                var muted = AudioSessions.SetMute(target.Value, true);
                var during = AudioSessions.List().FirstOrDefault(s => s.ProcessId == target.Value);
                AudioSessions.SetMute(target.Value, before?.Muted ?? false);

                var ok = muted > 0 && during is { Muted: true };
                results.Add(("Mute session", ok, ok ? $"{muted} session(s) toggled and restored" : "mute did not take effect"));
            }
            catch (Exception ex)
            {
                results.Add(("Mute session", false, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        // 6. Capture.
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"offstream-spike-accept-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            Console.WriteLine($"Capturing {seconds}s of a generated 440 Hz tone...");

            using var tone = Tone.Play(null);
            var capture = await LoopbackCapture.RecordAsync(null, TimeSpan.FromSeconds(seconds), path, CancellationToken.None);

            results.Add(($"Capture {seconds}s of loopback audio", capture.Bytes > 0,
                $"{capture.Bytes:N0} bytes @ {capture.Format.SampleRate}Hz/{capture.Format.Channels}ch"));
            results.Add(("Captured audio is not silence", capture.AnyNonSilentSample,
                capture.AnyNonSilentSample ? path : "pure silence — was anything playing?"));
        }
        catch (Exception ex)
        {
            results.Add(("Capture loopback audio", false, $"{ex.GetType().Name}: {ex.Message}"));
        }

        // Report.
        Console.WriteLine();
        Console.WriteLine("=== Results ===");
        foreach (var (check, passed, detail) in results)
            Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {check,-34} {detail}");

        var failed = results.Count(r => !r.Passed);
        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"All {results.Count} checks passed."
            : $"{failed} of {results.Count} checks FAILED.");

        return failed == 0 ? 0 : 1;
    }

    // ---- argument helpers --------------------------------------------------

    private static string? GetValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int? GetInt(string[] args, string name) =>
        int.TryParse(GetValue(args, name), out var value) ? value : null;

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Accepts either a full endpoint id or a name fragment.</summary>
    private static string? ResolveDeviceArg(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith('{') || value.Contains('.')) return value;

        var match = AudioEndpoints.FindByName(value);
        if (match is null) throw new ArgumentException($"No render endpoint matching '{value}'.");

        Console.WriteLine($"Matched '{value}' → {match.Name}");
        return match.Id;
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..(length - 1)] + "…";
}
