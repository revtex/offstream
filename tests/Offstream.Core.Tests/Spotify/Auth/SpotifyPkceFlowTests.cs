using Moq;
using Offstream.Core.Spotify.Auth;
using SpotifyAPI.Web;
using Xunit;

namespace Offstream.Core.Tests.Spotify.Auth;

/// <summary>
/// The URL-building and code/token-exchange half of PKCE, against a stub <see cref="ISpotifyOAuthClient"/>
/// — the seam that exists because <c>IOAuthClient</c> does not carry the PKCE overloads.
/// </summary>
public sealed class SpotifyPkceFlowTests
{
    private static SpotifyAuthOptions Options(Uri? redirectUri = null) => new()
    {
        ClientId = "client-id",
        RedirectUri = redirectUri ?? new Uri("http://127.0.0.1:4002/callback"),
        Scopes = ["user-read-currently-playing", "user-read-playback-state"],
    };

    [Fact]
    public void CreateLoginChallenge_BuildsAUriPointingAtSpotifysAuthorizeEndpoint()
    {
        var challenge = SpotifyPkceFlow.CreateLoginChallenge(Options());

        Assert.StartsWith("https://accounts.spotify.com/authorize", challenge.LoginUri.ToString(), StringComparison.Ordinal);
        Assert.Contains("client_id=client-id", challenge.LoginUri.Query, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", challenge.LoginUri.Query, StringComparison.Ordinal);
        Assert.Contains("response_type=code", challenge.LoginUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateLoginChallenge_IncludesTheConfiguredScopes()
    {
        var challenge = SpotifyPkceFlow.CreateLoginChallenge(Options());

        Assert.Contains("user-read-currently-playing", challenge.LoginUri.Query, StringComparison.Ordinal);
        Assert.Contains("user-read-playback-state", challenge.LoginUri.Query, StringComparison.Ordinal);
    }

    /// <summary>Every challenge is a fresh, unguessable value — reusing one would defeat the state check.</summary>
    [Fact]
    public void CreateLoginChallenge_GeneratesAFreshVerifierAndStateEachTime()
    {
        var first = SpotifyPkceFlow.CreateLoginChallenge(Options());
        var second = SpotifyPkceFlow.CreateLoginChallenge(Options());

        Assert.NotEqual(first.CodeVerifier, second.CodeVerifier);
        Assert.NotEqual(first.State, second.State);
    }

    [Fact]
    public async Task ExchangeCodeAsync_SendsTheCodeAndVerifierFromTheChallenge()
    {
        var oauthClient = new Mock<ISpotifyOAuthClient>();
        PKCETokenRequest? captured = null;

        oauthClient
            .Setup(x => x.RequestTokenAsync(It.IsAny<PKCETokenRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PKCETokenRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PKCETokenResponse { AccessToken = "access", RefreshToken = "refresh" });

        var flow = new SpotifyPkceFlow(oauthClient.Object);
        var options = Options();
        var challenge = SpotifyPkceFlow.CreateLoginChallenge(options);

        await flow.ExchangeCodeAsync(options, challenge, "the-auth-code");

        Assert.NotNull(captured);
        Assert.Equal("the-auth-code", captured!.Code);
        Assert.Equal(challenge.CodeVerifier, captured.CodeVerifier);
        Assert.Equal(options.ClientId, captured.ClientId);
        Assert.Equal(options.RedirectUri, captured.RedirectUri);
    }

    [Fact]
    public async Task RefreshAsync_SendsTheRefreshToken()
    {
        var oauthClient = new Mock<ISpotifyOAuthClient>();
        PKCETokenRefreshRequest? captured = null;

        oauthClient
            .Setup(x => x.RequestTokenAsync(It.IsAny<PKCETokenRefreshRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PKCETokenRefreshRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PKCETokenResponse { AccessToken = "new-access" });

        var flow = new SpotifyPkceFlow(oauthClient.Object);
        var options = Options();

        await flow.RefreshAsync(options, "old-refresh-token");

        Assert.NotNull(captured);
        Assert.Equal("old-refresh-token", captured!.RefreshToken);
        Assert.Equal(options.ClientId, captured.ClientId);
    }
}
