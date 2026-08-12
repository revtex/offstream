using System.Net;
using System.Net.Sockets;
using Offstream.Core.Spotify.Auth;
using Xunit;

namespace Offstream.Core.Tests.Spotify.Auth;

/// <summary>
/// The hand-rolled redirect catcher that replaces <c>EmbedIOAuthServer</c> (plan §8, §10 Phase 4).
/// </summary>
/// <remarks>
/// These bind a real <see cref="System.Net.HttpListener"/> to a real loopback port and hit it
/// with a real <see cref="HttpClient"/> — no fakes, because the one thing worth proving about
/// this class is that the actual OS-level plumbing works without EmbedIO or admin rights.
/// </remarks>
public sealed class SpotifyLoopbackListenerTests
{
    /// <summary>
    /// Binding <c>HttpListener</c> takes a literal port, so the suite finds one the OS
    /// considers free via a throwaway <see cref="TcpListener"/> rather than hard-coding one
    /// that might already be taken on the machine running the tests.
    /// </summary>
    private static Uri FreeRedirectUri()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return new Uri($"http://127.0.0.1:{port}/callback");
    }

    [Fact]
    public async Task WaitForCallbackAsync_ParsesASuccessfulRedirect()
    {
        var redirectUri = FreeRedirectUri();
        using var listener = new SpotifyLoopbackListener(redirectUri);

        var waiting = listener.WaitForCallbackAsync();

        using var http = new HttpClient();
        var response = await http.GetAsync(new Uri($"{redirectUri}?code=abc123&state=xyz"));

        var callback = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(callback.Succeeded);
        Assert.Equal("abc123", callback.Code);
        Assert.Equal("xyz", callback.State);
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task WaitForCallbackAsync_ParsesAnErrorRedirect()
    {
        var redirectUri = FreeRedirectUri();
        using var listener = new SpotifyLoopbackListener(redirectUri);

        var waiting = listener.WaitForCallbackAsync();

        using var http = new HttpClient();
        _ = await http.GetAsync(
            new Uri($"{redirectUri}?error=access_denied&error_description=The+user+declined&state=xyz"));

        var callback = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(callback.Succeeded);
        Assert.Equal("access_denied", callback.Error);
        Assert.Equal("The user declined", callback.ErrorDescription);
        Assert.Null(callback.Code);
    }

    /// <summary>
    /// A browser routinely asks for <c>/favicon.ico</c> on the redirect's origin. That must not
    /// be mistaken for the callback — only a request to the exact registered path counts.
    /// </summary>
    [Fact]
    public async Task WaitForCallbackAsync_IgnoresRequestsToOtherPaths()
    {
        var redirectUri = FreeRedirectUri();
        using var listener = new SpotifyLoopbackListener(redirectUri);

        var waiting = listener.WaitForCallbackAsync();

        using var http = new HttpClient();
        _ = await http.GetAsync(new Uri(redirectUri, "/favicon.ico"));

        Assert.False(waiting.IsCompleted);

        _ = await http.GetAsync(new Uri($"{redirectUri}?code=abc123&state=xyz"));
        var callback = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("abc123", callback.Code);
    }

    [Fact]
    public async Task WaitForCallbackAsync_ThrowsWhenCancelledBeforeAnyRequestArrives()
    {
        using var listener = new SpotifyLoopbackListener(FreeRedirectUri());
        using var cancellation = new CancellationTokenSource();

        var waiting = listener.WaitForCallbackAsync(cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Constructor_RejectsANonLoopbackRedirectUri() =>
        Assert.Throws<ArgumentException>(() => new SpotifyLoopbackListener(new Uri("http://example.com/callback")));

    [Fact]
    public async Task WaitForCallbackAsync_AfterDisposeThrows()
    {
        var listener = new SpotifyLoopbackListener(FreeRedirectUri());
        listener.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => listener.WaitForCallbackAsync());
    }
}
