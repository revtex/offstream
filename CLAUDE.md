# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**Offstream** is a Windows Spotify audio recorder on **.NET 10 + WPF**, with **ffmpeg performing every audio conversion**. It succeeds **Spytify** (.NET Framework 4.6.1 / WinForms), which is retired and lives at `../spy-spotify`.

The two names are not interchangeable. "Offstream" is what is being built here; "Spytify" only ever means the app being retired.

**Current state: phases 0–6 are done** — recording pipeline, metadata (Last.fm and Spotify Web API, wired into recording and writing real tags and cover art), settings persistence, and the WPF shell (Record / Settings / Advanced tabs, tray, single-instance guard). Phase 7 (Windows integration polish) is next. `docs/MODERNIZATION-PLAN.md` is authoritative for architecture, phases, and acceptance criteria — read it before proposing work, and keep it updated when decisions change. Each phase's **findings** blocks there record what was learned building it, and are usually the fastest way to understand why something is the way it is.

## Offstream owns every name — this is the rule that gets broken by accident

Code carries over from the predecessor. **Names never do.** No namespace, type, file, folder, project, resource key, setting, path, mutex, or build artefact in this repo may contain `EspionSpotify`, `Spytify`, `spy-spotify`, or any other inherited identifier. A file copied from the old tree gets renamed and re-namespaced **in the same commit that introduces it** — never "cleaned up later".

- Namespaces are `Offstream.Core.*` / `Offstream.App.*`; projects are `Offstream.Core`, `Offstream.App`, `Offstream.Core.Tests`, `Offstream.UI.Tests`, `Offstream.FakeSpotify`.
- Settings live at `%APPDATA%\Offstream\settings.json`; logs at `%APPDATA%\Offstream\logs\`.
- The old form interface `IFrmEspionSpotify` appears here in no spelling — the core reports progress via events / `IProgress<T>`.
- `MapperID3` does not survive: ffmpeg writes every textual tag, and the remaining sliver — Ogg/Opus cover art — is `Offstream.Core.Metadata.CoverArtWriter`.
- User-visible strings count too: the predecessor's "Spy" tab is Offstream's **Record** tab; "spy options" are **detection options**.

Plan §0 holds the full table, and regression suite 7 (§9.2) is a test that fails the build on a forbidden identifier.

**There is no settings migration.** Offstream does not read `%LOCALAPPDATA%\Spytify\user.config` and ships no importer — clean slate with good first-run defaults (plan §6). Do not add one back.

**Provider credentials are the user's, never ours.** The predecessor shipped three of its own Last.fm API keys hard-coded in its source and picked one at random per run. Offstream asks for a key (`metadata.lastFmApiKey`) and a Spotify Client ID on the Settings page instead. Do not embed a key, a client ID, or any other borrowed credential in this repo — a shared one is rate-limited collectively and revocable by someone who is not the user, and it is not ours to distribute.

## The app being retired is a reference, not a dependency

The predecessor is at `../spy-spotify`. Nothing here builds against it, but it is the source of truth for behaviour that took years to get right. Before writing anything that parses Spotify titles, buffers audio, assembles paths, or renders filename templates, **read the existing implementation first** — those files encode edge cases that are not obvious and not documented anywhere else. Read it, port the logic, and give it Offstream names on the way in.

The single most valuable asset there is `EspionSpotify/Router/AudioPolicyConfigFactory*`: per-application audio routing via the **undocumented `IAudioPolicyConfig` interface**. Its *behaviour* transfers into `Offstream.Core.Interop.Routing` — but **not its marshalling**, and not verbatim. See below.

## The routing interop is a rewrite, not a copy (Phase 0 finding)

`IAudioPolicyConfig` is an **IInspectable-based WinRT activation factory**, not a plain COM object, and .NET 5 removed built-in WinRT support. Three things the reference implementation relies on are gone: `UnmanagedType.HString`, `UnmanagedType.IInspectable`, and `ComInterfaceType.InterfaceIsIInspectable` (casting an RCW to one now throws `PlatformNotSupportedException`).

The working .NET 10 approach — in `src/Offstream.Core/Interop/Routing/`, all Phase 0 checks green on Windows 11 build 26200:

- Declare the interface `InterfaceIsIUnknown`, with `IInspectable`'s three methods written out explicitly as the first slots so vtable offsets line up. **Every method needs `[PreserveSig]`**, including the nineteen reserved slots.
- Create and read HSTRINGs by hand (`WindowsCreateString` / `WindowsGetStringRawBuffer` / `WindowsDeleteString`).
- Take the activation factory as `IUnknown`.

Two corrections to earlier project lore, both verified: the 21H2 and downlevel interfaces **differ only by IID, not by vtable layout** (so bind by probing IIDs, not by OS build number); and routing, session mute and loopback capture **all work unelevated**.

Details are in `docs/decisions/0001-phase-0-retarget-spike.md`. **Offstream targets Windows 11 only** (decided 2026-08-11), so the downlevel IID path is kept purely as insurance against Microsoft changing this undocumented IID again — not for Windows 10. One residual gap: routing is proven by COM round-trip, not audibly, because this machine has a single render endpoint.

## Non-negotiable constraints

These are load-bearing; violating them breaks the app in ways that are not obvious at compile time.

- **Never enable NativeAOT.** The routing code depends on built-in COM interop (`ComImport`, `Marshal.GetDelegateForFunctionPointer`), which AOT does not support.
- **Never enable aggressive trimming.** WPF trims poorly and settings/localisation are reflection-driven. Publish self-contained and untrimmed.
- **`Offstream.Core` must not reference WPF or `System.Windows`.** The predecessor passed the form itself into its watcher and recorder; Offstream uses events or `IProgress<T>` instead.
- **All conversion goes through ffmpeg.** No NAudio.Lame, no bundled LAME DLLs.
- **Use `ProcessStartInfo.ArgumentList`, never a command string.** Track metadata comes from Spotify window titles and is untrusted. The argv array prevents argument injection structurally; the old app needed hand-written `CommandLineToArgvW` escaping because .NET Framework lacked `ArgumentList`.

## Spotify Web API rules

Set by the user on 2026-08-13 and binding like the constraints above. Most were already met; the two that were not — over-broad scopes and no rate-limit handling — were both invisible at compile time and silent at runtime, which is why they are written down rather than assumed.

- **Endpoints, parameters and field names come from the [OpenAPI schema](https://developer.spotify.com/reference/web-api/open-api-schema.yaml).** Never guessed. In practice `SpotifyAPI.Web` is the typed SDK and nothing here hand-rolls HTTP; check the schema before adding a call, not after.
- **[Authorization Code with PKCE](https://developer.spotify.com/documentation/web-api/tutorials/code-pkce-flow) for user data.** Client Credentials only for public non-user data. **Implicit Grant is banned** — it is deprecated. A desktop app is a public client, so there is no Client Secret to protect and none may be introduced.
- **Redirect URIs are HTTPS**, except `http://127.0.0.1` for local development. Never bare `localhost` (Spotify rejects it), never wildcards. `SpotifyAuthOptions.DefaultRedirectUri` is the one in use and a test pins its shape.
- **Request the minimum [scopes](https://developer.spotify.com/documentation/web-api/concepts/scopes) the shipped features need, never a scope for a feature that might arrive later.** Offstream makes exactly two calls: `/me/player/currently-playing`, which needs `user-read-currently-playing`, and `/albums/{id}`, which needs no user scope. That is the whole list, and `SpotifyAuthOptionsTests` fails the build if it grows. A scope requested ahead of its feature is a permission the user grants for nothing, on a consent screen where the spare lines look identical to the load-bearing one.
- **Tokens are stored protected, refreshed, and abandoned when dead.** DPAPI covers the refresh token; the access token never reaches disk. Spotify rotates the refresh token on every renewal, so the replacement must be persisted or a long-running install silently stops working. A **401** means the refresh token itself is gone — clear it and put the user back through sign-in rather than retrying it forever. Only 401: treating a rate limit or an outage that way would sign the user out over a transient fault.
- **On 429, honour `Retry-After` exactly; back off exponentially only where there is no such instruction.** Guessing shorter is what gets an application throttled harder. Never retry in a tight loop. `SpotifyRetryHandler` is the implementation — note that `SpotifyClientConfig.CreateDefault()` attaches **no** retry handler, so a client built without one treats rate limiting as fatal.
- **Log throttling at `Warning`.** The Record page's activity log shows Information and above, so anything quieter is invisible to everyone who has not gone looking. Transient 5xx stays at `Information` — it usually clears, and promoting it makes the Problems filter too noisy to read.
- **Quota and rate limit are different failures.** 429 is request-rate throttling. A **403** can mean the signed-in account is not on the dashboard app's allowlist *or* that the app has passed the user quota its mode allows — only the response body tells them apart, so quote it rather than replacing it.
- **No deprecated endpoints.** Prefer `/playlists/{id}/items` over `/playlists/{id}/tracks`, and `/me/library` over the type-specific library endpoints.
- **Handle every documented status code, and surface Spotify's own error message.** It is a better explanation than anything writable from a status code alone. A tagging fault is still always downgraded to "no metadata" — the status decides what the user is told, never whether the recording survives.
- **[Developer Terms](https://developer.spotify.com/terms): attribute content to Spotify, and never use the API to train machine-learning models on Spotify data.** Attribution lives on the Settings page beside the provider that requires it. The terms' caching clause is **deliberately out of scope** — writing tags and cover art permanently into recorded files is what this app is for, and that was the user's explicit call on 2026-08-13. Do not "fix" it.

## ffmpeg traps that have already bitten this project

Both were discovered the hard way in the predecessor. Cover them with tests, not assumptions.

- **Ogg/Opus stores tags at the _stream_ level**, not the container level. Verify with `ffprobe -show_entries stream_tags`. Using `-show_format` returns nothing and looks like the tags failed to write when they did.
- **Cover art** is a second input stream for MP3 (`-map 1:v -c:v mjpeg -disposition:v attached_pic`). ffmpeg's `METADATA_BLOCK_PICTURE` support for Ogg/Opus is weaker; TagLib# writes it correctly and is the documented fallback for that container.
- Drain `RedirectStandardError` **before** waiting on the process — a full stderr pipe deadlocks.

## Testing

The predecessor's **293 xUnit tests come across and are the safety net for the whole effort** — assertions unchanged, namespaces and fixtures renamed per the naming rule. Phase 2's exit criterion is 293/293 green on .NET 10 *before* any behaviour changes. An assertion failure there is a retarget defect, not an opportunity to redesign.

Beyond that: ffmpeg argv golden tests, encode-integration tests asserted with ffprobe, settings round-trip tests, ViewModel tests (the old UI had zero coverage), and a naming-hygiene test that fails on a forbidden identifier. No network calls in unit tests — the old suite had one test hitting the live Last.fm API that fails offline; it becomes a fixture. Run with analyzers as errors.

## Conventions

- SDK-style projects and `PackageReference` only. Projects are authored fresh, not converted — the old app used `packages.config` with manual `<Compile Include>` lists, so files added on disk were silently not compiled.
- Nullable reference types enabled.
- MVVM via CommunityToolkit.Mvvm source generators; no code-behind logic beyond wiring.
- Inline validation (`INotifyDataErrorInfo`), not modal dialogs.
- User-facing strings live in `Offstream.App/Resources/Strings.resx` (+ `.fr.resx`) with an en/fr key-parity test. Resource **keys are re-keyed for Offstream**; do not carry the predecessor's key names across.
