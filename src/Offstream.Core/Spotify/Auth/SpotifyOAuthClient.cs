using SpotifyAPI.Web;

namespace Offstream.Core.Spotify.Auth;

/// <summary>
/// The two token requests a PKCE flow needs.
/// </summary>
/// <remarks>
/// <c>SpotifyAPI.Web</c>'s own <c>IOAuthClient</c> interface does not carry the PKCE overloads
/// — <c>OAuthClient.RequestToken(PKCETokenRequest, ...)</c> is a member of the concrete class
/// only — so this is the seam <see cref="SpotifyPkceFlow"/> is tested against instead.
/// </remarks>
public interface ISpotifyOAuthClient
{
    /// <summary>Exchanges an authorization code and its verifier for a token.</summary>
    Task<PKCETokenResponse> RequestTokenAsync(
        PKCETokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>Trades a refresh token for a new access token.</summary>
    Task<PKCETokenResponse> RequestTokenAsync(
        PKCETokenRefreshRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts <see cref="OAuthClient"/> to <see cref="ISpotifyOAuthClient"/>.
/// </summary>
/// <param name="config">
/// Supply one built with <c>SpotifyClientConfig.CreateDefault().WithHTTPClient(...)</c> to
/// route these requests through an <see cref="IHttpClientFactory"/>-managed
/// <see cref="HttpClient"/> instead of the SDK's own default. Null uses the SDK default.
/// </param>
public sealed class SpotifyOAuthClient(SpotifyClientConfig? config = null) : ISpotifyOAuthClient
{
    private readonly OAuthClient _client = new(config ?? SpotifyClientConfig.CreateDefault());

    /// <inheritdoc />
    public Task<PKCETokenResponse> RequestTokenAsync(
        PKCETokenRequest request, CancellationToken cancellationToken = default) =>
        _client.RequestToken(request, cancellationToken);

    /// <inheritdoc />
    public Task<PKCETokenResponse> RequestTokenAsync(
        PKCETokenRefreshRequest request, CancellationToken cancellationToken = default) =>
        _client.RequestToken(request, cancellationToken);
}
