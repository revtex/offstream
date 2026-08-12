using System.Net;

namespace Offstream.Core.Spotify.Auth;

/// <summary>What arrived on the loopback redirect: a code, or Spotify's reason it declined.</summary>
public sealed record SpotifyCallback(string? Code, string? State, string? Error, string? ErrorDescription)
{
    public bool Succeeded => Error is null && Code is not null;
}

/// <summary>Catches the one HTTP request the PKCE redirect sends, then stops.</summary>
public interface ISpotifyLoopbackListener : IDisposable
{
    /// <summary>The redirect URI this listener is bound to.</summary>
    Uri RedirectUri { get; }

    /// <summary>
    /// Waits for the redirect. Cancelling aborts the listen — the browser tab is left open, but
    /// nothing here keeps waiting for it.
    /// </summary>
    Task<SpotifyCallback> WaitForCallbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>Something about the local half of the auth exchange went wrong.</summary>
public sealed class SpotifyAuthException : Exception
{
    public SpotifyAuthException()
    {
    }

    public SpotifyAuthException(string message) : base(message)
    {
    }

    public SpotifyAuthException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// A single-shot local HTTP server that catches the PKCE authorization redirect.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists so the app does not depend on EmbedIO.</b> <c>SpotifyAPI.Web.Auth</c>'s
/// <c>EmbedIOAuthServer</c> would do this too, but it still pulls in EmbedIO 3.5.2 as of
/// SpotifyAPI.Web 7.4.2 (verified against its nuspec) — and dropping EmbedIO is itself the
/// point of this phase (plan §8, §10 Phase 4). <see cref="HttpListener"/> is built into the
/// framework and does everything a one-shot redirect catcher needs.
/// </para>
/// <para>
/// Binding to a specific loopback host and port (never a wildcard prefix like
/// <c>http://+:port/</c>) is what lets this run unelevated: Windows only requires a URL ACL
/// reservation for wildcard bindings, not for a literal <c>127.0.0.1</c>.
/// </para>
/// <para>
/// A browser routinely asks for <c>/favicon.ico</c> on the same origin as the redirect. Those
/// requests get a bare 404 and the listener keeps waiting — only a request whose path matches
/// <see cref="RedirectUri"/> counts as the callback.
/// </para>
/// </remarks>
public sealed class SpotifyLoopbackListener : ISpotifyLoopbackListener
{
    private const string SuccessPage =
        "<!doctype html><meta charset=utf-8><title>Offstream</title>" +
        "<body style=font-family:sans-serif;text-align:center;padding-top:4em>" +
        "<p>Offstream is connected to Spotify. You can close this tab.</p>";

    private const string ErrorPage =
        "<!doctype html><meta charset=utf-8><title>Offstream</title>" +
        "<body style=font-family:sans-serif;text-align:center;padding-top:4em>" +
        "<p>Something went wrong connecting Offstream to Spotify. You can close this tab and try again.</p>";

    private readonly HttpListener _listener = new();
    private bool _disposed;

    public SpotifyLoopbackListener(Uri redirectUri)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);

        if (!redirectUri.IsLoopback)
        {
            throw new ArgumentException(
                $"Redirect URI '{redirectUri}' is not a loopback address.", nameof(redirectUri));
        }

        RedirectUri = redirectUri;
        _listener.Prefixes.Add(Prefix(redirectUri));
    }

    /// <inheritdoc />
    public Uri RedirectUri { get; }

    /// <inheritdoc />
    public async Task<SpotifyCallback> WaitForCallbackAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new SpotifyAuthException(
                $"Could not listen on {RedirectUri} — is another instance of Offstream already " +
                "waiting for a Spotify sign-in, or is the port in use by something else?", ex);
        }

        // Stop() is what actually aborts a pending GetContextAsync; a linked token alone would
        // only stop awaiting it; the listen would run on unobserved.
        await using var registration = cancellationToken.Register(() =>
        {
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }
        });

        while (true)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new SpotifyAuthException("The local sign-in listener stopped unexpectedly.", ex);
            }

            if (!IsCallbackRequest(context.Request.Url))
            {
                await RespondAsync(context.Response, HttpStatusCode.NotFound, ErrorPage);
                continue;
            }

            var callback = Parse(context.Request.Url!);
            await RespondAsync(
                context.Response,
                callback.Succeeded ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
                callback.Succeeded ? SuccessPage : ErrorPage);

            return callback;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ((IDisposable)_listener).Dispose();
    }

    private bool IsCallbackRequest(Uri? requested) =>
        requested is not null
        && string.Equals(requested.AbsolutePath, RedirectUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);

    private static SpotifyCallback Parse(Uri requested)
    {
        var query = System.Web.HttpUtility.ParseQueryString(requested.Query);

        return new SpotifyCallback(
            query["code"], query["state"], query["error"], query["error_description"]);
    }

    private static async Task RespondAsync(HttpListenerResponse response, HttpStatusCode status, string html)
    {
        try
        {
            var body = System.Text.Encoding.UTF8.GetBytes(html);

            response.StatusCode = (int)status;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = body.Length;

            await response.OutputStream.WriteAsync(body);
        }
        finally
        {
            response.Close();
        }
    }

    /// <summary>
    /// <see cref="HttpListener"/> prefixes are host/port only — the path is matched by hand in
    /// <see cref="IsCallbackRequest"/> — and must end in a trailing slash.
    /// </summary>
    private static string Prefix(Uri redirectUri) =>
        new UriBuilder(redirectUri) { Path = "/" }.Uri.ToString();
}
