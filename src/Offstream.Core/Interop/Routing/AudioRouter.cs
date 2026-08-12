using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Offstream.Core.Interop.Routing;

/// <summary>Pins one process's audio to a chosen endpoint.</summary>
public sealed class AudioRouter(DataFlow flow = DataFlow.Render)
{
    private const string RenderInterface = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string CaptureInterface = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";
    private const string MmDeviceApiToken = @"\\?\SWD#MMDEVAPI#";

    private IAudioPolicyConfig? _config;

    private IAudioPolicyConfig Config => _config ??= AudioPolicyConfigFactory.Create();

    public string Variant => Config.Variant;

    private string InterfaceSuffix => flow == DataFlow.Render ? RenderInterface : CaptureInterface;

    private string PackDeviceId(string deviceId) => $"{MmDeviceApiToken}{deviceId}{InterfaceSuffix}";

    private static string UnpackDeviceId(string deviceId)
    {
        if (deviceId.StartsWith(MmDeviceApiToken, StringComparison.Ordinal))
            deviceId = deviceId[MmDeviceApiToken.Length..];
        if (deviceId.EndsWith(RenderInterface, StringComparison.Ordinal))
            deviceId = deviceId[..^RenderInterface.Length];
        if (deviceId.EndsWith(CaptureInterface, StringComparison.Ordinal))
            deviceId = deviceId[..^CaptureInterface.Length];
        return deviceId;
    }

    /// <summary>
    /// Routes <paramref name="processId"/> to <paramref name="deviceId"/>, or back to the
    /// system default when <paramref name="deviceId"/> is null or empty.
    /// </summary>
    public void SetEndpoint(int processId, string? deviceId)
    {
        var hstring = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
                hstring = WinRtString.Create(PackDeviceId(deviceId));

            // Both roles, matching the reference implementation: Multimedia alone leaves
            // some playback paths on the old endpoint.
            ThrowIfFailed(Config.SetPersistedDefaultAudioEndpoint((uint)processId, flow, Role.Multimedia, hstring),
                nameof(Role.Multimedia));
            ThrowIfFailed(Config.SetPersistedDefaultAudioEndpoint((uint)processId, flow, Role.Console, hstring),
                nameof(Role.Console));
        }
        finally
        {
            WinRtString.Delete(hstring);
        }
    }

    /// <summary>Returns the endpoint currently pinned for a process, or null if none is.</summary>
    public string? GetEndpoint(int processId)
    {
        var hr = Config.GetPersistedDefaultAudioEndpoint(
            (uint)processId, flow, Role.Multimedia | Role.Console, out var hstring);

        if (hr != 0) return null;

        try
        {
            var raw = WinRtString.Read(hstring);
            return string.IsNullOrEmpty(raw) ? null : UnpackDeviceId(raw);
        }
        finally
        {
            WinRtString.Delete(hstring);
        }
    }

    public void ResetAll() =>
        ThrowIfFailed(Config.ClearAllPersistedApplicationDefaultEndpoints(), "ClearAll");

    private static void ThrowIfFailed(int hr, string what)
    {
        if (hr != 0) Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
        _ = what;
    }
}
