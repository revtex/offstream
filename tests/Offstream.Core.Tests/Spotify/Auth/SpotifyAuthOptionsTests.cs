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
    /// Offstream reads what is playing and looks up its album. The first needs this scope, the
    /// second needs none — so this is the whole list, and anything added here has to be justified
    /// by an endpoint that is actually called.
    /// </summary>
    [Fact]
    public void DefaultScopes_AreTheMinimumTheAppActuallyUses() =>
        Assert.Equal(["user-read-currently-playing"], SpotifyAuthOptions.DefaultScopes);

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
