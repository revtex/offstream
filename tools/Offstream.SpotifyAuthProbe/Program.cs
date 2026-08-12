using Offstream.Core.Spotify.Auth;
using SpotifyAPI.Web;

// Manual verification for plan §10 Phase 4's exit criterion: nothing automated can complete an
// interactive Spotify sign-in, so this is the tool a human runs once against a real app
// registration. See README.md in this folder for the two-minute setup.

var clientId = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("OFFSTREAM_SPOTIFY_CLIENT_ID");

if (string.IsNullOrWhiteSpace(clientId))
{
    Console.Error.WriteLine(
        """
        No Client ID given.

        Usage:
          dotnet run --project tools/Offstream.SpotifyAuthProbe -- <client-id>
          OFFSTREAM_SPOTIFY_CLIENT_ID=<client-id> dotnet run --project tools/Offstream.SpotifyAuthProbe

        Create an app at https://developer.spotify.com/dashboard, add
        http://127.0.0.1:4002/callback as a redirect URI, and pass its Client ID here.
        """);

    return 1;
}

var options = new SpotifyAuthOptions { ClientId = clientId };

var authenticator = new SpotifyAuthenticator(
    options,
    new SpotifyPkceFlow(new SpotifyOAuthClient()),
    new BrowserLauncher(),
    redirectUri => new SpotifyLoopbackListener(redirectUri));

try
{
    Console.WriteLine($"Redirect URI: {options.RedirectUri}");
    Console.WriteLine($"Scopes: {string.Join(", ", options.Scopes)}");
    Console.WriteLine();
    Console.WriteLine("Opening your browser to sign in to Spotify...");

    var result = await authenticator.AuthenticateAsync();

    Console.WriteLine();
    Console.WriteLine("Signed in.");
    Console.WriteLine($"  Token type:  {result.TokenType}");
    Console.WriteLine($"  Expires at:  {result.ExpiresAt:u}");
    Console.WriteLine($"  Has refresh: {!string.IsNullOrEmpty(result.RefreshToken)}");

    var client = new SpotifyClient(SpotifyClientConfig.CreateDefault(result.AccessToken, result.TokenType));

    Console.WriteLine();
    Console.WriteLine("Fetching what's currently playing...");

    var playback = await client.Player.GetCurrentlyPlaying(new PlayerCurrentlyPlayingRequest());

    Console.WriteLine(playback?.Item switch
    {
        FullTrack track => $"  Now playing: {string.Join(", ", track.Artists.Select(a => a.Name))} — {track.Name}",
        FullEpisode episode => $"  Now playing an episode: {episode.Name}",
        _ => "  Nothing is currently playing (that's fine — the API call itself succeeded).",
    });

    Console.WriteLine();
    Console.WriteLine("Refreshing the token...");

    var refreshed = await authenticator.RefreshAsync(result.RefreshToken);

    Console.WriteLine($"  Refresh succeeded; new token expires at {refreshed.ExpiresAt:u}.");
    Console.WriteLine();
    Console.WriteLine("All checks passed.");

    return 0;
}
catch (SpotifyAuthException ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Sign-in failed: {ex.Message}");
    return 1;
}
catch (APIException ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Spotify API call failed: {ex.Message}");
    return 1;
}
