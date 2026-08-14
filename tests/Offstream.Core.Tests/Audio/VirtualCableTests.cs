using Offstream.Core.Audio;
using Xunit;

namespace Offstream.Core.Tests.Audio;

/// <summary>
/// Cable detection, against a supplied device list rather than the machine's own — so the rule is
/// testable without the driver installed, and on CI where it never is.
/// </summary>
public sealed class VirtualCableTests
{
    private static RenderDevice Device(string name) => new($"id-{name}", name, IsDefault: false);

    /// <summary>
    /// The real shape of the name: Windows wraps the driver's product name in the endpoint's own,
    /// which is exactly why this is a substring match and not an equality one.
    /// </summary>
    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)")]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)")]
    [InlineData("VB-Audio Virtual Cable")]
    [InlineData("Speakers (vb-audio virtual cable)")]
    public void AnEndpointCarryingTheDriverName_IsDetected(string name)
    {
        var status = VirtualCable.DetectIn([Device("Speakers (Realtek)"), Device(name)]);

        Assert.True(status.IsInstalled);
        Assert.Equal(name, status.DeviceName);
    }

    [Fact]
    public void OrdinaryEndpoints_AreNotMistakenForIt()
    {
        var status = VirtualCable.DetectIn(
        [
            Device("Speakers (Realtek High Definition Audio)"),
            Device("Headphones (Arctis 7)"),
            Device("Digital Output (S/PDIF)"),
        ]);

        Assert.False(status.IsInstalled);
        Assert.Null(status.DeviceName);
    }

    [Fact]
    public void NoEndpointsAtAll_IsNotInstalled() =>
        Assert.False(VirtualCable.DetectIn([]).IsInstalled);

    [Fact]
    public void DetectIn_RejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => VirtualCable.DetectIn(null!));

    /// <summary>
    /// The licence position, pinned. VB-CABLE is donationware whose readme forbids integrating the
    /// package into another installation procedure without the author's agreement, so Offstream
    /// ships no vendor binaries and sends the user to the source instead. If this ever becomes a
    /// local path, plan §11 open question 9 has to have been answered first.
    /// </summary>
    [Fact]
    public void TheAbsentCase_PointsAtTheVendorRatherThanABundledInstaller()
    {
        Assert.StartsWith("https://", VirtualCable.DownloadUrl, StringComparison.Ordinal);
        Assert.Contains("vb-audio.com", VirtualCable.DownloadUrl, StringComparison.Ordinal);
    }
}
