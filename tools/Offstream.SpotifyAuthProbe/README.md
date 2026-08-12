# Offstream.SpotifyAuthProbe

Manual verification for the exit criterion in `docs/MODERNIZATION-PLAN.md` §10 Phase 4:
> manual Spotify auth verified against a real app registration

Nothing automated can complete an interactive OAuth sign-in against Spotify's real servers, so
this is the tool a human runs once to prove the PKCE loopback flow (`Offstream.Core.Spotify.Auth`)
actually works end to end — browser redirect, code exchange, an authenticated API call, and a
token refresh.

## Setup (one time)

1. Go to the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard) and create an app.
2. In its settings, add `http://127.0.0.1:4002/callback` as a Redirect URI. Spotify does not
   accept bare `localhost` — it must be the literal loopback IP.
3. Copy the app's **Client ID** (not the client secret — PKCE does not use one).

## Running it

```powershell
dotnet run --project tools\Offstream.SpotifyAuthProbe -- <client-id>
```

or

```powershell
$env:OFFSTREAM_SPOTIFY_CLIENT_ID = "<client-id>"
dotnet run --project tools\Offstream.SpotifyAuthProbe
```

A browser opens to Spotify's sign-in page. Approve access, and the tool prints the token
details, fetches whatever is currently playing (or reports that nothing is, which is still a
successful call), and refreshes the token to prove that grant works too.

Play something in Spotify first if you want to see the "now playing" line succeed with a track
rather than the empty-playback message — either way is a pass.
