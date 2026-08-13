using System.Net.Http;
using Offstream.Core.Spotify;
using Offstream.Core.Spotify.Auth;
using Serilog;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;

namespace Offstream.App.Services;

/// <summary>
/// The user's Spotify account, as the shell deals with it: signing in once, and building an
/// authenticated client afterwards.
/// </summary>
public interface ISpotifyAccount
{
    /// <summary>Runs a PKCE sign-in in the browser and returns the refresh token to store.</summary>
    /// <exception cref="SpotifyAuthException">The sign-in was declined, timed out or was tampered with.</exception>
    Task<string> SignInAsync(string clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a client for a stored refresh token, or null when the account is not set up.
    /// </summary>
    /// <param name="clientId">The user's Client ID.</param>
    /// <param name="refreshToken">The stored refresh token, already unprotected.</param>
    /// <param name="onRefreshTokenRotated">
    /// Called with the replacement whenever Spotify rotates the refresh token, which it does on
    /// every renewal. Not persisting it is how a long-running install silently stops working.
    /// </param>
    ISpotifyClient? CreateClient(string? clientId, string? refreshToken, Action<string> onRefreshTokenRotated);
}

/// <summary>
/// Wires <see cref="Offstream.Core.Spotify.Auth"/> up to the shell.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Client ID comes from settings, not from configuration.</b> It is the user's own, typed
/// on the Settings page, and it changes without an app restart — so the auth objects are built
/// per call rather than registered once in the container. That is the piece
/// <see cref="AppServices"/> deliberately left for this change to make.
/// </para>
/// <para>
/// <b>Renewal is the SDK's job.</b> <see cref="PKCEAuthenticator"/> notices an expired access
/// token and redeems the refresh token itself, which matters because a recording session can
/// easily outlive the one-hour token it started with. The seeded token is deliberately expired so
/// the first API call renews rather than failing with a stale one.
/// </para>
/// </remarks>
public sealed class SpotifyAccount(IHttpClientFactory httpClientFactory) : ISpotifyAccount
{
    private readonly IHttpClientFactory _httpClientFactory =
        httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

    /// <inheritdoc />
    public async Task<string> SignInAsync(string clientId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var options = new SpotifyAuthOptions { ClientId = clientId };

        var authenticator = new SpotifyAuthenticator(
            options,
            new SpotifyPkceFlow(new SpotifyOAuthClient(Config())),
            new BrowserLauncher(),
            redirectUri => new SpotifyLoopbackListener(redirectUri));

        var result = await authenticator.AuthenticateAsync(cancellationToken: cancellationToken);

        return result.RefreshToken;
    }

    /// <inheritdoc />
    public ISpotifyClient? CreateClient(string? clientId, string? refreshToken, Action<string> onRefreshTokenRotated)
    {
        ArgumentNullException.ThrowIfNull(onRefreshTokenRotated);

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(refreshToken)) return null;

        // Seeded expired on purpose: there is no access token on disk — only the refresh token —
        // so the authenticator must redeem one before the first request rather than after a 401.
        var seed = new PKCETokenResponse
        {
            AccessToken = string.Empty,
            TokenType = "Bearer",
            RefreshToken = refreshToken,
            ExpiresIn = 0,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
        };

        var authenticator = new PKCEAuthenticator(clientId, seed);

        authenticator.TokenRefreshed += (_, token) =>
        {
            if (!string.IsNullOrEmpty(token.RefreshToken)) onRefreshTokenRotated(token.RefreshToken);
        };

        Log.Debug("Building a Spotify client from the stored refresh token.");

        return new SpotifyClient(Config().WithAuthenticator(authenticator));
    }

    /// <summary>
    /// An SDK config routed through the factory's <see cref="HttpClient"/> rather than the
    /// SDK's own, so token requests share the app's handler pool and its DNS refresh behaviour.
    /// </summary>
    /// <remarks>
    /// The retry handler has to be attached explicitly: <see cref="SpotifyClientConfig.CreateDefault"/>
    /// leaves <c>RetryHandler</c> null, so without this a rate-limited call throws on the first
    /// 429 and the track records untagged. See <see cref="SpotifyRetryHandler"/> for why the wait
    /// is Spotify's to specify rather than ours to guess.
    /// </remarks>
    private SpotifyClientConfig Config() => SpotifyClientConfig
        .CreateDefault()
        .WithHTTPClient(new NetHttpClient(_httpClientFactory.CreateClient(nameof(Offstream))))
        .WithRetryHandler(new SpotifyRetryHandler());
}
