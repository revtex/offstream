using SpotifyScopes = SpotifyAPI.Web.Scopes;

namespace Offstream.Core.Spotify.Auth;

/// <summary>
/// What a PKCE sign-in needs: the app's client ID, the redirect it is registered under, and the
/// permissions to ask for.
/// </summary>
/// <remarks>
/// This is the "options pattern" object plan §10 Phase 4 calls for — a plain POCO rather than
/// an <c>IOptions&lt;T&gt;</c>-flavoured type, because <c>Offstream.Core</c> otherwise has no
/// dependency on <c>Microsoft.Extensions.Options</c> and this alone is not reason to add one.
/// Binding it from configuration is the host's job (<c>Offstream.App</c>); once settings
/// persistence lands (plan §6, Phase 5), that is where <see cref="ClientId"/> comes from.
/// </remarks>
public sealed record SpotifyAuthOptions
{
    /// <summary>
    /// Spotify does not allow bare <c>localhost</c> as a redirect host — it must be an explicit
    /// loopback IP literal. https://developer.spotify.com/documentation/web-api/concepts/redirect_uri
    /// </summary>
    public static readonly Uri DefaultRedirectUri = new("http://127.0.0.1:4002/callback");

    /// <summary>
    /// The one scope the metadata layer needs, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Offstream makes exactly two calls: <c>GET /me/player/currently-playing</c>, which this
    /// scope covers, and <c>GET /albums/{id}</c>, which needs no user scope at all. Nothing here
    /// grants playback control — Offstream reads, it never drives Spotify.
    /// </para>
    /// <para>
    /// This asked for <c>user-read-playback-state</c> and <c>user-read-recently-played</c> as
    /// well, on the assumption a later feature would want them. Neither was ever called. A scope
    /// requested ahead of the feature that needs it is a permission the user is asked to grant for
    /// nothing, on a consent screen where the extra lines are indistinguishable from the one that
    /// is load-bearing. Ask again when something actually calls those endpoints.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> DefaultScopes = [SpotifyScopes.UserReadCurrentlyPlaying];

    /// <summary>The Client ID from the user's Spotify Developer Dashboard app.</summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Must exactly match a redirect URI registered on the dashboard app, port included.
    /// </summary>
    public Uri RedirectUri { get; init; } = DefaultRedirectUri;

    public IReadOnlyList<string> Scopes { get; init; } = DefaultScopes;
}
