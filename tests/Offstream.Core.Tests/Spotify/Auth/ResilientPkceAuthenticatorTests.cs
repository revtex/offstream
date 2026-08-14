using Offstream.Core.Spotify.Auth;
using SpotifyAPI.Web;
using Xunit;

namespace Offstream.Core.Tests.Spotify.Auth;

/// <summary>
/// The one-hour bug, pinned. Renewal itself needs the network, but the fault does not: it is a
/// field being blanked on a shared token instance, so it reproduces exactly by blanking it.
/// </summary>
public sealed class ResilientPkceAuthenticatorTests
{
    private static PKCETokenResponse TokenWith(string? refreshToken) => new()
    {
        AccessToken = "access",
        TokenType = "Bearer",
        RefreshToken = refreshToken!,
        ExpiresIn = 3600,
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// What actually happened in the field: Spotify renews without returning a refresh token, the
    /// SDK copies the absence onto the live token, and the next renewal has nothing to send.
    /// </summary>
    [Fact]
    public void ARenewalThatOmitsTheRefreshToken_DoesNotLoseIt()
    {
        var token = TokenWith("the-refresh-token");
        var authenticator = new ResilientPkceAuthenticator("client", token);

        // The SDK writes the renewal response onto the instance it was given. Spotify sent no
        // refresh_token, so the field is cleared.
        token.RefreshToken = null!;

        authenticator.RestoreRefreshToken();

        Assert.Equal("the-refresh-token", token.RefreshToken);
    }

    /// <summary>
    /// The whole failure was that this holds across renewals: one blanked renewal used to poison
    /// every request after it, for the life of the session.
    /// </summary>
    [Fact]
    public void RepeatedRenewalsThatOmitIt_KeepWorking()
    {
        var token = TokenWith("the-refresh-token");
        var authenticator = new ResilientPkceAuthenticator("client", token);

        for (var renewal = 0; renewal < 5; renewal++)
        {
            token.RefreshToken = null!;
            authenticator.RestoreRefreshToken();

            Assert.Equal("the-refresh-token", token.RefreshToken);

            authenticator.RememberRefreshToken();
        }
    }

    /// <summary>
    /// Spotify rotates the refresh token on most renewals, and the replacement is what must be
    /// kept — restoring a superseded one would send a token that has already been retired.
    /// </summary>
    [Fact]
    public void ARenewalThatRotatesTheToken_KeepsTheNewOne()
    {
        var token = TokenWith("original");
        var authenticator = new ResilientPkceAuthenticator("client", token);

        token.RefreshToken = "rotated";
        authenticator.RememberRefreshToken();

        token.RefreshToken = null!;
        authenticator.RestoreRefreshToken();

        Assert.Equal("rotated", token.RefreshToken);
    }

    /// <summary>A token that is present is never touched.</summary>
    [Fact]
    public void APresentRefreshToken_IsLeftAlone()
    {
        var token = TokenWith("current");
        var authenticator = new ResilientPkceAuthenticator("client", token);

        authenticator.RestoreRefreshToken();

        Assert.Equal("current", token.RefreshToken);
    }

    /// <summary>
    /// Nothing to restore is not something to invent. An install that genuinely has no refresh
    /// token must fail its way to the sign-in button, not carry an empty string to Spotify.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithNothingEverStored_NothingIsFabricated(string? stored)
    {
        var token = TokenWith(stored);
        var authenticator = new ResilientPkceAuthenticator("client", token);

        authenticator.RestoreRefreshToken();

        Assert.True(string.IsNullOrEmpty(token.RefreshToken));
    }

    /// <summary>The wrapper exposes the SDK's own live token, not a copy of it.</summary>
    [Fact]
    public void Token_IsTheInstanceTheSdkMutates()
    {
        var token = TokenWith("the-refresh-token");

        Assert.Same(token, new ResilientPkceAuthenticator("client", token).Token);
    }

    [Fact]
    public void Constructor_RejectsAMissingToken() =>
        Assert.Throws<ArgumentNullException>(() => new ResilientPkceAuthenticator("client", null!));
}
