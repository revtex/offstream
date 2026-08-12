using SpotifyAPI.Web;

namespace Offstream.Core.Spotify.Auth;

/// <summary>A completed sign-in: enough to build a <see cref="SpotifyClient"/> and to refresh later.</summary>
/// <param name="AccessToken">Bearer token for API calls.</param>
/// <param name="TokenType">Always <c>Bearer</c> in practice; carried through rather than assumed.</param>
/// <param name="RefreshToken">Redeems a new access token without another browser round trip.</param>
/// <param name="ExpiresAt">When <see cref="AccessToken"/> stops working.</param>
public sealed record SpotifyAuthResult(string AccessToken, string TokenType, string RefreshToken, DateTimeOffset ExpiresAt)
{
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    internal static SpotifyAuthResult From(PKCETokenResponse response) => new(
        response.AccessToken,
        response.TokenType,
        response.RefreshToken,
        new DateTimeOffset(response.CreatedAt, TimeSpan.Zero).AddSeconds(response.ExpiresIn));
}

/// <summary>
/// Runs one PKCE sign-in end to end: builds the login URL, opens the browser, catches the
/// redirect, and redeems the code.
/// </summary>
/// <remarks>
/// <para>
/// This is the orchestration the reference implementation's <c>SpotifyAPI</c> constructor and
/// <c>AuthOnAuthReceived</c> did inline, alongside the HTTP calls and the mapping — split out
/// here so each half is independently testable. <see cref="SpotifyPkceFlow"/> and
/// <see cref="ISpotifyLoopbackListener"/> are both interfaces or take interfaces, so this class's
/// own logic — state-mismatch rejection, timeout handling, error-callback handling — is
/// testable with fakes for both, no browser or real HTTP involved.
/// </para>
/// <para>
/// <b>The state check is not optional.</b> Skipping it (the reference implementation did) means
/// a stray redirect — a leftover browser tab from a previous attempt, or a maliciously crafted
/// one — could complete whatever sign-in happens to be waiting. It is not documented as
/// deliberately dropped anywhere in the plan, so this closes it rather than carrying it forward.
/// </para>
/// </remarks>
public sealed class SpotifyAuthenticator(
    SpotifyAuthOptions options,
    SpotifyPkceFlow flow,
    IBrowserLauncher browser,
    Func<Uri, ISpotifyLoopbackListener> listenerFactory)
{
    /// <summary>How long to wait for the user to complete the browser sign-in.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Runs the full sign-in: URL, browser, redirect, code exchange.</summary>
    /// <exception cref="SpotifyAuthException">
    /// The user was signed in outside the timeout, Spotify reported an error, the redirect's
    /// state did not match, or the local listener could not be started.
    /// </exception>
    public async Task<SpotifyAuthResult> AuthenticateAsync(
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var challenge = SpotifyPkceFlow.CreateLoginChallenge(options);

        using var listener = listenerFactory(options.RedirectUri);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? DefaultTimeout);

        var waitingForCallback = listener.WaitForCallbackAsync(deadline.Token);

        browser.Open(challenge.LoginUri);

        SpotifyCallback callback;

        try
        {
            callback = await waitingForCallback;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpotifyAuthException(
                $"Signing in to Spotify was not completed within {(timeout ?? DefaultTimeout).TotalMinutes:F0} minutes.");
        }

        if (callback.Error is not null)
        {
            throw new SpotifyAuthException(
                $"Spotify declined the sign-in: {callback.Error}" +
                (callback.ErrorDescription is null ? "" : $" ({callback.ErrorDescription})"));
        }

        if (!string.Equals(callback.State, challenge.State, StringComparison.Ordinal))
        {
            throw new SpotifyAuthException(
                "The sign-in redirect's state did not match the request that was sent; discarding it.");
        }

        if (callback.Code is null)
            throw new SpotifyAuthException("Spotify's redirect carried no authorization code.");

        var token = await flow.ExchangeCodeAsync(options, challenge, callback.Code, cancellationToken);

        return SpotifyAuthResult.From(token);
    }

    /// <summary>Trades a refresh token for a new access token, without a browser round trip.</summary>
    public async Task<SpotifyAuthResult> RefreshAsync(
        string refreshToken, CancellationToken cancellationToken = default)
    {
        var token = await flow.RefreshAsync(options, refreshToken, cancellationToken);

        return SpotifyAuthResult.From(token);
    }
}
