using Offstream.Core.Spotify.Auth;
using Xunit;

namespace Offstream.Core.Tests.Spotify.Auth;

/// <summary>
/// The consent screen, asserted. Both of these are one careless edit away from asking the user
/// for more than the app uses, and neither fails a build on its own.
/// </summary>
public sealed class SpotifyAuthOptionsTests
{
    /// <summary>
    /// Offstream reads what is playing, looks up its album, and names the signed-in account. The
    /// first needs <c>user-read-currently-playing</c>, the second needs none, and the third needs
    /// both of the others — so this is the whole list, and anything added here has to be justified
    /// by an endpoint that is actually called.
    /// </summary>
    /// <remarks>
    /// The schema also puts <c>user-read-email</c> on <c>GET /me</c>, and it is deliberately not
    /// taken: Spotify removed the <c>email</c> field in its late-2024 cull, so that scope now
    /// covers data the endpoint no longer returns. The account id distinguishes two same-named
    /// accounts just as well and costs no permission at all.
    /// </remarks>
    [Fact]
    public void DefaultScopes_AreTheMinimumTheAppActuallyUses() =>
        Assert.Equal(
            ["user-read-currently-playing", "user-read-private"],
            SpotifyAuthOptions.DefaultScopes);

    /// <summary>
    /// A permission over a field Spotify has removed is a permission for nothing — the exact thing
    /// the minimum-scopes rule exists to prevent, and easy to take by following the schema alone.
    /// </summary>
    [Fact]
    public void DefaultScopes_DoNotAskForTheRemovedEmailField() =>
        Assert.DoesNotContain("user-read-email", SpotifyAuthOptions.DefaultScopes);

    /// <summary>
    /// Nothing in Offstream drives playback, and a scope granting it would be visible to the user
    /// as a permission the app never exercises.
    /// </summary>
    [Fact]
    public void DefaultScopes_GrantNoControlOverPlayback() =>
        Assert.DoesNotContain(
            SpotifyAuthOptions.DefaultScopes,
            scope => scope.StartsWith("user-modify", StringComparison.Ordinal)
                || scope.StartsWith("playlist-modify", StringComparison.Ordinal)
                || scope.StartsWith("ugc-", StringComparison.Ordinal));

    /// <summary>
    /// Spotify rejects a bare <c>localhost</c> redirect; it has to be the loopback IP literal.
    /// Plain HTTP is allowed only because this is loopback.
    /// </summary>
    [Fact]
    public void DefaultRedirectUri_IsAnExplicitLoopbackLiteral()
    {
        Assert.Equal("127.0.0.1", SpotifyAuthOptions.DefaultRedirectUri.Host);
        Assert.True(SpotifyAuthOptions.DefaultRedirectUri.IsLoopback);
    }

    [Fact]
    public void Options_DefaultToTheSharedRedirectAndScopes()
    {
        var options = new SpotifyAuthOptions { ClientId = "client" };

        Assert.Equal(SpotifyAuthOptions.DefaultRedirectUri, options.RedirectUri);
        Assert.Equal(SpotifyAuthOptions.DefaultScopes, options.Scopes);
    }
}
