# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Offstream has not shipped a release yet; everything below is work towards the first one.
Because Offstream succeeds **Spytify** (.NET Framework 4.6.1 / WinForms) rather than starting
from nothing, the `Changed`, `Removed` and `Fixed` entries are written against that predecessor
— they say how Offstream differs from the app it replaces. `docs/MODERNIZATION-PLAN.md` is the
phase plan these entries follow.

## [Unreleased]

### Added

- **A release pipeline, with the git tag as the only place a version number lives.** Pushing `v1.2.3`
  builds, tests, publishes, signs and attaches a self-contained `win-x64` zip and its SHA-256 to a
  GitHub release; the changelog becomes the release notes. Nothing in the repository records a
  released version, so a build cannot claim a number the tag disagrees with — a file that has to be
  bumped in lockstep with a tag is a file that eventually is not. A malformed tag is rejected before
  anything is built, because a tag stops being editable the moment anyone fetches it, and a
  prerelease suffix (`v1.2.3-rc.1`) marks the GitHub release as one so it does not become what
  "latest" resolves to. Unreleased builds call themselves `0.1.0-dev` rather than borrowing the last
  release's number, which is what turns "which build is this?" into a question with an answer. The
  same workflow runs from the Actions tab to exercise the pipeline without spending a version.
  Cutting a release is two steps — close `## [Unreleased]` into `## [1.2.3]` in a pull request, then
  tag — and a tag with no matching section fails in seconds rather than at the end of the pipeline.
  Falling back to `[Unreleased]` would have worked exactly once, and every release after that would
  republish the previous one's entries with no fix short of amending a release people had read.
- **Signing, wired and waiting.** `build/windows/sign.ps1` Authenticode-signs whatever it is given,
  and when no certificate is configured it says so and exits 0 rather than failing the build.
  Offstream has no certificate yet, so **every artefact is currently unsigned and Windows SmartScreen
  will warn on first run** — the release notes say this outright instead of letting users find out,
  and ship a SHA-256 as the only integrity check available in the meantime. Building the step now
  means acquiring a certificate later is two repository secrets rather than a pipeline change, and it
  gets reviewed while nothing depends on it. Timestamping is on by default: without it every
  signature stops verifying the day the certificate expires, including on copies installed years
  earlier.
- **A security policy, and the reporting channel it points at.** `SECURITY.md` says where to send a
  vulnerability and what the app actually handles that is worth attention — track metadata being
  untrusted input that reaches ffmpeg arguments and file paths, the PKCE sign-in, DPAPI token
  storage, and the hand-marshalled COM interop — along with what is deliberate, such as the Last.fm
  key sitting in plain text next to a DPAPI-protected refresh token. Private vulnerability
  reporting, Dependabot alerts and security updates, and secret scanning with push protection are
  enabled on the repository, so a reporter has somewhere private to go and a committed credential
  is refused at push time rather than found later.
- **CodeQL code scanning** on pushes to `main`, on pull requests, and weekly, over C#, the workflows
  themselves and the one Python script. It runs on Ubuntu with `build-mode: none`: the analysis
  reads C# without compiling it, which is what makes scanning a Windows-only WPF app on a Linux
  runner possible at all.
- **Solution scaffold on .NET 10.** SDK-style projects (`Offstream.Core`, `Offstream.App`,
  `Offstream.Core.Tests`, `Offstream.UI.Tests`, `Offstream.FakeSpotify`) under an `.slnx`
  solution, central package management, nullable reference types, and analyzers as errors.
- **GitHub Actions CI** on `windows-latest`: build, `dotnet format --verify-no-changes`,
  analyzers as errors, and the test suite with ffmpeg pinned. A parallel job fails any pull
  request that does not update this file, since a stale changelog reads as a current one; a
  user-invisible change skips it with the `no-changelog` label rather than by staying silent.
- **`build.ps1`** — restore, build, test, format, publish and run from an ordinary PowerShell
  prompt, with no Developer PowerShell or MSBuild discovery needed.
- **Per-application audio routing on .NET 10** (`Offstream.Core.Interop.Routing`), over the
  undocumented `IAudioPolicyConfig` interface. Routing, session mute and loopback capture are
  all verified working unelevated.
- **WASAPI loopback capture** (`Audio.LoopbackAudioCapture`) behind `IAudioCaptureSource`, with
  a silence keep-alive so idle gaps are still captured, and `Audio.AudioCaptureBuffer` pacing
  reads between capture and the recorder.
- **Spotify track detection** — window-title parsing, process discovery, and a
  cancellation-driven poller that reports track, play-state and elapsed-time changes.
- **The recording pipeline** — `Recording.TrackRecorder` captures one track to a temporary WAV
  and decides whether it is worth keeping; `Recording.RecordingSession` joins capture,
  detection, the recording rules, encoding and file naming, and reports progress through
  `IProgress<RecordingProgress>`.
- **ffmpeg encoding boundary** (`Offstream.Core.Encoding`) — declarative format profiles, an
  `ArgumentList`-based runner with a deadline and stderr draining, an `FFmpegLocator` that
  resolves configured → bundled → `PATH`, a startup version assertion, and a single-consumer
  `EncodeBacklog` so capture never waits on encoding.
- **FLAC and AAC output**, alongside MP3, WAV and Opus.
- **Cover art for every container that can carry it** — attached as a second ffmpeg input for
  MP3, FLAC and M4A, and written by TagLib# for Ogg/Opus, where ffmpeg's
  `METADATA_BLOCK_PICTURE` support is unreliable.
- **Filename templates** with folder levels, a counter, and 260-character path budgeting.
- **Last.fm metadata mapping**, driven by fixtures rather than the live API.
- **ffmpeg argument golden tests and encode-integration tests** that assert with `ffprobe`, plus
  a naming-hygiene test that fails the build on an identifier inherited from the predecessor.
- **Spotify Web API metadata, on PKCE.** `SpotifyTrackMapper` maps `FullTrack`/`FullAlbum` onto
  a `Track`; `SpotifyMetadataProvider` fetches the currently-playing track and its album and
  applies the mapping, guarded by the same title-match check the predecessor used to stop a
  race between detection and enrichment from tagging the wrong track. The PKCE sign-in itself —
  login URL, code exchange, refresh — is `Spotify/Auth/*`, orchestrated by
  `SpotifyAuthenticator` against `SpotifyAPI.Web` 7.4.2.
- **A loopback redirect listener with no EmbedIO dependency.** `SpotifyLoopbackListener` catches
  the PKCE authorization code on `System.Net.HttpListener`, bound to a literal loopback address
  so it runs without elevation.
- **`tools/Offstream.SpotifyAuthProbe`**, a console tool for the one verification step nothing
  automated can do: running the real PKCE flow against a real Spotify app registration in an
  actual browser.
- **`IHttpClientFactory`-backed DI wiring** for the Spotify OAuth client, and the options
  pattern (`SpotifyAuthOptions`) plan §10 Phase 4 asks for.
- **Settings persistence** at `%APPDATA%\Offstream\settings.json` — a schema grouped into
  `output`, `recording`, `metadata` and `app` sections, with a `schemaVersion`, validation on
  load, and first-run defaults chosen so the app is usable before Settings is ever opened.
- **Atomic settings writes.** The file is written to a sibling temp file and moved over the
  destination, so a crash mid-save leaves either the old file or the new one — never a
  half-written one.
- **DPAPI protection for the Spotify refresh token** (`CurrentUser` scope) before it reaches
  disk. A token that will not decrypt — a copied file, a different Windows user — is treated as
  "sign in again" rather than as a corrupt settings file, so every other preference survives.
- **The WPF shell** — a Fluent `NavigationView` with Record, Settings and Advanced tabs, pages
  and ViewModels resolved from the DI container, dark mode following the system theme, and
  high-contrast schemes mapped rather than flattened to light.
- **A live waveform on the Record page.** Nothing else on that page proves audio is arriving:
  status, track and elapsed all come from Spotify's window title, which keeps changing whether
  or not a sample reaches the encoder. The meter is drained by the UI at a fixed 30 Hz rather
  than pushed from capture, so a decoration costs the capture thread nothing.
- **An activity log on the Record page** with a level filter and copy-what-you-see, replayed
  from the in-memory sink so it is populated before the page is first shown.
- **Settings and Advanced pages** covering output folder, device, format, bitrate, minimum
  length, metadata provider, filename template with a token reference and a live preview
  rendered by the recorder's own naming code, existing-file policy, counter, detection options,
  tag options, timer, language and the ffmpeg path.
- **Inline validation via `INotifyDataErrorInfo`, and no OK button.** A valid edit is saved when
  the field commits; an invalid one is refused next to the field and never reaches the disk.
- **English and French resources** with a key-parity test, and a test that fails the build if a
  resource key carries an identifier inherited from the predecessor.
- **A tray icon with a menu**, shown only while the window is hidden there, coloured red while a
  recording is running — the one state the user cannot otherwise see.
- **A single-instance guard that surfaces the running window** instead of exiting silently. The
  claim is per logon session and per data directory, so a second Windows user gets their own
  Offstream, and `OFFSTREAM_HOME` relocates settings for portable use and for the UI suite.
- **Metadata actually reaches the file.** A `Last.fm` provider (`LastFmMetadataProvider`) and the
  existing Spotify one are selected from the settings, run against each track as it starts
  recording, and joined immediately before the encode is queued — so album, track number, disc,
  year, genre and album artists reach ffmpeg's `-metadata` arguments, and the cover art is fetched
  to a temp file and embedded. Enrichment overlaps the recording rather than following it, is
  bounded by a deadline, and can never fail a recording.
- **Spotify sign-in, on the Settings page.** The Client ID identifies an app and grants nothing;
  this is what produces the refresh token a recording session presents. Spotify rotates that token
  on every renewal, and the replacement is written back.
- **A Last.fm API key setting** (`metadata.lastFmApiKey`), the user's own. The predecessor shipped
  three of its own keys hard-coded in its source and picked one at random per run.
- **Tags the predecessor never wrote.** Genres from Last.fm's top tags (the three most-applied,
  since the tail of a tag cloud is listener bookkeeping rather than genre); the release date at
  Spotify's own precision, alongside the year the `{year}` token needs; the album's track total,
  so the track tag reads `4/12` and a player can tell a partial rip from a complete album; and
  the album's copyright line, preferring the recording's over the composition's.
- **Audio endpoints appearing and disappearing are handled.** Losing the endpoint mid-recording
  was silent — WASAPI simply stops delivering.
- **Extended-length path support.** Offstream does not write the output file, ffmpeg does, in a
  separate process with its own manifest, so a `longPathAware` opt-in here would not reach it.
  The `\\?\` prefix travels with the path through `ArgumentList` instead. It is applied only to
  fully-qualified, already-normalised paths, because the prefix turns path normalisation off —
  Windows stops resolving `.` and `..`, converting `/`, and trimming trailing dots and spaces,
  so an untidy path becomes one the filesystem rejects. Per-level allowances stay clamped to
  255: extended paths raise the total length, not the component length.
- **VB-CABLE detection, reported beside the device picker.** Detection only, and that is a
  licence decision rather than an unfinished one — the package is donationware whose readme
  forbids integrating it into another installation procedure without the author's agreement.
  Offstream carries no vendor binaries and links to vb-audio.com when the cable is absent.
  Plan open question 9 has to be answered before any other form ships.
- **The activity log is a tab of its own**, with a level filter and copy-what-you-see. Third
  home in three attempts: a log wants either the whole surface or none of it, and a shared page
  offers neither.
- **The Record page shows what it has done** — tracks saved this session with a reveal button
  per row, and the cover art, album and destination for the track being recorded. All of it was
  already known and thrown away: the enricher fetches the art to embed it, and the destination
  is rendered to decide where to write.
- **Genre from Spotify's artists, with Last.fm behind it.** Spotify has no genre on a track at
  any endpoint — it models genre as an attribute of the artist — so `/v1/artists/{id}` is the
  one place the data still lives. It needs no new scope, since artist data is public, and is
  cached per session by artist id, misses included. Artist genres describe a body of work rather
  than a recording, which is the honest limitation and the reason for a second rung: Last.fm's
  tags are per track, then per artist, then empty.
- **The album's track total from the Windows media session.** Spotify reports it alongside the
  track number and only the second was being read, so the `5/12` track tag needed a configured
  provider even though the client had volunteered both halves.
- **A summary of what each metadata provider contributes**, under the Settings dropdown. It
  named three providers and said nothing about how they differ, leaving the one question worth
  asking — what do I lose by picking this one — answerable only by recording something and
  running `ffprobe` over it. Phrased as what each provider *adds*, because none of them is the
  floor; only Spotify carries a release date, so `{year}` is empty under either of the others.
- **1051 tests** (877 Core, 174 UI).

### Changed

- **`LICENSE` is now unbroken MIT text, and the attribution moved to `NOTICE`.** The predecessor's
  copyright notice sat mid-licence, between the header and the permission grant, which is enough to
  stop GitHub's detector matching the file — so a repository under a perfectly ordinary MIT licence
  advertised itself as "Other", which invites exactly the doubt a licence file exists to remove.
  Both copyright lines are still in `LICENSE`, as the licence requires; `NOTICE` carries the prose
  about what was ported, and now also lists every third-party component with its licence —
  including that **TagLibSharp is LGPL-2.1-only**, which is a packaging constraint for phase 8 and
  was written down nowhere.
- **The README now explains the app before it explains the build.** It opened on a phase list and
  a retarget spike, so someone arriving at the repository could read half of it without learning
  what Offstream does, that it splits a listening session into one tagged file per song, or what
  the window looks like — while the two facts they most needed were absent: there is no installer
  yet, and recording an ordinary output device captures every sound the machine makes, not only
  Spotify. Screenshots of the Record, Settings and Advanced pages now sit beside a walkthrough
  from install to first recording, and the developer material — setup, `build.ps1`, IDEs, testing,
  the naming rule, the relationship to the predecessor — is intact below, in collapsed sections.
  The status line was two phases out of date.
- **SpotifyAPI.Web upgraded 5.1.1 → 7.4.2.** `SpotifyWebAPI` and `AuthorizationCodeAuth` are
  gone; auth is PKCE end to end, which needs no client secret at all — a public desktop app was
  never able to keep one confidential regardless of how it was stored.
- **The PKCE sign-in validates its `state` parameter.** The predecessor did not; skipping that
  check is how a stray or forged redirect could complete a sign-in it did not originate.
- **All audio conversion goes through ffmpeg** as an external process, replacing in-process
  encoding.
- **ffmpeg arguments are passed as an argument vector**, never a command string. Track metadata
  comes from Spotify window titles and is untrusted; passing argv elements individually removes
  the argument-injection class of bug structurally, where the predecessor needed hand-written
  `CommandLineToArgvW` escaping.
- **Encoding runs on a background queue** instead of on the recording thread, so a slow encode
  can no longer clip the start of the following track.
- **A recording's length is measured from the audio captured**, not from a timer tick, so a
  stalled capture can no longer pass the minimum-length rule with a near-empty file.
- **The destination filename is claimed after encoding**, immediately before the file is moved
  into place, so a name reserved while an encode was queued cannot be taken in the meantime.
- **A failed encode keeps its captured WAV** and reports the path, rather than discarding the
  one part of the recording that cannot be recreated.
- **The recording rules are separated from the orchestration**, and the orchestration reports
  progress through events and `IProgress<T>` rather than holding a reference to the UI.
- **Capture pacing is separated from the capture device**, so it can be tested without an audio
  endpoint.
- **The silence keep-alive targets the endpoint being captured** rather than the default output
  device, which is the wrong one whenever a user records anything else.
- **Polling is cancellation-driven** (`PeriodicTimer` plus a `CancellationToken`) instead of
  using `System.Timers.Timer` with an `async void` handler, a fire-and-forget event raise, and a
  `bool` re-entrancy guard.
- **Windows 11 (build 22000) is the floor.** Windows 10 is no longer supported.
- **Settings are validated on load and rejected loudly.** Malformed JSON, an unknown
  `schemaVersion`, or an out-of-range value produces a message naming the offending field rather
  than silently reverting to defaults and discarding the user's configuration.
- **Settings live at `%APPDATA%\Offstream\settings.json`** as JSON, replacing the .NET
  `user.config` mechanism.
- **The target framework is `net10.0-windows10.0.22621.0`.** SMTC lives in
  `Windows.Media.Control`, which only exists to bind against when the CsWinRT projections are
  on, and those are switched on by the TFM carrying a version. 22621 rather than Windows 11's
  22000 floor: it is the oldest build still in support. The hand-rolled `IAudioPolicyConfig`
  interop is unaffected by construction — it names its runtime class only as a string and
  otherwise uses `ComImport` and raw P/Invoke — and was re-verified on build 26200 regardless.
- **The Windows media session is the primary track source**, with the window title as fallback.
  The title is only readable while Spotify has a window; minimised to the tray it has none, and
  detection stopped dead — the recorder could sit idle through a whole session because the user
  tidied the taskbar. It is also better information: the title is one string that has to be split
  on a separator that can legitimately appear inside either half, while the media session hands
  over separate fields and an album with them.
- **Metadata precedence is the provider first, the media session underneath.** Not "client first
  with the API as fallback", which sounds equivalent and is not — the two have asymmetric
  coverage, and the API is the only source for half the tag set. The provider wins wherever it
  answers; what the client already knew fills the gaps.
- **The file-name template and its preview take the card's full width**, in the order they are
  used, with the preview in a recessed well of its own — it is the one thing in that card that
  is output rather than input.
- **Last.fm genres come from the artist, never the recording.** Last.fm does carry a per-track
  tag cloud, and it used to be asked first on the reasoning that a tag describing this recording
  beats one describing its performer. That is not the trade actually being made: Last.fm has no
  track tags at all for a great many recordings and returns an empty cloud rather than an error,
  so the first rung answered for some tracks on an album and not others — and a library where one
  track is "trance" and the next is untagged sorts worse than one where every track carries the
  artist's genre. It also puts the two providers on the same footing, since Spotify has no
  track-level genre to offer at all. One request per artist instead of two per track, out of the
  cache that was already there.
- **The recording display is backlit rather than reflective**, and no longer carries the track
  name. Pale grey with near-black segments was a calculator face and the palest object in a
  nearly black window. The track name moved to the card with the art and album it belongs to.

### Removed

- **NAudio.Lame and the bundled LAME DLLs** — ffmpeg encodes MP3.
- **Settings migration.** Offstream does not read the predecessor's `user.config` and ships no
  importer; it starts from a clean slate with first-run defaults.
- **MetroFramework, EmbedIO and DotNetZip**, along with `packages.config` and the manual
  `<Compile Include>` lists that silently omitted files added on disk.
- **Every identifier inherited from the predecessor.** No namespace, type, file, project,
  resource key, setting or path carries the old naming, and a test fails the build if one
  reappears.
- **The Phase 0 spike project** (`spike/Offstream.Spike`). It was scratch scaffolding for proving
  the risky parts of the retarget — WASAPI loopback, `IAudioPolicyConfig` routing, session mute —
  before there was anywhere else to put them, and everything it proved has lived in
  `Offstream.Core` with tests of its own since Phase 2. What remained was a hand-run console
  harness nothing built on, carried in the solution and the layout docs as though it were part of
  the app. `docs/decisions/0001-phase-0-retarget-spike.md` keeps the findings.

### Added

- **The Settings page names which Spotify account is signed in**, as display name and account id
  — not just that one exists. Recordings go untagged when the signed-in account is not the one
  the music plays on, and nothing on screen said which it was, so the only way to find out was
  to read the logs. Costs one scope, `user-read-private`, and existing installs show nothing
  here until they sign in again, since a refresh token predating the scope cannot read the
  profile. The id is there because a display name is not an identifier: two accounts can share
  one, and telling exactly those apart is what this is for. `user-read-email` would be the
  obvious way to do that and is deliberately not requested — Spotify removed the `email` field
  in its late-2024 cull, so that permission now covers data the endpoint no longer returns.

### Fixed

- **Pressing record with Spotify paused, then pressing play, recorded nothing.** The level meter
  moved, the counter ran and the page named the song, but no file was ever written until the user
  pressed stop and start again. Being played is half of what makes a track recordable, and that
  check ran only when the track changed — while two observations of the same song count as the
  same track whether or not it is playing, by design, so that a pause mid-song does not read as a
  new one. A song that started playing therefore raised nothing the session was listening for: it
  had already passed the track over once as not recordable, and went on waiting for a change that
  had happened. The predecessor never met this because it waited for Spotify to produce audio
  before it began watching at all; Offstream starts listening the moment record is pressed, which
  is worth keeping. The check now also runs when playback starts, for the track already showing
  and only when nothing is being recorded — a pause mid-song still leaves the recorder alone, and
  starting with music already playing still produces exactly one recording rather than one that is
  immediately torn down and discarded as too short.
- **Every track after the first showed nothing but the artist and title.** The album, cover art
  and destination arrived a second into each song and were gone a moment later, on every track
  except the first one of a session — while the files themselves were tagged correctly, so the
  lookup was plainly working and only the page was wrong. The cause was that a report from the
  pipeline named the track it was *about*, and the shell read that as the track playing. Encoding,
  tagging and saving happen to the previous song while the next one is already recording, so those
  reports named a track that had finished: the now-playing line snapped back to it, which was the
  signal to drop the album, art and path of the song that was recording, and the next elapsed tick
  a seventieth of a second later snapped it forward again and dropped them a second time. Nothing
  re-runs a lookup for a track already under way, so the card stayed bare for the rest of it. The
  first track escaped only because nothing was finishing behind it. Reports now carry what is
  playing separately from what they are about, and the page takes its now-playing line, elapsed
  counter and transport state from the former — which also stops the counter jumping to the
  previous track's length and the transport flickering out of Recording each time a file lands.
  While it was there: the page and the tray no longer announce a song called "Spotify". The poller
  seeds an empty track when it starts listening, so that whatever is already playing counts as a
  change, and Spotify's own idle window title parses to the same empty thing — both of which
  render as the bare application name. A placeholder for the absence of a track is now reported
  as no track.
- **Unplugging the device being recorded left the recording running against nothing.** The
  detection for this was built and correct — it notices both ways of losing an endpoint, the
  pinned device disappearing and Windows moving the default elsewhere — and nothing was listening
  to it, so the session never heard. What the user saw was a recording that went on, an elapsed
  counter that stopped counting, and a track that was never written, with the reason in no log.
  The session now ends when its capture does: the track in flight is finished and saved on the
  same terms as pressing stop mid-song, since what was captured before the device went is as much
  of it as will ever exist, and the endpoint's name is reported so it is clear which device to
  plug back in. Recording does not follow the audio to the new device — a replacement endpoint can
  have a different sample rate and channel count, and continuing into one would put a seam in the
  middle of the file — so plugging it back in and pressing record is the recovery.
- **A recording that stopped itself left the page offering to stop it.** The recording timer
  elapsing ended the session, but nothing told the window, so the transport went on showing Stop
  for a session that had already finished, the file counter that run reached was not written back
  until the user pressed it, and the audio device stayed open in the meantime. A session that ends
  on its own now says so, and is released exactly as a stopped one is.
- **Starting and stopping recording a few times crashed the app outright.** No dialog, no log
  line, no managed exception — the process disappeared and left an access violation in the
  Windows event log, because the fault was in the audio service calling back into the app rather
  than in anything the app was running. Each recording session registers an endpoint-notification
  callback with Windows so that unplugging the device being recorded ends the recording rather
  than silently capturing nothing; stopping a session closed the capture but never withdrew that
  registration, and the garbage collector cannot withdraw it either. Every cycle therefore left
  another live registration pointing at an object nothing was keeping alive, and the next time an
  endpoint changed — which is exactly what opening the next session's audio client does — the
  audio service called into freed interop memory. A session now disposes the capture it was
  given, which is what withdraws the registration, and a watcher holds a reference to itself for
  as long as Windows can still call it: registering deliberately does not count as a reference,
  which Windows documents and leaves to the caller, so a missed disposal now costs a few bytes
  instead of the app. Three start/stop cycles were enough to crash the old build; the fixed one
  went twenty-five without it. Two smaller leaks of the same shape went with it: a failed start —
  no ffmpeg, most often — no longer abandons the capture it had already opened, and the endpoint
  lookup no longer leaves a device enumerator behind on a path that runs several times a second
  while recording.
- **Losing the recorded device could have wedged the audio service rather than ending the
  recording.** Endpoint notifications ran their handlers on the thread Windows delivers them on,
  which is forbidden from blocking, from waiting, and from closing an audio object — and the
  handler for a lost endpoint does all three, against the endpoint that just disappeared, while
  the audio service waits for the call to return. Handlers now run on a pool thread, and the
  notification returns immediately.
- **An advertisement showed the previous song's cover art, album and save path.** The three
  details describing the track being written arrive together from the metadata lookup and were
  cleared only when the session stopped, so anything never enriched — an advertisement above
  all, but equally a lookup that found nothing — inherited the last song's and displayed them
  under its own name. Not a cosmetic fault: the card named a file path that nothing was being
  written to. They are now dropped the moment the displayed track changes.
- **The Spotify sign-in button said "Sign in to Spotify" beside the words "Signed in".** It
  offered to do something already done. Pressing it does have a use once an account is
  stored — it is the only way to move the install to a different account, which is exactly what
  someone whose recordings are coming back untagged needs — so it now says "Sign in as a
  different account" instead of being disabled or hidden. The button and its status line are
  also stacked rather than side by side: the row sized itself off whichever label was current
  and clipped the sentence beside it as soon as either grew, which the longer label and the
  French translation both do.
- **Spotify's own error message reached the log as `Exception of type
  'SpotifyAPI.Web.APIException' was thrown`.** The SDK parses the error body into
  `Exception.Message` only when it recognises the shape and leaves .NET's placeholder there
  otherwise, so the one string that could explain a failure was replaced by one that explains
  nothing — and a 403 naming the rejected account arrived as a warning listing two possible
  causes and confirming neither. The reason is now read off the response body, in both the
  shape the Web API sends and the shape the accounts service sends, and quoted.
- **A session Spotify will never answer for cost every recording thirty seconds.** The long
  retry budget was added so a free account's advertisement break would not cost the following
  track its tags, on the assumption that an answer carrying no track always resolves by itself.
  It does not: when the account signed in to Offstream is not the account playing the music, or
  playback is in a private session, the player endpoint answers 204 forever. The budget now
  stands down after two tracks in a row exhaust it, and a successful lookup restores it — and
  because everything on that path was logged at Debug, which the activity log does not show, it
  now says so once, at `Warning`, naming both causes.
- **A data race in the audio ring buffer.** The read position and byte count were captured
  before the lock was taken, so a concurrent write could move them underneath a read and produce
  torn audio. Reads are also span-based now, instead of allocating a fresh buffer on every read
  in a path that runs continuously for hours.
- **The last chunk of every track was dropped.** The write into the WAV honoured the stop token,
  and that token is cancelled at exactly the moment a track ends — with a chunk already taken out
  of the ring buffer and therefore unrecoverable.
- **The track already playing when a session started was never recorded.** With no previous
  track to compare against, the first observation counted as "nothing changed", so only the song
  after it was captured.
- **The file counter could exceed the width its template allows**, rendering `10000` for a
  `{count:0000}` mask and breaking both sort order and the "have I already recorded this?"
  check. It now saturates so the ceiling can be detected.
- **A test that called the live Last.fm API** and failed with no network is now fixture-driven.
- **Spotify track/disc numbers of 0 no longer get written as a literal zeroth track.** The SDK's
  `FullTrack.TrackNumber`/`DiscNumber` default to 0 (a non-nullable `int`) when a track could not
  be fully populated; the predecessor wrote that 0 straight into the tag. Now mapped to "unknown"
  instead, since Spotify numbers both from 1.
- **A bare release year no longer silently drops the year.** `DateTime.TryParse` rejects a
  four-digit year with nothing else on it, which the predecessor's mapping also relied on —
  dropping the year for every album whose Spotify precision is year-only rather than a full date.
- **A hand-edited settings file with a section or key removed no longer loads as zeroes.**
  System.Text.Json's source generator does not run property initializers for properties absent
  from the JSON, so an omitted `bitrateKbps` read back as `0` and an omitted section as `null`.
  The schema now declares its defaults as constructor parameter defaults, which the generator
  does honour, and a test suite pins the behaviour.
- **The last chunk of a recording could be lost on a fast track change.** Two recorders share one
  capture buffer across a change, and the incoming one discarded its contents without waiting for
  the outgoing one to finish reading its own tail out of it.
- **No metadata was written to any recording.** The provider dropdown was read, validated and
  saved, and then consulted by nobody: the recorder built its encode request straight from the
  `Track` scraped from the Spotify window title, so every file carried an artist and a title and
  nothing else. `SpotifyMetadataProvider` existed but nothing resolved it, no Last.fm provider
  class existed at all, and the cover-art path was always null because nothing fetched the art.
- **The Spotify provider could never have worked**, whatever was configured. Nothing resolved
  `SpotifyAuthenticator`, so no refresh token was ever obtained, so a session had nothing to
  present to the API.
- **AAC output was named `.aac` but was an m4a file.** The file name came from the lower-cased
  enum member while the encoding profile writes an MP4 container, producing a file Windows and
  most players refuse to open. The extension now comes from the profile.
- **The file counter restarted at 1 on every run.** The session increments it as recordings land,
  on its own working copy of the settings, and nothing wrote it back — so each night's recordings
  landed on the previous night's names and the "have I already recorded this?" check answered for
  the wrong file.
- **Album art was invisible everywhere except VLC.** MP3s were tagged as ID3v2.4, ffmpeg's
  default; Windows Explorer's thumbnail handler and Windows Media Player have never read v2.4
  cover art, so the picture was in the file and neither showed it. The predecessor tagged with
  TagLib#, which writes v2.3 — hence the regression. MP3 output now pins `-id3v2_version 3`.
  Nothing is lost by it: the full release date and `4/12` track numbers both survive.
- **The `artist` tag repeated the album artist and dropped featured performers.** It was built
  from `Track.Artists`, which returns the album artists whenever a provider has supplied them —
  so `artist` and `album_artist` were identical on every enriched file. It now credits the
  track's own performers, as the predecessor's TPE1/TPE2 split did. File names are unaffected;
  the `{artist}` template token still renders from `Track.Artists`.
- **The elapsed counter drifted behind Spotify and never caught up.** It added one second per
  timer tick observed, and `PeriodicTimer` schedules the next tick from when the previous one was
  consumed rather than making up a late one — so every delayed tick lost a second permanently.
  The counter is now sampled from a monotonic clock anchored at the track's start, with pauses
  banked rather than counted, so a late tick reports the truth and the drift corrects itself.
- **The Record page lagged while recording.** The waveform emitted a separate `DrawRectangle` per
  bar — several hundred drawing instructions rebuilt thirty times a second — and drove itself
  from `CompositionTarget.Rendering`, which wakes the UI thread at the display's refresh rate and
  keeps WPF composing rather than idling. The bars are now one frozen geometry in a single draw
  call, sampled by a timer at exactly the rate the scroll needs.
- **Spotify tagged nothing on tracks whose boundary caught its backend mid-change.** The window
  title advances the instant the desktop client does, while `/v1/me/player/currently-playing` is
  served from player state that trails it by a second or more — so asking once at the boundary
  returns the *previous* track, the title-match guard correctly refuses it, and the recording is
  saved bare. The predecessor waited before its first poll and retried a second later; the port
  kept the guard and dropped the retry. Both are back, with a momentary empty answer treated as
  the same race rather than as "nothing is playing". A podcast episode still fails immediately —
  retrying could not change that answer.
- **The Record page grew taller than its window, so the log never scrolled.** WPF-UI's
  `NavigationViewContentPresenter` wraps a page in a `DynamicScrollViewer` whenever its
  `ScrollViewer.CanContentScroll` is true — which that control's own static constructor makes the
  default — and inside it the page is measured with infinite height. The log's star row resolved
  to its content's size instead of the viewport, so the list realised every retained line and ran
  off the bottom of the window, with tail-following silently doing nothing.
- **The activity log grew for the life of the session instead of scrolling.** The in-memory sink
  keeps the last 2000 lines and the page's backing buffer trimmed with it, but the collection
  actually bound to the list never dropped its oldest entry — so an overnight session ended with
  a pane holding far more lines than had been retained. Both now trim in lockstep, and a line the
  filter hides no longer evicts one it shows.
- **A single day's log file had no size ceiling.** The daily roll bounds the file count, not the
  size of any one file. It now rolls at 16 MB as well, which caps the log directory at seven
  files rather than seven days of unbounded writing.
- **Nothing tagged from Last.fm ever carried a genre.** The mapper hard-coded an empty array and
  never read the `toptags` node. Since Spotify has also stopped returning album genres for most
  of its catalogue, the genre tag was empty whichever provider was chosen.
- **The "write the counter to the track number" setting did nothing.** It is now applied to the
  tag, without disturbing the `{track}` filename token, which keeps meaning the position within
  the album.
- **Tagging stopped an hour into every session.** The SDK's `PKCEAuthenticator` stores exactly
  one token and renews by writing the response's fields onto it in place — but Spotify's PKCE
  renewal is not obliged to return a new `refresh_token`, and when it omits one that null lands
  on the good value. The first renewal still succeeds, so nothing looks wrong; the next throws
  from inside the SDK and every lookup for the rest of the session fails identically.
  `ResilientPkceAuthenticator` remembers the last refresh token Spotify actually sent and puts
  it back before each request, leaving renewal itself to the SDK.
- **Every logged cause was dropped on the way to the Record page.** `InMemoryLogSink` rendered
  the message template and discarded `LogEvent.Exception`, so the answer to the bug above sat in
  the log file for hours while the pane said only that something failed. It now appends the
  exception's type and message — not the stack trace, which would swamp a one-line-per-entry
  list.
- **"Keep the one on disk" behaved as "overwrite"** for anyone whose template was more than
  artist and title. The policy was checked the instant a track changed, before the metadata
  lookup had returned anything, so a template naming an album rendered with album, year and
  track number still empty — the check looked somewhere nothing is ever written, found nothing
  every time, and let the rename replace the real file without a word. The authoritative check
  moved after enrichment, where the destination is finally knowable, and all three outcomes now
  name themselves and the file.
- **Spotify metadata matched nothing the media session reported.** The guard compared Spotify's
  bare track name against the detected title after running only Spotify's side through the
  window-title splitter — a parsed string against an unparsed one, for every track the media
  session found, which has been the common case since it became the primary source. Four
  attempts, several seconds, then an untagged recording. `DetectedTrackMatch` reduces both sides
  to a common form and compares them in both shapes. Normalisation stops well short of fuzzy:
  the wrong answer here is not "no metadata" but a file tagged as a different song.
- **Last.fm accepted any release its database happened to associate.** It had no equivalent of
  the guard above, so whatever came back was written — and for a well-known track that is
  regularly a DJ set or radio show it once appeared on. `DetectedTrackMatch.AlbumAgrees` now
  checks the reported album against the one the client is playing the track out of, treating an
  edition suffix as more said about the same record. A rejected release still yields its genre,
  since tags describe the recording rather than the release.
- **Last.fm reported no genre for a great many tracks it has tags for.** The lookup stopped at
  the empty per-track tag cloud without asking about the artist, and as the *chosen* provider it
  mapped nothing at all — genres included — unless an album came back too, which is the wrong
  gate for a question that never asked about albums.
- **What the media session already knew was discarded** whenever the provider could not help.
  With the match guard rejecting every attempt, no provider configured, or Spotify down, the
  file was written bare while artist, title, album, album artist and position had all been
  reported for that exact track. Both mappers now fill rather than clear.
- **Closing the window mid-recording left a process running with no window and no tray icon**,
  killable only from Task Manager. `OnExit` was `async void`, which WPF does not await: it ran
  to its first await, returned, and let `Application.Run` tear the Dispatcher down, so the
  continuation carrying the rest of the shutdown was posted to a Dispatcher that would never run
  it — the host was never stopped, the capture client never closed, the encode backlog never
  drained, and the log never flushed, which is why the failure left nothing to read. The
  container was then disposed synchronously, which throws on a singleton implementing
  `IAsyncDisposable` only, and nothing bounded the drain. Shutdown is now synchronous, off the
  Dispatcher, asynchronous in disposal, and capped at thirty seconds.
- **Every recording failure was printed twice**, once at `Error` and once at `Information` with
  identical text — one colour meaning *act on this* and one meaning *carry on*.
- **A cover-art failure was filed as news.** It went out as a progress message, which lands at
  `Information`, below the Problems filter and so invisible to anyone who went looking for
  exactly this.
- **The session total read "0 saved" forever.** The count was right the whole time; only the
  derived string was never refreshed.
- **A break between tracks cost the track after it every tag.** Spotify's `currently-playing`
  answers a free account's advertisement, or 204 No Content, rather than the track about to
  start — and the lookup counted each such answer as a failed match, so the four attempts it
  allows were spent inside a break that runs far longer and the recording was saved bare. An
  answer carrying no track at all is now a different failure from an answer carrying the wrong
  one: something is playing, since a recording is running, so it resolves on its own and gets a
  budget of its own — thirty polls rather than four. Bounded rather than open-ended, because a
  track the API will never report should delay one recording and not every one. The enrichment
  deadline moves 20s to 45s to leave room for it; the recording it runs alongside is unaffected
  either way, since enrichment starts when the track does.
- **The log could not tell a 204 from an advertisement.** Both printed "nothing playing", which
  is the one distinction worth having when a lookup keeps missing — so the line naming what
  Spotify answered named the shape of the answer rather than the absence of a track.
- **One song could be recorded twice, and enriched twice.** The Windows media session fills its
  fields as they arrive, so the first read after it becomes available can carry a title and no
  artist. That counted as a track change: the take in progress ended, was reported as an
  advertisement, was discarded for falling under the minimum length, and a second recording of
  the same song started a moment later when the artist arrived. A title without an artist is now
  read as a source part-way through reading — which is the only thing it can mean, since the
  window-title parser puts an unsplittable title in the artist field instead.

### Security

- **Untrusted track metadata can no longer influence an ffmpeg command line** — see the argument
  vector note under *Changed*.
- **The PKCE `state` parameter is validated** — see *Changed*.
- **The Spotify refresh token is encrypted at rest** with DPAPI, scoped to the current Windows
  user, so a copied `settings.json` is useless to anyone else.
