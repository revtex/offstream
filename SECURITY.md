# Security policy

## Reporting a vulnerability

**Use [private vulnerability reporting](https://github.com/revtex/offstream/security/advisories/new)** —
the *Report a vulnerability* button on the repository's Security tab. It opens a private thread with
the maintainer, and it is enabled on this repository specifically so a report does not have to start
in public.

Please do not open a public issue for anything exploitable. An issue is the right place for a bug
that is merely wrong; it is the wrong place for one that is wrong in a way somebody can use.

Useful in a report: what you did, what happened, and what an attacker gets out of it. A crash that
needs the attacker to already have the user's Windows account is a much smaller problem than one
reachable from a Spotify track title, and saying which it is saves a round trip.

Offstream is maintained by one person and has not shipped a release yet. Expect a first reply within
a week; expect a fix to land on `main` rather than in a patch release, because there is nothing to
patch yet.

## What is supported

| Version | Supported |
| --- | --- |
| `main` | Yes |
| Anything else | No — there are no releases yet |

## What the app handles that is worth attention

Offstream records local audio and writes files. It has no server, accepts no inbound connections
except one loopback redirect during sign-in, and sends nothing anywhere unless a metadata provider
is configured. The parts where that stops being boring:

- **Spotify track metadata is untrusted input.** It arrives from window titles and the system media
  transport, and it reaches ffmpeg arguments, file names and directory paths. Encoding uses
  `ProcessStartInfo.ArgumentList`, never a command string, so argument injection is prevented
  structurally rather than by escaping; path assembly rejects traversal segments and budgets path
  length. A way past either is a real finding.
- **OAuth.** Sign-in is Authorization Code with PKCE against `http://127.0.0.1:4002/callback`. There
  is no client secret, by design — a desktop app cannot keep one. The `state` parameter is validated
  on the redirect.
- **Token storage.** The Spotify refresh token is encrypted with DPAPI, scoped to the current
  Windows user, and the access token never reaches disk. A 401 clears the stored refresh token
  rather than retrying it.
- **The routing interop** talks to an undocumented COM interface (`IAudioPolicyConfig`) and marshals
  HSTRINGs by hand. Memory-safety mistakes there are plausible in a way they are not in the rest of
  a managed codebase.

## Known, and deliberate

- **The Last.fm API key is stored in plain text** in `%APPDATA%\Offstream\settings.json`. It is a
  read-only key for a public catalogue, it is the user's own rather than one shipped by Offstream,
  and the file sits in a per-user directory. DPAPI covers the Spotify refresh token, which actually
  grants something.
- **Recordings and their tags are written unencrypted.** That is what the app is for.
- **Offstream ships no API keys, client IDs or other credentials.** If you find one committed here,
  that is a bug worth reporting — it should never happen, and secret scanning with push protection
  is enabled to keep it from happening quietly.
