using Moq;
using Offstream.Core.Spotify.Auth;
using SpotifyAPI.Web;
using Xunit;

namespace Offstream.Core.Tests.Spotify.Auth;

/// <summary>
/// The orchestration around one sign-in — state checking, error handling, timeout — against
/// fakes for the listener and the browser, so none of this needs a real browser or network.
/// </summary>
public sealed class SpotifyAuthenticatorTests
{
    private static SpotifyAuthOptions Options() =>
        new() { ClientId = "client-id", RedirectUri = new Uri("http://127.0.0.1:4002/callback") };

    /// <summary>A controllable stand-in for the real loopback listener.</summary>
    private sealed class FakeListener(Uri redirectUri) : ISpotifyLoopbackListener
    {
        private readonly TaskCompletionSource<SpotifyCallback> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Uri RedirectUri { get; } = redirectUri;

        public bool Disposed { get; private set; }

        public Task<SpotifyCallback> WaitForCallbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            return _completion.Task;
        }

        public void Complete(SpotifyCallback callback) => _completion.TrySetResult(callback);

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeBrowserLauncher : IBrowserLauncher
    {
        public Uri? Opened { get; private set; }

        public void Open(Uri uri) => Opened = uri;
    }

    /// <summary>
    /// Holds the listener the authenticator's factory produces. The factory only runs once
    /// <see cref="SpotifyAuthenticator.AuthenticateAsync"/> is actually called — a field on this
    /// holder, read after that call, is what lets a test get at the same instance the
    /// authenticator is waiting on.
    /// </summary>
    private sealed class ListenerCapture
    {
        public FakeListener? Listener { get; private set; }

        public ISpotifyLoopbackListener Create(Uri uri) => Listener = new FakeListener(uri);
    }

    /// <summary>
    /// Wires an authenticator with a stub token exchange and a listener the test controls via
    /// <paramref name="capture"/>. <see cref="SpotifyAuthenticator.AuthenticateAsync"/> creates
    /// the listener and opens the browser synchronously before its first await, so both are
    /// already populated immediately after the call — no polling needed to observe them.
    /// </summary>
    private static (SpotifyAuthenticator Authenticator, ListenerCapture Capture, FakeBrowserLauncher Browser) Build(
        PKCETokenResponse? tokenResponse = null)
    {
        var oauthClient = new Mock<ISpotifyOAuthClient>();
        oauthClient
            .Setup(x => x.RequestTokenAsync(It.IsAny<PKCETokenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tokenResponse ?? new PKCETokenResponse
            {
                AccessToken = "access-token",
                RefreshToken = "refresh-token",
                TokenType = "Bearer",
                ExpiresIn = 3600,
            });

        var flow = new SpotifyPkceFlow(oauthClient.Object);
        var browser = new FakeBrowserLauncher();
        var capture = new ListenerCapture();

        var authenticator = new SpotifyAuthenticator(Options(), flow, browser, capture.Create);

        return (authenticator, capture, browser);
    }

    /// <summary>
    /// Pulls <c>state</c> back out of the URL the authenticator built, so the test can complete
    /// the callback with the value that will actually match — mirroring how a real browser
    /// round-trips it.
    /// </summary>
    private static string StateFrom(Uri? loginUri)
    {
        Assert.NotNull(loginUri);
        var query = System.Web.HttpUtility.ParseQueryString(loginUri!.Query);
        var state = query["state"];
        Assert.NotNull(state);
        return state!;
    }

    [Fact]
    public async Task AuthenticateAsync_OpensTheBrowserAtTheLoginUriBeforeWaiting()
    {
        var (authenticator, capture, browser) = Build();
        var authenticating = authenticator.AuthenticateAsync();

        Assert.NotNull(browser.Opened);
        Assert.StartsWith("https://accounts.spotify.com/authorize", browser.Opened!.ToString(), StringComparison.Ordinal);

        capture.Listener!.Complete(new SpotifyCallback("code", StateFrom(browser.Opened), null, null));
        await authenticating.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AuthenticateAsync_WithAMatchingState_ExchangesTheCodeAndReturnsTheToken()
    {
        var (authenticator, capture, browser) = Build();
        var authenticating = authenticator.AuthenticateAsync();

        capture.Listener!.Complete(new SpotifyCallback("the-code", StateFrom(browser.Opened), null, null));

        var result = await authenticating.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("access-token", result.AccessToken);
        Assert.Equal("refresh-token", result.RefreshToken);
        Assert.False(result.IsExpired);
    }

    /// <summary>
    /// The check the reference implementation skipped. Accepting a mismatched state is how a
    /// stray or forged redirect could complete a sign-in it did not originate.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsync_WithAMismatchedState_ThrowsAndNeverExchangesTheCode()
    {
        var oauthClient = new Mock<ISpotifyOAuthClient>();
        var flow = new SpotifyPkceFlow(oauthClient.Object);
        var capture = new ListenerCapture();
        var authenticator = new SpotifyAuthenticator(Options(), flow, new FakeBrowserLauncher(), capture.Create);

        var authenticating = authenticator.AuthenticateAsync();
        capture.Listener!.Complete(new SpotifyCallback("the-code", "a-forged-state", null, null));

        await Assert.ThrowsAsync<SpotifyAuthException>(() => authenticating.WaitAsync(TimeSpan.FromSeconds(10)));

        oauthClient.Verify(
            x => x.RequestTokenAsync(It.IsAny<PKCETokenRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AuthenticateAsync_WhenSpotifyReportsAnError_ThrowsWithTheReason()
    {
        var (authenticator, capture, _) = Build();
        var authenticating = authenticator.AuthenticateAsync();

        capture.Listener!.Complete(new SpotifyCallback(null, "state", "access_denied", "The user declined"));

        var exception = await Assert.ThrowsAsync<SpotifyAuthException>(
            () => authenticating.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("access_denied", exception.Message, StringComparison.Ordinal);
        Assert.Contains("The user declined", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_WhenTheDeadlinePasses_ThrowsSpotifyAuthException()
    {
        var (authenticator, _, _) = Build(); // listener is never completed

        await Assert.ThrowsAsync<SpotifyAuthException>(
            () => authenticator.AuthenticateAsync(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task AuthenticateAsync_DisposesTheListenerAfterwards()
    {
        var (authenticator, capture, browser) = Build();
        var authenticating = authenticator.AuthenticateAsync();

        capture.Listener!.Complete(new SpotifyCallback("code", StateFrom(browser.Opened), null, null));
        await authenticating.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(capture.Listener!.Disposed);
    }

    [Fact]
    public async Task RefreshAsync_ReturnsANewToken()
    {
        var oauthClient = new Mock<ISpotifyOAuthClient>();
        oauthClient
            .Setup(x => x.RequestTokenAsync(It.IsAny<PKCETokenRefreshRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PKCETokenResponse { AccessToken = "new-access", RefreshToken = "new-refresh" });

        var authenticator = new SpotifyAuthenticator(
            Options(), new SpotifyPkceFlow(oauthClient.Object), new FakeBrowserLauncher(), uri => new FakeListener(uri));

        var result = await authenticator.RefreshAsync("old-refresh-token");

        Assert.Equal("new-access", result.AccessToken);
    }
}
