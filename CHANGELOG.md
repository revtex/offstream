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

- **Solution scaffold on .NET 10.** SDK-style projects (`Offstream.Core`, `Offstream.App`,
  `Offstream.Core.Tests`, `Offstream.UI.Tests`, `Offstream.FakeSpotify`) under an `.slnx`
  solution, central package management, nullable reference types, and analyzers as errors.
- **GitHub Actions CI** on `windows-latest`: build, `dotnet format --verify-no-changes`,
  analyzers as errors, and the test suite with ffmpeg pinned.
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
- **855 tests**, 13 of which drive the real window through FlaUI and are excluded from CI.

### Changed

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

### Removed

- **NAudio.Lame and the bundled LAME DLLs** — ffmpeg encodes MP3.
- **Settings migration.** Offstream does not read the predecessor's `user.config` and ships no
  importer; it starts from a clean slate with first-run defaults.
- **MetroFramework, EmbedIO and DotNetZip**, along with `packages.config` and the manual
  `<Compile Include>` lists that silently omitted files added on disk.
- **Every identifier inherited from the predecessor.** No namespace, type, file, project,
  resource key, setting or path carries the old naming, and a test fails the build if one
  reappears.

### Fixed

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
- **Nothing tagged from Last.fm ever carried a genre.** The mapper hard-coded an empty array and
  never read the `toptags` node. Since Spotify has also stopped returning album genres for most
  of its catalogue, the genre tag was empty whichever provider was chosen.
- **The "write the counter to the track number" setting did nothing.** It is now applied to the
  tag, without disturbing the `{track}` filename token, which keeps meaning the position within
  the album.

### Security

- **Untrusted track metadata can no longer influence an ffmpeg command line** — see the argument
  vector note under *Changed*.
- **The PKCE `state` parameter is validated** — see *Changed*.
- **The Spotify refresh token is encrypted at rest** with DPAPI, scoped to the current Windows
  user, so a copied `settings.json` is useless to anyone else.
