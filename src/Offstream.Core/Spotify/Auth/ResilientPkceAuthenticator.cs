using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;

namespace Offstream.Core.Spotify.Auth;

/// <summary>
/// Wraps <see cref="PKCEAuthenticator"/> so it keeps hold of the refresh token when Spotify's
/// renewal response does not carry one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this exists for.</b> The SDK stores exactly one token — the instance handed to its
/// constructor, reachable as <see cref="PKCEAuthenticator.InitialToken"/> — and renews by writing
/// the response's fields onto it in place. Spotify's PKCE renewal is not obliged to return a new
/// <c>refresh_token</c>, and when it omits one that null is copied over the good value. The first
/// renewal still succeeds, so nothing looks wrong; the *next* one throws
/// <c>ArgumentException: String is empty or null (Parameter 'refreshToken')</c> from inside the
/// SDK, and every lookup for the rest of the session fails the same way.
/// </para>
/// <para>
/// The symptom is distinctive, and was reported as warnings with "no reason on why": tagging works
/// for exactly one hour — the access token's lifetime — then stops until the app restarts. One log
/// showed a client built at 15:47:56 failing from 16:49:00, and another built at 18:35:33 failing
/// from 19:37:49.
/// </para>
/// <para>
/// <b>The repair.</b> A refresh token stays valid until it is revoked or replaced, so the last one
/// Spotify actually sent is still good: this remembers it and puts it back before each request, and
/// an omitted <c>refresh_token</c> becomes a no-op instead of a session-ending loss. Renewal itself
/// is left alone — expiry checks, the token request and <see cref="TokenRefreshed"/> all stay the
/// SDK's, and only the one field it mishandles is guarded.
/// </para>
/// <para>
/// Composition rather than a subclass because <see cref="PKCEAuthenticator.Apply"/> implements its
/// interface without being virtual, so there is nothing to override. Wrapping works because the
/// token instance is shared: repairing <see cref="PKCEAuthenticator.InitialToken"/> from out here
/// repairs the very object the SDK is about to read.
/// </para>
/// </remarks>
public sealed class ResilientPkceAuthenticator : IAuthenticator
{
    private readonly PKCEAuthenticator _inner;
    private string? _lastKnownGood;

    public ResilientPkceAuthenticator(string clientId, PKCETokenResponse initialToken)
    {
        ArgumentNullException.ThrowIfNull(initialToken);

        _inner = new PKCEAuthenticator(clientId, initialToken);
        _lastKnownGood = initialToken.RefreshToken;
    }

    /// <summary>Raised by the SDK whenever it renews, forwarded so callers can persist the result.</summary>
    public event EventHandler<PKCETokenResponse> TokenRefreshed
    {
        add => _inner.TokenRefreshed += value;
        remove => _inner.TokenRefreshed -= value;
    }

    /// <summary>The live token. The SDK mutates this instance in place on every renewal.</summary>
    public PKCETokenResponse Token => _inner.InitialToken;

    /// <inheritdoc />
    public async Task Apply(IRequest request, IAPIConnector apiConnector)
    {
        RestoreRefreshToken();

        await _inner.Apply(request, apiConnector);

        RememberRefreshToken();
    }

    /// <summary>
    /// Puts the last refresh token Spotify sent back on the token, if a renewal blanked it.
    /// </summary>
    /// <remarks>
    /// Before the request rather than after the renewal: the SDK reads the field while building the
    /// renewal request, so by the time anything of ours could observe the damage, the call that
    /// needed the value has already thrown.
    /// </remarks>
    public void RestoreRefreshToken()
    {
        if (!string.IsNullOrEmpty(Token.RefreshToken)) return;
        if (string.IsNullOrEmpty(_lastKnownGood)) return;

        Token.RefreshToken = _lastKnownGood;
    }

    /// <summary>Records a refresh token Spotify has just sent, so a later renewal can restore it.</summary>
    public void RememberRefreshToken()
    {
        if (string.IsNullOrEmpty(Token.RefreshToken)) return;

        _lastKnownGood = Token.RefreshToken;
    }
}
