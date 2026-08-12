using Moq;
using Offstream.Core.Audio;
using Offstream.Core.Interop;
using Xunit;

namespace Offstream.Core.Tests.Audio;

/// <summary>
/// Covers the probe's process-matching half, which is testable without audio hardware.
/// </summary>
/// <remarks>
/// Reading real session state needs a live WASAPI endpoint, so that path belongs to the
/// Phase 9 manual checklist and to <c>Offstream.Spike accept</c>. What is asserted here is
/// the decision that runs first and short-circuits everything else.
/// </remarks>
public sealed class SpotifyPlaybackProbeTests
{
    private readonly Mock<IProcessManager> _processManager = new();

    [Fact]
    public async Task WithNoSpotifyProcess_ReportsNotPlayingWithoutTouchingAudio()
    {
        _processManager
            .Setup(x => x.GetProcesses())
            .Returns([new ProcessInfo(1, "Firefox", "Facebook")]);

        var probe = new SpotifyPlaybackProbe(_processManager.Object);

        Assert.False(await probe.IsSpotifyPlayingAsync());
    }

    [Fact]
    public async Task WithNoProcessesAtAll_ReportsNotPlaying()
    {
        _processManager.Setup(x => x.GetProcesses()).Returns([]);

        var probe = new SpotifyPlaybackProbe(_processManager.Object);

        Assert.False(await probe.IsSpotifyPlayingAsync());
    }

    [Fact]
    public async Task RespectsCancellation()
    {
        _processManager.Setup(x => x.GetProcesses()).Returns([]);

        var probe = new SpotifyPlaybackProbe(_processManager.Object);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => probe.IsSpotifyPlayingAsync(cancelled.Token));
    }
}

/// <summary>
/// Endpoint enumeration against the real audio stack.
/// </summary>
/// <remarks>
/// These touch actual hardware, so they assert invariants rather than specific devices: a
/// build agent may have one endpoint, many, or none. What they catch is the NAudio 2.x
/// device API breaking under a runtime update, which the Phase 0 spike proved works today.
/// </remarks>
public sealed class AudioEndpointsTests
{
    [Fact]
    public void ListRender_ReturnsConsistentEntries()
    {
        var devices = AudioEndpoints.ListRender();

        Assert.All(devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.NotNull(device.Name);
        });

        // At most one endpoint can be the system default.
        Assert.True(devices.Count(d => d.IsDefault) <= 1);
    }

    [Fact]
    public void FindByName_WithNoMatch_ReturnsNull() =>
        Assert.Null(AudioEndpoints.FindByName("no-such-endpoint-exists-anywhere-xyzzy"));
}
