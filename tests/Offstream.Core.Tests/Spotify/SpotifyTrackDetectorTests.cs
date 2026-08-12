using Moq;
using Offstream.Core.Interop;
using Offstream.Core.Spotify;
using Xunit;

namespace Offstream.Core.Tests.Spotify;

/// <summary>
/// Ported from the reference suite's <c>SpotifyProcessTests</c>.
/// </summary>
/// <remarks>
/// The original stubbed a global <c>ExternalAPI.Instance</c> singleton in its constructor so
/// detection would not reach the network. Parsing and enrichment are separate concerns here
/// (see <see cref="SpotifyTitleParser"/>), so there is nothing global left to stub.
/// </remarks>
public sealed class SpotifyTrackDetectorTests
{
    private readonly Mock<IProcessManager> _processManager = new();
    private readonly Mock<ISpotifyPlaybackProbe> _playbackProbe = new();

    public SpotifyTrackDetectorTests()
    {
        _playbackProbe
            .Setup(x => x.IsSpotifyPlayingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        SetProcesses(
            new ProcessInfo(1, "Firefox", "Facebook"),
            new ProcessInfo(2, "Spotify", string.Empty));
    }

    private void SetProcesses(params IProcessInfo[] processes) =>
        _processManager.Setup(x => x.GetProcesses()).Returns(processes);

    private SpotifyTrackDetector Build() => new(_processManager.Object, _playbackProbe.Object);

    [Fact]
    public async Task WithNoSpotifyProcess_ReturnsNull()
    {
        SetProcesses(new ProcessInfo(1, "Firefox", "Facebook"));

        Assert.Null(await Build().GetCurrentTrackAsync());
    }

    [Fact]
    public async Task WithSpotifyButNoWindowTitle_ReturnsNull()
    {
        // A Spotify helper process with no window is not the one that reports tracks.
        SetProcesses(new ProcessInfo(2, "Spotify", string.Empty));

        Assert.Null(await Build().GetCurrentTrackAsync());
    }

    [Fact]
    public async Task WithPlayingTrack_ReturnsTrack()
    {
        SetProcesses(new ProcessInfo(3, "Spotify", "Artist Name - Song Title"));
        _processManager
            .Setup(x => x.GetProcessById(3))
            .Returns(new ProcessInfo(3, "Spotify", "Artist Name - Song Title"));

        var track = await Build().GetCurrentTrackAsync();

        Assert.NotNull(track);
        Assert.Equal("Artist Name", track.Artist);
        Assert.Equal("Song Title", track.Title);
        Assert.True(track.Playing);
        Assert.False(track.Ad);
    }

    /// <summary>
    /// The audio probe reports silence between tracks. A real title must still count as
    /// playing, or the first seconds of every song are lost.
    /// </summary>
    [Fact]
    public async Task WithRealTitleButSilentAudio_StillCountsAsPlaying()
    {
        SetProcesses(new ProcessInfo(3, "Spotify", "Artist - Title"));
        _processManager.Setup(x => x.GetProcessById(3)).Returns(new ProcessInfo(3, "Spotify", "Artist - Title"));
        _playbackProbe.Setup(x => x.IsSpotifyPlayingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var track = await Build().GetCurrentTrackAsync();

        Assert.NotNull(track);
        Assert.True(track.Playing);
    }

    [Fact]
    public async Task WithIdleTitleAndSilentAudio_IsNotPlaying()
    {
        SetProcesses(new ProcessInfo(3, "Spotify", SpotifyWindowTitles.Spotify));
        _processManager
            .Setup(x => x.GetProcessById(3))
            .Returns(new ProcessInfo(3, "Spotify", SpotifyWindowTitles.Spotify));

        var track = await Build().GetCurrentTrackAsync();

        Assert.NotNull(track);
        Assert.False(track.Playing);
    }

    [Fact]
    public async Task WhenSpotifyExitsMidPoll_ReturnsNullAndRecovers()
    {
        SetProcesses(new ProcessInfo(3, "Spotify", "Artist - Title"));
        _processManager.Setup(x => x.GetProcessById(3)).Returns((IProcessInfo?)null);

        var detector = Build();

        Assert.Null(await detector.GetCurrentTrackAsync());

        // Spotify comes back; the detector must re-resolve rather than stay on the dead id.
        SetProcesses(new ProcessInfo(9, "Spotify", "Artist - Title"));
        _processManager.Setup(x => x.GetProcessById(9)).Returns(new ProcessInfo(9, "Spotify", "Artist - Title"));

        Assert.Null(await detector.GetCurrentTrackAsync());     // re-resolves on this poll
        Assert.NotNull(await detector.GetCurrentTrackAsync());  // reads on the next
    }

    [Fact]
    public void GetSpotifyProcesses_MatchesByProcessName()
    {
        SetProcesses(
            new ProcessInfo(1, "Firefox", "Facebook"),
            new ProcessInfo(2, "Spotify", string.Empty),
            new ProcessInfo(3, "Spotify", "Artist - Title"));

        var spotify = SpotifyTrackDetector.GetSpotifyProcesses(_processManager.Object);

        Assert.Equal(2, spotify.Count);
        Assert.All(spotify, p => Assert.Equal("Spotify", p.ProcessName));
    }

    [Fact]
    public void GetMainSpotifyWindowHandle_ReturnsHandleOfTitledWindow()
    {
        SetProcesses(
            new ProcessInfo(2, "Spotify", string.Empty, 0x1000),
            new ProcessInfo(3, "Spotify", "Artist - Title", 0x1012));

        Assert.Equal(0x1012, SpotifyTrackDetector.GetMainSpotifyWindowHandle(_processManager.Object));
    }

    [Fact]
    public void GetMainSpotifyWindowHandle_WithNoSpotify_ReturnsNull()
    {
        SetProcesses(new ProcessInfo(1, "Firefox", "Facebook"));

        Assert.Null(SpotifyTrackDetector.GetMainSpotifyWindowHandle(_processManager.Object));
    }
}
