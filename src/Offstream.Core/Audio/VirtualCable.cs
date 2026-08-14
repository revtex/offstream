namespace Offstream.Core.Audio;

/// <summary>Whether a VB-Audio virtual cable is installed, and what to tell the user if not.</summary>
/// <param name="IsInstalled">True when a matching render endpoint exists.</param>
/// <param name="DeviceName">The endpoint's full name, when one was found.</param>
public readonly record struct VirtualCableStatus(bool IsInstalled, string? DeviceName);

/// <summary>
/// Detects VB-Audio Virtual Cable among the render endpoints.
/// </summary>
/// <remarks>
/// <para>
/// <b>Detection only, and that is a licence decision rather than an unfinished one.</b> VB-CABLE is
/// donationware owned by Vincent Burel. Its readme permits redistributing the package "AS IS" but
/// states plainly that integrating it into another installation procedure needs the author's
/// agreement. The predecessor ships the vendor's setup executables in its own tree and launches
/// them elevated from its UI, and displays neither the origin nor the donationware notice the
/// licence asks for. Offstream does not carry vendor binaries: it detects the cable and, when it is
/// absent, points the user at vb-audio.com to install it themselves. See plan §11 open question 9 —
/// until that is answered, this is the only form of the feature that ships.
/// </para>
/// <para>
/// <b>Matched by name, like the reference does.</b> There is no stable device id to key on — the
/// endpoint id is generated per install — so the driver's product name is what identifies it. The
/// match is a case-insensitive substring because Windows appends the endpoint's own suffix, giving
/// names of the form "CABLE Input (VB-Audio Virtual Cable)".
/// </para>
/// <para>
/// Why the cable matters at all: loopback capture records whatever the endpoint is playing, so
/// recording the default endpoint records every sound on the machine — notifications, other apps —
/// into the file. Routing Spotify to a virtual cable and capturing that instead is what makes a
/// recording contain only Spotify.
/// </para>
/// </remarks>
public static class VirtualCable
{
    /// <summary>The driver's product name, as it appears inside an endpoint's friendly name.</summary>
    public const string DriverName = "VB-Audio Virtual Cable";

    /// <summary>Where a user without the cable is sent. Never a bundled installer — see the remarks.</summary>
    public const string DownloadUrl = "https://vb-audio.com/Cable/";

    /// <summary>Looks for the cable among the endpoints supplied.</summary>
    /// <remarks>
    /// Takes the device list rather than enumerating, so the rule is testable without audio
    /// hardware and the caller can reuse a list it already has.
    /// </remarks>
    public static VirtualCableStatus DetectIn(IEnumerable<RenderDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        foreach (var device in devices)
        {
            if (device.Name?.Contains(DriverName, StringComparison.OrdinalIgnoreCase) == true)
            {
                return new VirtualCableStatus(IsInstalled: true, device.Name);
            }
        }

        return new VirtualCableStatus(IsInstalled: false, DeviceName: null);
    }

    /// <summary>Looks for the cable among the machine's active render endpoints.</summary>
    public static VirtualCableStatus Detect() => DetectIn(AudioEndpoints.ListRender());
}
