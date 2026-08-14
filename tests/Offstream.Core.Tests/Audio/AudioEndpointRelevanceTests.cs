using Offstream.Core.Audio;
using Xunit;

namespace Offstream.Core.Tests.Audio;

/// <summary>
/// Which endpoint changes actually end a capture. The two cases look different in the
/// notifications and are easy to conflate, which is why each is pinned separately.
/// </summary>
public sealed class AudioEndpointRelevanceTests
{
    private const string Captured = "{0.0.0.00000000}.{captured}";
    private const string Other = "{0.0.0.00000000}.{other}";

    private static AudioEndpointChange Change(AudioEndpointChangeKind kind, string? id = null) =>
        new(kind, id);

    /// <summary>An explicitly chosen device is lost when that exact endpoint goes away.</summary>
    [Fact]
    public void AChosenDeviceGoingAway_EndsTheCapture() =>
        Assert.True(AudioEndpointRelevance.EndsTheCapture(
            Captured,
            Change(AudioEndpointChangeKind.Unavailable, Captured)));

    /// <summary>Endpoint ids are compared without case, as Windows does not promise one.</summary>
    [Fact]
    public void AChosenDeviceGoingAway_IsMatchedWithoutCase() =>
        Assert.True(AudioEndpointRelevance.EndsTheCapture(
            Captured.ToUpperInvariant(),
            Change(AudioEndpointChangeKind.Unavailable, Captured)));

    /// <summary>Somebody else's headphones being unplugged is not our problem.</summary>
    [Fact]
    public void AnotherDeviceGoingAway_DoesNotEndTheCapture() =>
        Assert.False(AudioEndpointRelevance.EndsTheCapture(
            Captured,
            Change(AudioEndpointChangeKind.Unavailable, Other)));

    /// <summary>
    /// A capture pinned to a device does not care where Windows sends new audio — it is still
    /// reading the endpoint it was told to read.
    /// </summary>
    [Fact]
    public void ADefaultChange_DoesNotEndACapturePinnedToADevice() =>
        Assert.False(AudioEndpointRelevance.EndsTheCapture(
            Captured,
            Change(AudioEndpointChangeKind.DefaultChanged, Other)));

    /// <summary>
    /// The confusing one. A capture following the default loses its audio when the default moves,
    /// even though nothing was removed and the old endpoint still enumerates — so this must not
    /// be decided by looking for a removal.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ADefaultChange_EndsACaptureFollowingTheDefault(string? captured) =>
        Assert.True(AudioEndpointRelevance.EndsTheCapture(
            captured,
            Change(AudioEndpointChangeKind.DefaultChanged, Other)));

    /// <summary>
    /// A capture following the default is not ended by an unrelated device disappearing; only the
    /// default moving takes its audio away.
    /// </summary>
    [Fact]
    public void ADeviceGoingAway_DoesNotEndACaptureFollowingTheDefault() =>
        Assert.False(AudioEndpointRelevance.EndsTheCapture(
            null,
            Change(AudioEndpointChangeKind.Unavailable, Other)));

    /// <summary>Plugging something in never moves audio that is already being read.</summary>
    [Theory]
    [InlineData(Captured)]
    [InlineData(null)]
    public void ADeviceAppearing_NeverEndsACapture(string? captured) =>
        Assert.False(AudioEndpointRelevance.EndsTheCapture(
            captured,
            Change(AudioEndpointChangeKind.Available, Captured)));
}
