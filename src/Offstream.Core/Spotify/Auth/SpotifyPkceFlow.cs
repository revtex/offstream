using SpotifyAPI.Web;

namespace Offstream.Core.Spotify.Auth;

/// <summary>
/// One login attempt's PKCE material: the URL to send the user's browser to, and the verifier
/// and state needed to complete the round trip when it comes back.
/// </summary>
/// <param name="LoginUri">Open this in the user's browser.</param>
/// <param name="CodeVerifier">
/// Kept only for the code exchange — never sent anywhere until then, which is the entire point
/// of PKCE (RFC 7636): a public client with no client secret can still prove it originated the
/// request it is now redeeming.
/// </param>
/// <param name="State">
/// An opaque value round-tripped through Spotify. The caller must reject a callback whose
/// <c>state</c> does not match — accepting it is what lets a stray or forged redirect complete
/// a login it did not initiate.
/// </param>
public sealed record SpotifyLoginChallenge(Uri LoginUri, string CodeVerifier, string State);

/// <summary>
/// Builds the PKCE login URL and exchanges what comes back for a token — the pure-ish half of
/// the flow, kept separate from actually catching the redirect (<see cref="SpotifyLoopbackListener"/>)
/// and opening a browser, so it is testable against a stub <see cref="ISpotifyOAuthClient"/>
/// alone.
/// </summary>
/// <remarks>
/// Follows the SDK's own documented PKCE sequence exactly: <c>PKCEUtil.GenerateCodes()</c> for
/// the verifier/challenge pair, a <see cref="LoginRequest"/> with <c>S256</c> as the challenge
/// method, then <see cref="PKCETokenRequest"/> to redeem the code. Nothing here is Offstream
/// policy — it is what the SDK requires, reproduced faithfully.
/// </remarks>
public sealed class SpotifyPkceFlow(ISpotifyOAuthClient oauthClient)
{
    /// <summary>Generates fresh PKCE codes and builds the URL to send the user to.</summary>
    public static SpotifyLoginChallenge CreateLoginChallenge(SpotifyAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var (verifier, challenge) = PKCEUtil.GenerateCodes();
        var state = Guid.NewGuid().ToString("N");

        var request = new LoginRequest(options.RedirectUri, options.ClientId, LoginRequest.ResponseType.Code)
        {
            CodeChallengeMethod = "S256",
            CodeChallenge = challenge,
            State = state,
            Scope = [.. options.Scopes],
        };

        return new SpotifyLoginChallenge(request.ToUri(), verifier, state);
    }

    /// <summary>Redeems the code from a successful callback for an access and refresh token.</summary>
    public Task<PKCETokenResponse> ExchangeCodeAsync(
        SpotifyAuthOptions options,
        SpotifyLoginChallenge challenge,
        string code,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var request = new PKCETokenRequest(options.ClientId, code, options.RedirectUri, challenge.CodeVerifier);

        return oauthClient.RequestTokenAsync(request, cancellationToken);
    }

    /// <summary>Trades a refresh token for a new access token, without a fresh browser round trip.</summary>
    public Task<PKCETokenResponse> RefreshAsync(
        SpotifyAuthOptions options, string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var request = new PKCETokenRefreshRequest(options.ClientId, refreshToken);

        return oauthClient.RequestTokenAsync(request, cancellationToken);
    }
}
