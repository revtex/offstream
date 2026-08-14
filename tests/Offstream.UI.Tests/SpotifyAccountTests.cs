using Offstream.App.Services;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// The one decidable part of <see cref="SpotifyAccount"/>: how a profile becomes the line naming
/// the signed-in account. Everything else in that class is a network call.
/// </summary>
public sealed class SpotifyAccountTests
{
    /// <summary>
    /// Both halves, because the name alone identifies nothing — two Spotify accounts can carry the
    /// same display name, which is the case that put the account on screen in the first place.
    /// </summary>
    [Fact]
    public void Describe_WithBoth_NamesTheAccountAndIdentifiesIt() =>
        Assert.Equal("Alex (alex_9x2)", SpotifyAccount.Describe("Alex", "alex_9x2"));

    /// <summary>Spotify returns a null display name for a profile that never set one.</summary>
    [Fact]
    public void Describe_WithNoDisplayName_FallsBackToTheId() =>
        Assert.Equal("alex_9x2", SpotifyAccount.Describe(null, "alex_9x2"));

    [Fact]
    public void Describe_WithNoId_StillNamesTheAccount() =>
        Assert.Equal("Alex", SpotifyAccount.Describe("Alex", null));

    /// <summary>Nothing to say beats saying something empty next to a working sign-in.</summary>
    [Fact]
    public void Describe_WithNeither_SaysNothing() =>
        Assert.Null(SpotifyAccount.Describe(null, null));

    /// <summary>
    /// Two accounts sharing a display name have to read differently, which is the whole point.
    /// </summary>
    [Fact]
    public void Describe_ForTwoAccountsSharingAName_TellsThemApart() =>
        Assert.NotEqual(
            SpotifyAccount.Describe("Alex", "alex_9x2"),
            SpotifyAccount.Describe("Alex", "alex1994"));
}
