# Offstream — Modernization Plan (.NET 10 + WPF)

**Offstream** is a Windows Spotify audio recorder on **.NET 10 (LTS) with a WPF + Fluent UI**, delegating **all audio conversion to ffmpeg**.

It succeeds **Spytify** (.NET Framework 4.6.1 / WinForms), which lives at `../spy-spotify` as a **read-only reference**. Wherever this document names a Spytify file, it is citing source material to read — never a component of this repo. See §0 for the rule that governs this.

**Shape of the work:** this is a *retarget and UI replacement*, not a from-scratch rewrite. The audio stack, the undocumented COM routing, the domain logic and the 293-test suite all carry over as source — renamed on the way in. That is what makes it tractable.

---

## 0. Naming rule — Offstream owns every identifier

Source code carries over. **Names do not.** Nothing in this repository — no namespace, type, file, folder, project, resource key, setting, path, mutex, or build artefact — may contain `EspionSpotify`, `Spytify`, `spy-spotify`, or any other identifier inherited from the predecessor. A file copied from the old tree is renamed and re-namespaced in the same commit that introduces it.

| Concern | Offstream's name |
| --- | --- |
| Root namespace | `Offstream.Core.*`, `Offstream.App.*` |
| Solution / projects | `Offstream.slnx`, `Offstream.Core`, `Offstream.App`, `Offstream.Core.Tests`, `Offstream.UI.Tests`, `Offstream.FakeSpotify` |
| Settings | `%APPDATA%\Offstream\settings.json` |
| Logs | `%APPDATA%\Offstream\logs\offstream-.log` |
| Single-instance mutex | `Local\Offstream` — per logon session, and per data directory when `OFFSTREAM_HOME` is set (Phase 6 PR 4 corrected this from `Global\`) |
| Executable / installer | `Offstream.exe`, `Offstream-{version}-setup.exe` |
| Resources | `Offstream.App/Resources/Strings.resx`, `Strings.fr.resx` |
| Test namespaces | `Offstream.Core.Tests.*` — never the old suite's namespaces |

Two consequences worth stating outright, because they are the cases most likely to be carried over on autopilot:

- The old UI interface `IFrmEspionSpotify` does not appear here in any spelling. The core reports progress through events / `IProgress<T>` (§3).
- The old tag-mapping type `MapperID3` does not survive under any spelling. ffmpeg writes every textual tag during the encode, and what is left of it — cover art for Ogg/Opus, the one container ffmpeg cannot be trusted with — is `Offstream.Core.Metadata.CoverArtWriter` (§5.2). It was going to be `TagMapper`; a class that only writes pictures should not be named for mapping tags.

A grep for the old identifiers across this repo returns hits only in documentation, and only inside sections that are explicitly about the reference tree.

---

## 1. Alternatives considered

A Go + Wails rewrite was scoped first (see git history for `docs/REWRITE-GO-WAILS.md`). It was rejected, and the reason matters for understanding this plan.

The hardest component is per-application audio routing through the **undocumented `IAudioPolicyConfig` interface**. It is not in any SDK header, and two IIDs exist (21H2 and downlevel). Reimplementing it in Go means hand-writing COM vtables with no header support and no compiler help, with a real chance of failure; the Go plan needed a 3–5 day de-risking phase and a documented fallback in case it proved impractical.

> **Revised after Phase 0 (DR-0001).** This section originally claimed .NET removes that risk *entirely*, because the working implementation could be kept as-is. It cannot: `IAudioPolicyConfig` is an IInspectable-based WinRT activation factory, and .NET 5 removed the marshalling the reference implementation used. **.NET 10 also requires the vtable to be written out slot by slot** — so the "Go means hand-writing vtables, .NET doesn't" contrast is not real.
>
> The decision still stands, on narrower grounds: the interface declaration stays compiler-checked rather than becoming raw function pointers, and WASAPI capture, session control, process interop and the 293-test suite genuinely do carry over. The rewrite was scoped to the marshalling layer, took hours rather than days, and is now proven green.

WASAPI capture, session control and process interop transfer unchanged, and were verified working under NAudio 2.2.1 on .NET 10 in Phase 0.

| | Go + Wails | **.NET 10 + WPF** |
| --- | --- | --- |
| Undocumented COM routing | Rewrite, may fail | **Behaviour kept; marshalling rewritten** — done and proven in Phase 0 |
| WASAPI capture / sessions | Rewrite | **Kept** (NAudio 2.x) |
| Domain logic + 293 tests | Rewrite | **Kept** |
| Runtime dependency | None (15 MB binary) | None (self-contained publish) |
| Estimated effort | 9–13 weeks | **5–7 weeks** |

Go's genuine advantage was a tiny single binary with no runtime install. Self-contained .NET publish also removes the runtime install; the size difference (~15 MB vs ~80 MB) stops being decisive once a ~35 MB ffmpeg is bundled either way.

---

## 2. Technology decisions

| Layer | Choice | Rationale |
| --- | --- | --- |
| Runtime | **.NET 10 (LTS)** — verify current at kickoff | Long support window; even-numbered releases are LTS |
| TFM | `net10.0-windows` today; **raise to `net10.0-windows10.0.22621.0` before Phase 7** | Windows-only app. The versioned TFM is what unlocks WinRT projections for SMTC (§5.3); it became available once Windows 10 left scope |
| Projects | **SDK-style csproj** | Kills `packages.config` and the manual `<Compile Include>` lists |
| UI | **WPF + [WPF-UI](https://github.com/lepoco/wpfui) (Fluent)** | See §2.1 |
| MVVM | **CommunityToolkit.Mvvm** | Source-generated `[ObservableProperty]` / `[RelayCommand]`; no boilerplate |
| DI / config / logging | `Microsoft.Extensions.*` + **Serilog** | Rotating file sink + in-app console sink |
| Audio | **NAudio 2.2.x** | Same API family as today; targets modern .NET |
| Encoding | **ffmpeg** (external process) | Per requirement; removes NAudio.Lame and the LAME DLLs |
| P/Invoke | **Microsoft.Windows.CsWin32** | Source-generated, typed; replaces hand-written `NativeMethods` |
| Media session | `Windows.Media.Control` (WinRT) | SMTC track detection — see §5.3 |
| Tray | **H.NotifyIcon.Wpf** | WPF has no built-in tray icon |
| Tests | **xUnit + Moq + System.IO.Abstractions** | Already in use — suite ports directly |
| UI tests | **FlaUI** | UIA-driven automation for WPF |
| Installer | **WiX v4** or Inno Setup | Per-user install, signed |

### 2.1 Why WPF over the alternatives

The UI is a three-tab settings form: toggles, text fields, a combo box or two, and a log pane. That is WPF's sweet spot.

- **WinUI 3 / Windows App SDK** — most current Microsoft look, but packaging friction (MSIX or unpackaged bootstrapper), no built-in tray icon, and a thinner ecosystem. Reasonable if matching Windows 11 chrome exactly is a priority.
- **Avalonia** — excellent modern XAML, healthy project. Choose it *instead* if cross-platform is plausibly on the roadmap; it costs a little more for Windows-native integration.
- **Blazor Hybrid / WebView2** — closest to the Wails idea, but adds a browser runtime and ~150 MB RSS for an app that runs unattended overnight, with no benefit for a form-heavy UI.
- **MAUI** — WinUI underneath with extra layers. No.
- **WinForms on .NET 10** — cheapest path, hard ceiling on polish.

With WPF-UI, "modern" becomes a theme choice (Fluent, dark mode, Mica) rather than a framework bet, and memory stays around 40–60 MB.

**The decisive argument is data binding.** In the current app, adding one settings field means coordinated edits to `frmEspionSpotify.Designer.cs` (control, `Controls.Add`, declaration), `Init()`, `SetLanguage()`, an event handler, `UserSettings`, `Settings.settings`, `Settings.Designer.cs`, and `App.config` — with layout expressed as `tableLayoutPanel` row/column arithmetic. In WPF that is a bound property plus a line of XAML. Every UI defect encountered while adding the filename-template field was a direct consequence of the WinForms designer model.

Data binding is the decisive argument, and it is stated here in terms of the reference app only to show what is being escaped; Offstream's own UI never reproduces that model.

### 2.2 Constraints to respect

- **Do not enable NativeAOT.** The app depends on built-in COM interop (`ComImport`, `Marshal.GetDelegateForFunctionPointer`) for audio routing; AOT does not support it.
- **Do not enable aggressive trimming.** WPF's trimming support is limited, and reflection-driven settings/localisation will break. Publish **self-contained, untrimmed** (or `PartialTrim` only after measuring).
- **`System.Configuration.ConfigurationManager`** is a NuGet package on modern .NET, and the `Settings.settings` designer model is legacy. §6 replaces it outright rather than carrying it forward.

---

## 3. Architecture

Split the monolith into a testable core plus a thin UI.

```
Offstream.slnx
├── src/Offstream.Core/           net10.0-windows  — no UI references
│   ├── Audio/                  capture, devices, sessions, ring buffer
│   ├── Encoding/               ffmpeg profiles, runner, probing
│   ├── Metadata/               Spotify/Last.fm providers, tag mapping
│   ├── Naming/                 filename template engine, path assembly
│   ├── Recording/              watcher state machine, recorder
│   ├── Settings/               model, JSON persistence, validation
│   ├── Spotify/                process/title polling, SMTC
│   └── Interop/                CsWin32 + COM (routing, power, processes)
├── src/Offstream.App/            net10.0-windows  — WPF, MVVM only
│   ├── Views/                  XAML
│   ├── ViewModels/             CommunityToolkit.Mvvm
│   ├── Services/               dialogs, tray, navigation
│   └── Resources/              themes, i18n .resx
├── tests/Offstream.Core.Tests/   xUnit — the 293 + new
├── tests/Offstream.UI.Tests/     FlaUI end-to-end
└── tools/Offstream.FakeSpotify/  window-title test harness
```

**Rule:** `Offstream.Core` must not reference WPF or `System.Windows`. The reference app passes its form interface into the watcher and recorder so they can write to the console pane; Offstream instead exposes `IProgress<RecordingProgress>` and events, so the core is UI-agnostic and testable without a form mock.

**Rule:** every folder and type above is named for what it does in Offstream, not for where it came from. §0 governs; when a carried-over file's original name is the only thing describing it, rename it to describe its behaviour instead.

### 3.1 Recording pipeline (unchanged in shape)

```
WASAPI loopback ─► ring buffer ─► temp .wav ─► ffmpeg ─► final file (+ tags + cover art)
       │                              │
   AudioThrottler              per-track Recorder
```

The existing design — one capture stream feeding a lock-guarded circular buffer, with per-track recorders draining slices and `SilenceAnalyzer` trim-start/trim-end semantics — is sound. Keep it. Modernise the plumbing only: `CancellationToken` throughout (already partly there), `Channel<T>` for the encode queue, `IAsyncEnumerable` where it reads better.

---

## 4. Asset disposition

Left column: what to read in the reference tree at `../spy-spotify`. Right column: what exists in **this** repo afterwards. The mapping is deliberately explicit — a carried-over file arrives already renamed, never as a copy that gets cleaned up later.

| Read from reference tree | Becomes, in Offstream |
| --- | --- |
| `EspionSpotify/Router/*` (IAudioPolicyConfig, both OS variants) | `Core/Interop/Routing/*` — **behaviour kept, marshalling rewritten**. The .NET Framework WinRT marshalling does not exist on .NET 10; see DR-0001 and `spike/Offstream.Spike/Routing/` for the proven replacement |
| `EspionSpotify/AudioSessions/*` (capture, throttler, circular buffer, MM devices) | `Core/Audio/*` — retargeted to NAudio 2.x |
| `Native/NativeMethods.cs` | Deleted; CsWin32-generated P/Invoke in `Core/Interop/` |
| `Native/ProcessManager.cs`, `FileManager.cs` | `Core/Interop/ProcessControl.cs`, `Core/Naming/OutputPaths.cs` |
| `Spotify/*` (process, status, title parsing) | `Core/Spotify/*` — plus SMTC as primary source (§5.3) |
| `Models/*` (Track, UserSettings, FileNameTemplate, OutputFile) | `Core/Metadata/Track.cs`, `Core/Settings/OffstreamSettings.cs`, `Core/Naming/FileNameTemplate.cs`, `Core/Recording/OutputFile.cs` |
| Template engine | `Core/Naming/*` — **behaviour kept exactly**; recently written and fully tested |
| `API/*` (Last.fm, Spotify) | `Core/Metadata/Providers/*` — logic kept, SDKs upgraded (§8) |
| `Recorder.cs` encode paths | **Dropped** — `Core/Encoding/*` gives ffmpeg all conversion |
| `MapperID3` (TagLib#) | `Core/Metadata/CoverArtWriter.cs` — **replaced but for Ogg/Opus cover art**; ffmpeg writes tags, see §5.2 |
| `frmEspionSpotify.*` (1,200 lines + designer) | **Dropped entirely** — `App/Views/*` + `App/ViewModels/*` |
| `Properties/Settings.*`, `App.config` | **Dropped entirely** — `Core/Settings/*` over JSON (§6) |
| `EspionSpotify.Updater` | `Offstream.Updater` — rewritten (§10, Phase 8) |
| `EspionSpotify.Tests` (293 tests) | `tests/Offstream.Core.Tests` — **assertions kept**, namespaces and fixtures renamed; the safety net for the whole port |
| `EspionSpotify.FakeSpotify` | `tools/Offstream.FakeSpotify` — retargeted |

---

## 5. The ffmpeg boundary

All conversion goes through ffmpeg. Capture writes raw PCM WAV to a temp file; ffmpeg produces the final artefact.

### 5.1 Format profiles (data, not code)

| Format | Args | Notes |
| --- | --- | --- |
| MP3 | `-c:a libmp3lame -b:a {rate}k` | CBR; VBR (`-q:a`) can be exposed later |
| WAV | `-c:a pcm_s16le` | Or stream-copy when the temp already matches |
| Opus | `-c:a libopus -b:a {rate}k` | Ogg container, `.opus` |
| **FLAC** | `-c:a flac -compression_level 8` | New — near-free once ffmpeg owns conversion |
| **AAC/M4A** | `-c:a aac -b:a {rate}k` | New |

Two decisions, both settled in Phase 3:

1. **Bundle ffmpeg, with a runtime override.** An **LGPL-only build** (libmp3lame, libopus, libvorbis, libflac; no GPL components) is ~35 MB stripped, gives zero-setup operation, and obliges shipping ffmpeg's licence plus a written source offer — the installer work lands in Phase 8. `Encoding/FFmpegLocator` implements the search **configured → bundled → `PATH`**, in that order: an override that loses to the bundle is not an override. A configured path that does not exist is an error rather than a cue to fall back, because silently encoding with a different ffmpeg than the user pointed at makes the resulting bug report unreadable. It accepts either the executable or the folder holding it, and resolves ffprobe as a sibling.
2. **The version is asserted at startup and logged.** `Encoding/FFmpegVersion` parses the `-version` banner; the floor is **6.0**, comfortably below every flag in use, so the check exists to fail loudly on a genuinely ancient build rather than obscurely at the first encode. Nightly builds identify themselves as `N-118488-g1e1e4d1` with no version number: those parse as *unknown* and are allowed through, since rejecting them would reject the newest builds there are. Reading that revision counter as a major version — which a naïve parse does — would pass any floor check by accident, so it is a test case.

### 5.2 Tagging and cover art — two traps

Both were hit while adding Opus support to the current app; both must be covered by tests, not assumptions.

- **Ogg/Opus stores tags at the _stream_ level**, not container level. Verification must use `ffprobe -show_entries stream_tags`. Using `-show_format` shows nothing and produces a false "tags are missing" conclusion.
- **Cover art** for MP3 is a second input stream (`-i cover.jpg -map 0:a -map 1:v -c:v mjpeg -disposition:v attached_pic`). ffmpeg's `METADATA_BLOCK_PICTURE` support for Ogg/Opus is weaker. TagLib# **does** write Opus cover art correctly (verified against `TagLib.Ogg.File`). **Plan:** ffmpeg writes all textual tags; if per-format ffprobe tests show cover art failing for a container, retain TagLib# for that container only.

**Settled in Phase 3.** Each format profile declares how it takes a picture, so the route is data rather than a branch: MP3, FLAC and M4A take it as a second ffmpeg input in the encode pass; Ogg/Opus takes it afterwards from `Core/Metadata/CoverArtWriter` (TagLib#); WAV has nowhere to put one and is skipped without complaint. `CoverArtIntegrationTests` reads the picture back out of every container, including the assertion that TagLib# rewriting the Opus comment header does not take ffmpeg's textual tags with it.

A failed picture is a **warning on the encode outcome, not a failed encode**: the audio is already on disk and playable, and losing a recording over album art is the wrong trade. The exception is a missing image for an attached-picture container, where ffmpeg cannot open its second input and the encode fails outright.

Full tag set, written by ffmpeg as `-metadata` arguments: title, subtitle, album, album artist, performers, genres, track number, disc number, year — plus the front-cover picture by whichever of the two routes the container needs.

### 5.3 Process discipline

Use `ProcessStartInfo.ArgumentList` — **available on modern .NET, unlike .NET Framework 4.6.1**. This eliminates by construction the argument-injection class of bug the current code needed hand-written `CommandLineToArgvW` escaping to avoid. Track metadata comes from Spotify window titles and is untrusted, so this matters.

Always: a `CancellationToken` with deadline, `RedirectStandardError` drained *before* waiting (a full stderr pipe deadlocks), and an exit-code check.

---

## 6. Settings — clean slate

Offstream stores settings at **`%APPDATA%\Offstream\settings.json`**, with a `schemaVersion` field, validation on load, and atomic writes (temp file + `File.Move` with overwrite).

> **Built as `Settings/SettingsStore` rather than through `Microsoft.Extensions.Configuration` (Phase 5).** That binder is a read-only, multi-source composition layer — it merges environment variables and command-line arguments over a file and hands back a bound object. Offstream needs the opposite: one file it owns, reads *and writes back*, with validation and atomic replacement. Configuration binding cannot write, so it would have been half the job plus a second serializer to do the other half. The Spotify Client ID is still read from `IConfiguration` in `Offstream.App`, where composing user-secrets and environment variables genuinely helps during development.

**There is no import from the predecessor.** Offstream does not read `%LOCALAPPDATA%\Spytify\user.config`, does not know its key names, and ships no migration code. A first run starts from defaults and the first-run experience is designed for that: sensible defaults for output path (`%USERPROFILE%\Music\Offstream`), format, and template, so the app is usable before the user opens Settings at all. Anyone moving over re-enters their preferences once.

This is a deliberate trade — it costs existing users one setup pass and buys a settings layer with no legacy key vocabulary anywhere in it.

The JSON schema is designed for Offstream, not transcribed from the old flat keys: grouped sections (`output`, `recording`, `metadata`, `app`) with nested objects rather than `settings_*` / `advanced_*` prefixes.

Two rules the schema must honour:

- **No log text in settings.** Logs go to a rotating Serilog file under `%APPDATA%\Offstream\logs\`. Unbounded console text in a settings string is fragile.
- **No credential is written in the clear.** Protect it with DPAPI (`ProtectedData`, `CurrentUser` scope) before it reaches disk. **As built, that means the Spotify *refresh token*** — Phase 4 chose PKCE, which has no client secret for a public desktop client to protect in the first place. The Client ID stays readable; it is sent in the clear on every authorize request and is not a credential on its own. The same reasoning covers `lastFmApiKey`, added 2026-08-12: it goes on the query string of every request, so protecting it at rest would buy nothing.

> **Every provider credential is the user's own** (2026-08-12). `spotifyClientId` always was. `lastFmApiKey` is too, in deliberate contrast to the reference, which shipped three of its own Last.fm keys hard-coded in its source and chose one at random per run. A key embedded here would be shared by every install, rate-limited collectively, and revocable by someone who is not the user — and it would not be Offstream's to ship.

---

## 7. Feature parity matrix

The acceptance checklist. Everything the app does today.

### Recording
WASAPI loopback capture · device selection · device volume · per-track splitting · minimum recorded length · recording timer (hhmmss) · silence trim start/end · mute ads · record everything (podcast) · record ads · skip/overwrite/duplicate existing · force Spotify to skip recorded track · listen to playback on default device · prevent sleep while recording.

### Output
MP3 128/160/256/320 · WAV · Opus · **FLAC (new)** · **AAC (new)** · filename template with tokens and folder support · counter with padding · output path · 260-char budgeting (**plus long-path support, new**).

### Metadata
Last.fm provider · Spotify Web API provider · None provider · cover art · counter as track number · extra title → subtitle · re-tag already-recorded tracks.

### Shell / UX
Three tabs (**Record / Settings / Advanced**) · console log pane · minimise to tray · en/fr localisation · VB-CABLE **detection** (installer invocation pending open question 9) · Spotify API credentials dialog · FAQ links · **auto-update (new)** · **local-only crash diagnostics (new, never auto-sent)** · analytics stays removed.

> The three-tab *structure* carries over because it works. The **tab labels do not** — the predecessor's "Spy" tab is Offstream's **Record** tab, and its "spy options" are **detection options**. §0 applies to user-visible strings as much as to code.

### Filename template — behaviour preserved exactly
Tokens `{artist} {title} {album} {album_artist} {year} {track} {disc} {count} {date} {time}`, format specs (`{track:00}`, `{count:0000}`, `{date:yyyy-MM-dd}`), backslash for folders, empty-token collapsing with orphaned-separator cleanup, invalid characters stripped rather than substituted. **.NET format strings are unchanged**, so unlike the Go plan there is no date-layout translation problem. The engine's *syntax* is user-facing and stays byte-identical; its *implementation* lives in `Offstream.Core.Naming` under Offstream names.

---

## 8. Dependency upgrades

| Package | Current | Target | Work |
| --- | --- | --- | --- |
| NAudio | 1.10.0 | 2.2.x | Namespace/API shifts; moderate |
| NAudio.Lame | 1.1.6 | **removed** | ✅ ffmpeg replaces it; never added here, and no `libmp3lame.*.dll` was ever copied in |
| SpotifyAPI.Web | 5.1.1 | **7.4.2** | ✅ **Breaking, done** — `SpotifyClient`/`SpotifyWebAPI` replaced; PKCE with a hand-rolled loopback listener (§10 Phase 4) |
| TagLibSharp | 2.2.0 | **2.3.0** | ✅ retained, for Ogg/Opus cover art and nothing else (§5.2) |
| System.IO.Abstractions | 13.2.8 | current | Minor |
| Newtonsoft.Json | 13.0.1 | **System.Text.Json** | ✅ source-generated context for settings (Phase 5). Still present transitively via SpotifyAPI.Web, which serializes its own models with it |
| EmbedIO / Unosquare.Swan | 2.9.2 | **removed** | ✅ was the OAuth loopback listener; replaced by `HttpListener` directly (`Spotify/Auth/SpotifyLoopbackListener`) — `SpotifyAPI.Web.Auth`'s own helper still depends on EmbedIO, so it is not referenced either |
| MetroFramework | 1.4.0 | **removed** | Replaced by WPF-UI |
| DotNetZip | 1.11.0 | **removed** | Known high-severity advisory; use `System.IO.Compression` |
| ExceptionReporter / Handlebars | — | already removed | — |
| xunit | 2.4.1 | current | Minor |

Also: `WebRequest` → `HttpClient` (with `IHttpClientFactory`), `AppDomain.CurrentDomain.BaseDirectory` → `AppContext.BaseDirectory`, and enable `<Nullable>enable</Nullable>` incrementally per project.

---

## 9. Testing strategy

The 293 existing tests are the safety net that makes this a retarget rather than a leap of faith. **Get them green on .NET 10 before changing any behaviour.**

### 9.1 Layers

| Layer | Tool | Scope |
| --- | --- | --- |
| Unit | xUnit + Moq + `MockFileSystem` | Naming, parsing, settings, tag mapping, ffmpeg argv |
| Golden | xUnit + testdata | Template rendering, ffmpeg argument construction |
| Integration | xUnit + real ffmpeg | Encode synthetic audio, assert with ffprobe |
| Contract | `HttpMessageHandler` fakes | Spotify/Last.fm against recorded fixtures |
| ViewModel | xUnit | **New** — the current UI is entirely untested |
| E2E | FlaUI | Tab navigation, settings persistence, validation, dialogs |
| Harness | FakeSpotify | Window-title simulation without real Spotify |
| Manual | Checklist | Routing, VB-CABLE, elevation, device hot-plug, tray |

### 9.2 Regression suites, in build order

1. **Bring the 293 across with their assertions unchanged** — only namespaces, fixture names and `using` directives are rewritten (§0). Any *assertion* failure is a retarget defect, not a design change.
2. **ffmpeg argv golden tests** — exact argument arrays per format × bitrate × metadata. Catches flag drift without invoking ffmpeg.
3. **Encode integration** — per format: generate a `sine` via ffmpeg, encode, then assert codec, container, sample rate, channels, duration and **all tags** via ffprobe (`stream_tags` for Ogg). This is the test that catches the §5.2 traps.
4. **Settings round-trip** — defaults → JSON → load → identical object; schema validation rejects malformed and out-of-range values; unknown `schemaVersion` fails loudly; DPAPI round-trip for the client secret; atomic-write survives a simulated crash mid-save. (Replaces the migration suite dropped with §6.)
5. **Path edge cases** — 260-char budgeting, UNC paths, reserved names (`CON`, `NUL`), invalid characters, empty rendering, long-path opt-in.
6. **ViewModel tests** — validation, command enablement, template live preview.
7. **Naming hygiene** — a test that greps the compiled assemblies' type and resource names for the forbidden identifiers in §0 and fails on a hit. Cheap, and it prevents the one failure mode this whole convention exists to avoid.

### 9.3 Discipline

- **No network in unit tests.** The current suite has one test that calls the live Last.fm API and fails offline; convert it to a fixture.
- **CI on `windows-latest`**: build, `dotnet format --verify-no-changes`, analyzers as errors, `dotnet test` with coverage, ffmpeg pinned to the bundled version.
- **Coverage gate ≥ 75%** on `Offstream.Core`, excluding `Interop` (hardware-dependent — covered by the manual checklist).
- **Golden updates require an explicit flag** so diffs are always reviewed.

---

## 10. Phases

Each phase has an exit criterion; do not start the next until it is met.

### Phase 0 — Retarget spike (2–3 days) — ✅ **complete**
Prove the risky parts survive the move before restructuring anything.
- ✅ Scratch `net10.0-windows` project (`spike/Offstream.Spike`) holding the routing, capture and interop logic, renamed into `Offstream.*` on the way in. **As of Phase 2 the routing lives in `Offstream.Core.Interop.Routing` and the spike references it**, so `accept` exercises the shipping code rather than a copy.
- ✅ WASAPI loopback capture under NAudio 2.2.1; session mute/volume; `IAudioPolicyConfig` routing — on Win11 build 26200.
- ✅ .NET 10 confirmed LTS to Nov 2028; WPF-UI 4.3.0 ships a `net10.0-windows7.0` target.
- ✅ ffmpeg: bundle an LGPL-only build with runtime override.

**Exit:** `Offstream.Spike accept --seconds 30` green — 8/8 checks on Win11 26200, unelevated. Decision record: **DR-0001**.

> **Windows 11 only** (decided 2026-08-11, open question 6). Windows 10 support is dropped, which closes what had been Phase 0's one unmet criterion — the unverified downlevel routing path. Windows 10 reached end of support in October 2025, so this also removes the need to test on an unsupported OS.

**Headline finding:** the routing interop could **not** be ported verbatim — .NET 5 removed the WinRT marshalling it depended on. The replacement is written and proven; §4 and DR-0001 carry the detail. Anyone starting Phase 2 should read DR-0001 first.

### Phase 1 — Solution scaffold (3–4 days) — ✅ **complete**
- ✅ `Offstream.slnx` and the SDK-style projects of §3, authored from scratch. Central package management via `Directory.Packages.props`; shared settings via `Directory.Build.props`.
- ✅ Windows 10 VM no longer required — Windows 10 is out of scope (open question 6).
- ✅ CI pipeline (`.github/workflows/ci.yml`) on `windows-latest`: restore, format check, build, test, plus a publish job asserting trimming and AOT stay off.
- ✅ Analyzers as errors; `dotnet format` verified clean.
- ✅ Serilog with rotating file sink + `InMemoryLogSink` feeding the console pane.
- ✅ Naming-hygiene test (suite 7) landed, guarding every later phase.

**Exit:** ✅ solution builds on .NET 10 with 0 warnings, **14/14 tests green** (13 unit + 1 FlaUI), `dotnet format --verify-no-changes` clean, self-contained publish succeeds. Decision record: **DR-0002**.

> **The scaffold is a running app, not empty projects.** `Offstream.exe` starts, hosts a WPF-UI Fluent shell, logs through Serilog to `%APPDATA%\Offstream\logs\`, and the FlaUI test drives the real window. That is deliberately further than "empty test projects run" — a shell that launches is what makes Phase 2's port verifiable.

### Phase 2 — Core green (4–5 days) — 🔄 **in progress**
- Move domain and audio code into `Offstream.Core`, renamed per §0; strip all UI coupling (the old form interface → events / `IProgress<T>`).
- Bring the 293 tests across and **get all of them passing.**
- CsWin32-generated P/Invoke in place of the hand-written native methods.

**Exit:** 293/293 green on .NET 10; `Offstream.Core` has no `System.Windows` reference; naming-hygiene test green.

**Progress — 475 tests green** (474 core + 1 FlaUI), 0 warnings, format clean, self-contained publish verified, `Offstream.Spike accept` 8/8 against the code in `Core`. (Phase 4 has since added 42 more, offline — see below.)

> **325 > 293 does not mean the exit criterion is met.** The counts are not comparable: four reference test files are deliberately deferred to the phase that reshapes what they cover (below), while several ported areas gained cases the reference never had. Phase 2 closes when the deferred files have landed in their phases and nothing from the reference suite is unaccounted for — not when a number is exceeded.

| Layer | Status |
| --- | --- |
| Enums (media format, provider, cover size, restrictions, policies) | ✅ ported |
| `Text/StringExtensions`, `Text/EnumerableExtensions` | ✅ ported |
| `Spotify/SpotifyWindowTitles` (idle/ad detection) | ✅ ported |
| `Audio/WaveFormatExtensions` (MP3 limits) | ✅ ported |
| `Naming/PathText` (diacritics, segment cleaning, tidy) | ✅ ported |
| `Naming/FileNameTemplate` | ✅ ported, syntax byte-identical |
| `Metadata/Track` | ✅ ported, with its own tests |
| `Settings` timer parsing, counter derivation | ✅ ported |
| `Naming/OutputPaths`, `OutputFile` (260-char budgeting, collisions, cleanup) | ✅ ported |
| `Settings/RecordingSettings` | ✅ partial — grown as consumers land |
| `Spotify/SpotifyTitleParser` (window-title → track) | ✅ ported, split from API enrichment |
| `Spotify/SpotifyTrackDetector` (process discovery, polling) | ✅ ported |
| `Interop/ProcessManager` | ✅ ported |
| `Spotify/SpotifyPoller` (poll loop, state machine, events) | ✅ ported, timers → `PeriodicTimer` |
| `Recording/RecordingPolicy` (what to record, and when to stop) | ✅ ported, split from orchestration |
| `Recording` orchestration (`RecordingSession`, `TrackRecorder`) | ✅ ported in Phase 3 — rules already split out; the form reference and the static `Running` flag are gone |
| `Metadata/Providers/LastFm*` (models + mapping) | ✅ ported; live-API test replaced by fixtures |
| `Metadata/Providers/Spotify*` (mapping + PKCE auth) | ✅ ported in Phase 4 — `SpotifyTrackMapper`, `Spotify/Auth/*`; not yet consumed by `RecordingSession` (needs Phase 5/6) |
| `Metadata/CoverArtWriter` (TagLib) | ✅ ported in Phase 3 — narrowed to Ogg/Opus cover art; ffmpeg writes the tags (§5.2) |
| `Audio/AudioRingBuffer` | ✅ ported — **fixed a data race**, added the coverage it never had |
| `Interop/PowerManagement`, `Interop/MediaKeys` via **CsWin32** | ✅ ported — hand-written `DllImport` gone |
| `Audio/AudioEndpoints`, `Audio/AudioSessions`, `Audio/SpotifyPlaybackProbe` | ✅ moved from the spike into Core |
| `Audio` capture + throttler (`LoopbackAudioCapture`, `AudioCaptureBuffer`) | ✅ ported in Phase 3 — pacing split from the device, so it is testable without one |
| `Interop/Routing` (the Phase 0 asset, moved into Core) | ✅ moved; spike now **references** Core instead of forking it |

**Reference test files still outstanding, and where each lands:**

| Reference file | Cases | Lands in |
| --- | --- | --- |
| `RecorderTests` | 7 | ✅ Phase 3 — the encode paths they covered are gone (§4); what they really tested lands in `TrackRecorderTests` and `RecordingSessionTests` |
| `FFmpegTests` | 7 | ✅ Phase 3 — rewritten as argv golden tests (§9.2 suite 2) |
| `MapperID3Tests` | 5 | ✅ Phase 3 — superseded by `CoverArtIntegrationTests`, which reads the picture back out of each container rather than asserting on a mapper's output |
| `SpotifyAPITests` | 9 | ✅ Phase 4 — superseded by `SpotifyTrackMapperTests` (18 cases) |
| `TranslationTests` | 4 | ✅ Phase 6 — en/fr key parity against Offstream's own `.resx` (`StringsTests`), plus the check that no key carries an inherited identifier |

Deliberate departures so far, each a consequence of a decision already taken:

- **`FromLegacySettings` is not ported.** It rebuilt a template from the predecessor's pre-template checkbox settings; with no importer (§6) nothing can supply its inputs. Its six test cases go with it.
- **The reference suite's idle-state test used the predecessor's product-name constant** as its negative case. §0 forbids that identifier, so the ported test asserts on a real track title instead — same property, stated more directly.
- **`GetTempFileName` → `GetTempPath` + `GetRandomFileName`.** The framework method is obsolete on modern .NET: it creates the file eagerly and gives up after 65535 names in a directory, which is a real ceiling for an app that records unattended overnight. The `.tmp` extension is kept because `DeleteFile` uses it to tell a scratch file from a finished recording.
- **`RecordingSettings.BitrateKbps` replaces `LAMEPreset`.** Forced by removing NAudio.Lame (§8); ffmpeg takes `-b:a {rate}k`.
- **Exceptions are typed.** The reference threw bare `Exception` for "no artist" and "empty file name"; those are now `UnrecognizedTrackException`, and invalid arguments are `ArgumentException`, so callers can distinguish them. Assertions changed from `Assert.Throws<Exception>` to the specific type — the only test edits made so far beyond renaming.
- **Title parsing is split from API enrichment.** The reference's `SpotifyStatus` both parsed the window title and called an `ExternalAPI.Instance` singleton to enrich the result, so no test could parse a title without stubbing a global. `SpotifyTitleParser` is now pure; enrichment moves to the metadata layer. This is the §3 "core must be testable" rule applied one level below the UI boundary, and it is why the ported process tests need no API stub at all.
- **`ISpotifyPlaybackProbe`** replaces a direct dependency on the whole audio-session manager. Track detection needs exactly one bit — is Spotify making sound — so it depends on one method instead of the audio stack, and stays testable with no audio hardware.
- **A data race in the circular buffer is fixed.** `Peek` captured the read position and byte count *before* taking the lock, so a concurrent write could move them underneath it and produce a torn read — in the buffer sitting between WASAPI capture and the recorders. All state is now read inside the lock, and the buffer gained the test coverage it never had (including a concurrency test that would catch a regression of exactly this shape). Reads are also span-based: the original allocated a fresh `byte[MaxLength]` on **every** read, in a hot path that runs continuously for hours.
- **The live Last.fm test becomes fixtures.** The reference's `TestAPIKeys_ReturnsOk` called the real API and failed offline — the exact case §9.3 forbids. Mapping is what can actually regress, so it is now covered by fixture-driven tests; a liveness check belongs in a manual or integration pass, not the safety net.
- **Recording rules are split from recording orchestration.** The reference's `Watcher` mixed pure decisions (is this track's type allowed? is this a new track? has the counter hit its ceiling?) with audio-session routing, sleep prevention, timers and direct calls into the WinForms form — so asserting on one boolean meant constructing the whole orchestrator with a form mock. `RecordingPolicy` holds the decisions and is directly testable; the orchestration that genuinely needs audio is deferred to pair with Phase 3, where the recorder's encode paths are replaced anyway (§4).
- **The poll loop is cancellation-driven, not timer-driven.** `SpotifyHandler` used `System.Timers.Timer` with an `async void` `Elapsed` handler, raised its events through fire-and-forget `Task.Run`, and guarded re-entrancy with a `bool`. All three fail only in production: an exception escaping `async void` takes the process down with an unreadable stack, a throwing event subscriber vanished silently, and the bool guard is a race. `SpotifyPoller` uses `PeriodicTimer` loops with a `CancellationToken`, raises events inline and in order, and cannot overlap polls by construction. Its tests drive `PollOnceAsync` directly, so they assert on the state machine instead of on wall-clock timing.

These mean the final count will not land on exactly 293. The number that matters is *every ported assertion passing*, with departures justified in writing rather than quietly dropped.

### Phase 3 — ffmpeg encoding (3–4 days) — ✅ **complete**
- ✅ ffmpeg resolution (`Encoding/FFmpegLocator`: configured → bundled → PATH) and version assertion (`Encoding/FFmpegVersion`).
- ✅ Format profiles as data (`Encoding/EncodingProfile`), `ArgumentList`-based runner with deadline and stderr drain (`Encoding/FFmpegRunner`).
- ✅ Worker queue for the encode backlog (`Encoding/EncodeBacklog`).
- ✅ Tags per format; ✅ cover art end-to-end (`Encoding/AudioEncoder` + `Metadata/CoverArtWriter`).
- ✅ **FLAC and AAC added** (§11) — a profile entry each, now that ffmpeg owns conversion.
- ✅ NAudio.Lame and the LAME DLLs: nothing to delete, as expected — the packages were never added and no file references them.

**Exit:** regression suites 2 and 3 pass for MP3, WAV, Opus, FLAC, AAC including cover art. **Met** — 475 tests green.

- **Suite 2 (argv golden tests)** — the exact argument vector per format × bitrate × metadata, asserted without invoking ffmpeg, so flag drift surfaces as a diff.
- **Suite 3 (encode integration)** — generates a `sine` via ffmpeg, encodes, then asserts codec, duration, tags and cover art with ffprobe. Tagged `Category=Ffmpeg`; CI installs ffmpeg 8.1.2 and `build.ps1` skips them when it is absent rather than failing.
- **The §5.2 Ogg trap is pinned by a test** that asserts tags are present under `stream_tags` **and absent under `format_tags`**, so the distinction cannot be lost again.
- **The injection case is covered both ways**: a golden test proves a hostile title stays one inert argv element, and an integration test encodes a title containing `" & echo pwned &` and reads it back intact.
- **Cover art is verified by reading it back**, per container (§5.2). The predecessor's Opus support looked right and was not, precisely because nothing ever reopened the file.
- **The backlog is unbounded and single-consumer.** Capture must never wait on encoding — the reference implementation encoded on the recording thread, which is how a slow lossless encode could clip the start of the next track. Unbounded because refusing a finished recording is worse than a long queue; one consumer because ffmpeg already uses every core it can, and a second encode would contend with the capture still running. Failures are events rather than exceptions, so one unencodable file cannot end the queue for the session, and a throwing subscriber cannot either.
- **Shutdown distinguishes draining from abandoning.** `CompleteAsync` finishes the backlog — closing the app with three tracks queued should produce three files — while disposal cancels, and starts nothing new. `ChannelReader.ReadAllAsync` hands over already-buffered items without re-checking the token, so the drain loop tests cancellation itself; without that, a cancelling shutdown still runs every queued encode.

**The two items §4 parked against this phase also landed:** the capture layer and the recorder orchestration that joins capture to the backlog. Both were deferred out of Phase 2 for needing the audio stack.

- **Capture pacing is separated from the capture device.** `AudioCaptureBuffer` holds the reference's four-second capacity and one-second reads; `LoopbackAudioCapture` is the WASAPI half behind `IAudioCaptureSource`. The original combined them, so none of the pacing could be reached without an audio endpoint — now all of it is a function of a `WaveFormat` and some bytes, and the recorder tests need no hardware. Waiting on a signal replaces the 100 ms poll loop.
- **The silence keep-alive now targets the endpoint being captured.** A render endpoint with nothing playing produces no loopback data at all, so silent gaps vanish rather than being recorded; the reference kept the *default* device alive, which is the wrong one whenever the user records anything else.
- **Recording length is measured from the bytes written, not from a timer.** The reference counted seconds on the watcher's tick and passed the number into the recorder, so a stalled capture still "recorded" for as long as the song played and a near-empty file could pass the minimum-length rule.
- **A missing first track was a retarget defect, found here and fixed.** `SpotifyPoller.Start` reset its current track to null, and a null previous track makes the first observation "no change" — so whatever was already playing when a session started was never recorded, only the song after it. The reference seeded an empty track when it began listening for exactly this reason. Now covered by a test.
- **The last chunk of every track was being dropped.** The write into the WAV honoured the stop token, and that token is cancelled at precisely the moment a track ends — with a chunk already taken out of the ring buffer and therefore unrecoverable. Writes of audio already in hand are now uncancellable, with a test that runs the race twenty times.
- **A failed encode keeps its WAV.** The captured audio cannot be recreated; a missing or broken ffmpeg can. The path is reported so the user can find it.

### Phase 4 — Dependency modernisation (4–5 days) — ✅ **complete**
- ✅ SpotifyAPI.Web 7.4.2 with PKCE + loopback redirect; EmbedIO dropped.
- ✅ `HttpClient`/`IHttpClientFactory` (the Spotify OAuth client routes through it); DI container wiring; options pattern (`SpotifyAuthOptions`).
- ✅ `System.Text.Json` — landed in Phase 5, where the app first writes JSON of its own (settings). ⬜ `System.IO.Compression` — still no use for it before the Phase 8 updater.

**Exit:** contract tests pass; manual Spotify auth verified against a real app registration. **Met.**
**Contract tests: 42 new tests (517 total), all offline.** **Manual verification: done**, via `tools/Offstream.SpotifyAuthProbe` against a real Spotify Developer Dashboard app registration — sign-in, a real `GetCurrentlyPlaying` call (returned an actual playing track), and a token refresh all succeeded on the first run, no code changes needed.

- **`SpotifyAPI.Web.Auth` is not referenced, on purpose.** It still depends on EmbedIO 3.5.2 even at 7.4.2 (verified against its published nuspec) — dropping EmbedIO is the point. The PKCE loopback redirect is caught by `Encoding.../Spotify/Auth/SpotifyLoopbackListener`, hand-rolled on the framework's own `HttpListener`. Binding to a literal loopback address rather than a wildcard prefix is what lets it run unelevated; verified with a real `HttpListener` bound to a real ephemeral port in CI, not mocked.
- **The state parameter is checked.** The reference implementation's `SpotifyAPI` did not validate it, which is how a stray or forged redirect could complete a sign-in it did not originate. `SpotifyAuthenticator` rejects a callback whose `state` does not match the one sent, with a test proving the token exchange never even runs in that case.
- **`SpotifyTrackMapper` replaces the reference's `SpotifyAPI.MapSpotifyTrackToTrack`/`MapSpotifyAlbumToTrack`**, reusing `SpotifyTitleParser.SplitTitle` for the title/subtitle split instead of a second copy of the same logic. Two real bugs surfaced while porting the reference's own test cases and fixed rather than carried forward:
  - `FullTrack.TrackNumber`/`DiscNumber` are non-nullable `int` on the current SDK, defaulting to 0 for a track it could not fully populate. The reference wrote that 0 straight into the tag; Spotify numbers both from 1, so 0 now maps to `null` instead of a literal (and wrong) zeroth track.
  - `DateTime.TryParse` rejects a bare four-digit year outright, silently dropping the release year for every album whose Spotify precision is year-only rather than a full date — the same call the reference made, with the same bug. Now parses "1987", "2010-10" and "2010-10-10" alike.
- **`SpotifyMetadataProvider` is the read half only** of the reference's `SpotifyAPI.UpdateTrack` — the retry-with-delay, fall-back-to-Last.fm-on-failure and reopen-the-auth-dialog orchestration is deliberately not ported here, because it belongs with whatever in the pipeline actually calls this, and nothing does yet. `RecordingSession` does not consume it: wiring a provider selection needs the settings screen (Phase 5) and the shell (Phase 6) that do not exist. The title-match guard against `RecordingSession`'s independently-racing detection *is* kept, reproduced from `IsPlaybackTrackDetectedTrack`. **Wired on 2026-08-12**, once both those phases were done — see *Metadata pipeline* below. Leaving it unwired is what made every recording carry two tags and no art.

**Reference test files still outstanding, and where each lands:** `SpotifyAPITests`' 9 cases are superseded by `SpotifyTrackMapperTests` (18 cases — the extra coverage is the two bugs above, plus `ChooseCoverUrl` edge cases the original never exercised).

### Phase 5 — Settings (1–2 days) — ✅ **complete**
- ✅ JSON schema (grouped sections `output`/`recording`/`metadata`/`app`), atomic writes, validation, `schemaVersion` handling.
- ✅ First-run defaults; DPAPI — **for the refresh token, not a client secret** (see below).
- ✅ No importer — §6. A test asserts the written file carries no inherited key vocabulary.

**Exit:** regression suite 4 passes; a fresh profile round-trips and a corrupted `settings.json` fails with a clear message rather than a crash. **Met** — 575 tests green, 56 of them new.

- **DPAPI protects the Spotify refresh token, because there is no client secret to protect.** This section was written before Phase 4 chose PKCE, which has no client secret at all — a public desktop app could never keep one confidential regardless of storage. What PKCE does produce is a long-lived refresh token granting API access on the user's behalf, so that is what goes through `ISecretProtector` on the way to disk. The Client ID stays readable: a PKCE public client's ID is sent in the clear on every authorize request and is not a credential on its own.
- **A token that will not decrypt is a normal outcome, not a corrupt file.** The same `settings.json` opened under a different Windows user, restored to another machine, or read after a credential reset will not decrypt. That costs one browser sign-in; refusing to load settings at all would cost the user every other preference they have, so the token becomes null and everything else loads.
- **System.Text.Json's source generator ignores property initializers, and that is why every settings record uses primary-constructor parameter defaults.** With `{ get; init; } = 320;`, JSON that omits `bitrateKbps` deserializes as **0**, not 320; reference types come back null rather than their initialized value. Reflection-based deserialization honours initializers, so the difference is invisible in review and shows up only as a settings file that quietly loads as zeroes — a hand-pruned file was enough to trigger it. Constructor parameter defaults *are* honoured. Measured against the actual generator rather than assumed, and pinned by `SettingsSchemaDefaultsTests` so a refactor back to initializers fails loudly.
- **The output path is the one default that cannot be a constructor default**, since it is derived from the current user's Music folder and a C# parameter default must be a compile-time constant. It is filled in on load instead, so omitting `path` behaves like omitting anything else. Null means "not specified" and gets the default; an explicit `""` is a value the user did set, and still fails validation rather than being silently overridden.
- **`OffstreamSettings` is deliberately not `RecordingSettings`.** The latter is the pipeline's working view: flat, full of computed properties, and mutated mid-session as the file counter increments. Persisting it directly would put derived values like `orderNumberMax` in the file and let the pipeline's convenience dictate the on-disk shape. `ToRecordingSettings` / `CaptureRuntimeState` bridge them, and `SettingsMappingTests` catches a field added to one and forgotten in the other — which would otherwise be a setting that silently does nothing.

### Phase 6 — WPF shell (8–10 days) ← the largest phase — ✅ **complete**

Delivered in four PRs: **PR 1** (shell scaffold, DI, navigation, design tokens), **PR 2** (Record page), **PR 3** (Settings and Advanced pages), **PR 4** (tray, minimise, single-instance guard, accessibility names, polish).

- ✅ App host, DI, navigation, Fluent theme, dark mode.
- ✅ **Record tab:** status, now-playing, elapsed, console log (with filter + copy), start/stop — **plus a live waveform**, which the plan did not ask for; see below.
- ✅ **Settings tab:** output path, device, quality, min length, format (incl. FLAC/AAC), metadata provider (*and, from 2026-08-12, the Last.fm API key and the Spotify sign-in that make the provider choice mean something*).
- ✅ **Advanced tab:** tray *(setting; the icon itself is below)*, timer, counter, filename template with a token reference **and live preview**, existing-file policy, detection options, tag options.
- ✅ Inline validation via `INotifyDataErrorInfo` instead of modal dialogs.
- ✅ i18n from `Offstream.App/Resources/Strings.resx` with an en/fr key-parity test (the reference tree's translation test, re-namespaced). **Resource keys are re-keyed for Offstream** — this is the file where inherited naming would otherwise survive longest.
- ✅ Tray icon, minimise behaviour, single-instance guard.

**Exit (as amended):** FlaUI suite covers the shell and the controls it drives — **the per-control page suites were dropped at the user's direction on 2026-08-12 in favour of manual verification** (PR 4 findings, below); key-parity test passes; side-by-side behavioural review against the reference app (behaviour compared, chrome and wording deliberately not copied). **Key-parity met** in PR 1; PR 4 closes the phase at **771 tests green** — 607 in `Offstream.Core.Tests`, 151 in `Offstream.UI.Tests`, and 13 Desktop-category FlaUI tests that CI and `build.ps1` exclude.

- **The three tabs are `NavigationView` items, not a `TabControl`.** §11 keeps the three-tab structure; it does not require the predecessor's control. A top-mode `NavigationView` reads as tabs, keeps each page a separate `Page` the container builds, and gets keyboard and UIA behaviour for free — which is what PR 4's FlaUI sweep will drive. ***Superseded by the redesign (2026-08-13): `NavigationView` is gone. See the redesign findings below.***
- **Pages come from the DI container via `INavigationViewPageProvider`, never from WPF-UI's default activator.** The default constructs pages reflectively, so a page whose constructor takes a ViewModel is built with nulls instead of failing — a blank tab with no exception. `PageProvider` resolves from the container, and `AppServicesTests` asserts every `Page` in `Views.Pages` is registered, since that failure is otherwise invisible until someone clicks the tab. ***Superseded: `PageProvider` is deleted; the shell takes the three pages as constructor parameters, so a missing registration now fails at startup. The `AppServicesTests` sweep survives, over `UserControl`.***
- **Pages and their ViewModels are singletons**, to match `NavigationCacheMode.Enabled`. A transient registration hands out an instance the navigation cache never displays, so a half-filled settings form quietly stops being the one on screen. ***Still true, for a different reason: the shell keeps all three loaded and switches them by visibility.***
- **XAML reaches strings through `{x:Static res:Strings.*}`**, against a `Strings` class generated from the .resx into `obj/` by MSBuild. A mistyped key fails the build rather than rendering an empty label. The cost is that a language change takes effect on the next launch — acceptable for a setting touched once, and the predecessor rebuilt its entire form to do it live.
- **Generating that class inside a WPF project needs an explicit ordering target, and the obvious version of it hangs the build.** Any XAML naming a local type makes WPF compile a throwaway assembly first, and that temp project invokes `CoreCompile` directly, so the generated `Strings` class does not exist during the pass that needs it. The fix is a target depending on `PrepareResourceNames;CoreResGen` — **not** on `PrepareResources`, which WPF extends with `MarkupCompilePass2ForMainAssembly` and which therefore recurses into markup compilation forever, silently, with no error output. Same class of trap for `NeutralResourcesLanguageAttribute`: the MSBuild property does not reach the temp project, so CA1824 fails there and only there, and the attribute lives in `AssemblyInfo.cs` instead. Both are commented at the site.
- **`SystemTheme` has twelve members and `ApplicationTheme` has four.** `== Dark ? Dark : Light` compiles, reads correctly, and renders the four high-contrast schemes as an ordinary light theme — silently undoing an accessibility setting. `ThemeService.FromSystem` maps them to `HighContrast` and everything unrecognised to Dark, pinned by `ThemeServiceTests`.
- **A settings file that will not load surfaces as an `InfoBar`, not a dialog.** §6's exit criterion asks for a clear message; the inline-validation rule rules out a modal. The message goes to the log as well, so it outlives the session.

**PR 2 (Record page) findings:**

- **The waveform is an addition to this plan, made deliberately.** Everything else on the Record tab — status, track, elapsed — is derived from Spotify's window title, which keeps changing whether or not a single sample reaches the encoder. Nothing on the predecessor's screen distinguished "recording" from "recording silence", and users found out at the end, from a folder of silent files. `Audio/AudioLevelMeter` plus `Views/Controls/WaveformView` is the smallest thing that closes that gap.
- **The meter is drained by the UI, not pushed from capture.** Capture raises `DataAvailable` roughly every 10 ms; turning each into a dispatcher callback would put ~100 UI marshals a second behind a decoration. The meter accumulates the interval's peak with a lock-free `Interlocked.CompareExchange` loop and resets on read, so a reader running at any rate still sees the loudest moment since it last looked, and the capture thread never blocks on the UI.
- **A WASAPI mix format reports its encoding as `Extensible`, not the float it wraps.** Reading `WaveFormat.Encoding` directly classifies an ordinary capture stream as unsupported and the meter never moves; `WaveFormatExtensible.ToStandardWaveFormat()` resolves it. Verified empirically, and pinned by a test — as is the reverse case, an unreadable format reading as silence rather than throwing, because a decoration must never cost a recording.
- **The waveform's clock is fixed, not the monitor's.** `CompositionTarget.Rendering` fires at the display refresh rate, so sampling per tick makes the waveform scroll at half speed on 60 Hz against 120 Hz. Bars are taken at a fixed 30 Hz off `RenderingEventArgs.RenderingTime`, and the frame event is only the invitation to check. The subscription is also torn down whenever the page is hidden or the meter goes away — `CompositionTarget.Rendering` is static, so a cached page would otherwise go on sampling from a tab nobody is looking at.
- **`TrackTimeChanged` fires on every 70 ms poll, not once a second.** That is what makes the elapsed counter smooth, and it means ~14 progress reports a second reach the ViewModel. Reports carrying no message are therefore *not* logged — logging them buries the session in ticks — and every property the handler touches is an `ObservableProperty` that raises nothing when the value is unchanged.
- **Sessions are built per start and never reused**, because `RecordingSession.StopAsync` disposes the poller it owns. `RecordingController` is the seam that knows this; it also resolves ffmpeg per session, so installing it or fixing the path mid-run works on the next start rather than needing an app restart.
- **Starting reports why it was refused instead of throwing.** No ffmpeg, no audio endpoint and unreadable settings are all things a user can fix, and all belong in the activity log next to everything else. Unreadable settings refuse outright rather than recording on defaults: writing to a folder the user never chose is worse than not recording. This is the other half of §6's "a corrupt file does not stop the app from opening" — it opens, and it says what is wrong before it touches the disk.
- **Copy takes what is on screen, filter included.** Someone copying the log is about to paste it into a bug report; handing them the lines they could not see would be handing them something other than what they were looking at. The clipboard call is guarded, because the Windows clipboard is a single shared lock any process can hold.
- **The log follows only while the user is already at the bottom.** A console that always jumps to the newest line makes reading back through a failure impossible during a session — which is exactly when someone wants to.

**PR 3 (Settings and Advanced pages) findings:**

- **There is no OK button.** A valid edit is written when the field commits; an invalid one is refused inline and never reaches the disk. This is only affordable because §6's `SettingsStore.Save` is atomic — a temp file beside the target plus `File.Move(overwrite: true)` — so a save per edit cannot leave a half-written file the way an in-place rewrite could. Text fields commit on focus loss rather than per keystroke: saving `1` on the way to `120` would persist a value the user never chose. The filename template is the deliberate exception, because its live preview is the point and a preview that waits for focus loss previews the previous template.
- **Both pages share one `SettingsDocument`, registered as a singleton.** Each page saves the whole settings file, so two documents would mean whichever tab was touched second silently reverted the other. The document also raises change notifications outward, which is how the Advanced page's preview updates when the output folder is edited on the Settings page.
- **Numeric fields are `string` on the ViewModel, not `int`.** Bound to an `int`, WPF's own converter rejects the text before `INotifyDataErrorInfo` ever sees it, and the user gets the framework's wording — or, mid-edit, an empty box that silently keeps the last good value. Holding the text and validating it gives every failure a message this app wrote, and the counter's bound is derived from the template's own padding (`{count:000}` stops at 999) rather than hard-coded.
- **`Persist()` calls `ValidateAllProperties()` first.** The toolkit's generated setter validates *after* raising `PropertyChanged`, so a handler that saves on change would otherwise act on the previous validation state. It is also what makes cross-field rules work: choosing Spotify as the metadata provider is invalid until a Client ID is present, and that is a fact about a different property than the one just edited.
- **Endpoint enumeration and the folder dialog are interfaces (`IAudioDeviceCatalog`, `IFolderPicker`).** One needs a sound card, the other a message loop, and a CI agent has neither. Behind those two seams, validation, saving and the preview are all ordinary unit tests — 58 of them here — instead of Desktop-category tests that only run on a developer's machine.
- **A configured device that is not currently connected stays selected and is shown as unavailable.** Dropping it from the list would silently re-point recording at the default endpoint, and the user would find out from the recordings. Unplugging a USB interface for an afternoon is not a decision to change the setting.
- **The preview renders through `OutputPaths.BuildFromTemplate`**, the same code the recorder uses, against a fixed sample track. A preview with its own formatter is a preview of a second implementation, and the interesting cases — token errors, the extension following the format, the counter's padding — are exactly where the two would drift.
- **The Spotify browser sign-in is deliberately not here.** The page offers the Client ID field, because that is a setting; the PKCE flow is not wired up, because no recording path consumes `SpotifyMetadataProvider` yet and a refresh token obtained now would be read by nothing. The Client ID also still reaches the container from configuration rather than from the settings file — making it live would force those registrations to become unconditional and re-read after every edit, which is work for the sign-in change to do.
- **Two pre-existing defects were found here and left for their own fixes.** (1) `RecordingSettings.MediaFormatExtension` returns `aac`, while `TrackRecorder` encodes through `EncodingProfiles.For(...)` and produces an m4a container — so AAC output is an m4a file named `.aac`, and this PR is what makes AAC selectable in the first place. (2) Nothing calls `OffstreamSettings.CaptureRuntimeState`, so the in-session file counter is never written back and numbering restarts on the next run — directly under the counter field this PR adds.

**PR 4 (tray, single instance, polish) findings:**

- **The per-control FlaUI page suites were dropped, at the user's direction (2026-08-12): "I can just manually test UI, dont need it automated."** Two suites covering every Settings and Advanced control had been written and were finding real things (see the focus finding below) before that call. What ships instead is 13 Desktop-category tests over the shell and the Record page, plus the **manual verification checklist in the PR description**. The automation ids and `AutomationProperties.Name` bindings **stay on every control regardless** — they are an accessibility requirement in their own right (Phase 9 asks for UIA names), and they are what any future suite would need. The phase exit criterion above is amended to match; re-adding page suites is cheap because the surface they need is already there.
- **`SetFocus` on a window does not move keyboard focus off the focused child** — and that is what made the abandoned suites report every text field as broken. Fields bind with the default `LostFocus` trigger (PR 3), so a value only reaches the settings file when focus actually leaves the box; focusing the *window* brings it to the front and leaves the caret exactly where it was. Typing Tab is what commits an edit. Worth recording because the symptom is a page that looks completely non-functional under automation while being perfectly correct in front of a user.
- **`IsOffscreen` does not mean "scrolled out of view" in WPF** — it follows `IsVisible`, which a control scrolled out of a `ScrollViewer` still has. A control below the fold therefore reports as on-screen and returns bounds that are off the page, so a click lands on whatever is drawn there instead. Containment of bounding rectangles plus the Scroll pattern is the only honest check. (Left here rather than in the code, since the helper that did this went with the suites.)
- **The single-instance mutex is `Local\`, not `Global\` — plan §0's name was wrong.** A global claim lets whoever logged in first block every other Windows user out of the app, and the activation signal cannot cross sessions to tell them why: their instance would simply exit with nothing on screen. Each logon session records its own audio, so each gets its own Offstream.
- **The claim is per data directory, keyed by a digest of the path.** What the guard protects is one `settings.json` against two writers, so a relocated `OFFSTREAM_HOME` is a different application in the sense that matters. The suffix is eight hex characters of SHA-256 rather than the path: a kernel object name is capped at 260 characters and cannot contain a backslash past the namespace prefix, so a pasted directory would be invalid, truncated, or both. This is also what lets the Desktop suite drive a window while the developer's own Offstream is running.
- **Standing down silently is the wrong half of single-instance.** The user double-clicked the icon because they wanted the window, and when the first instance is in the tray there is nothing on screen to tell them it is already running. The second process signals a named event and exits; the first surfaces. The signal arrives on a thread-pool thread, so it goes through the same ViewModel command the tray click uses rather than touching the window.
- **`OFFSTREAM_HOME` ignores a relative or malformed value rather than honouring it.** Resolving it against the working directory would scatter settings wherever the app was launched from, and the failure would present as settings that reset themselves at random.
- **WPF-UI 4.x carries no tray support, checked rather than assumed** — `Wpf.Ui.Tray` exists in the wpfui repository, but the 4.x package ships neither that namespace nor any `Shell_NotifyIcon` call, so taking the dependency the project already has was not an option. §3's choice stands: **H.NotifyIcon.Wpf**, the maintained MIT fork of Hardcodet.NotifyIcon.Wpf (the original is CPOL, which is worth avoiding in something that ships).
- **The tray icon exists only while the window is hidden.** An always-present icon is a permanent tray citizen nobody asked for; the setting is called *minimise to tray*, so the icon is what the window turned into. Its two states are one shape with a different fill — red while recording, which is the single moment the user cannot see the Record page and still needs to know a session is running.
- **The tray menu has its own Exit.** Without one, an app hidden in the tray can only be quit by restoring it first — the predecessor's tray icon had no menu at all, and that is where the papercut came from.
- **Surfacing takes three steps in order:** `Show()` undoes the hide, `WindowState = Normal` undoes the minimise (the window is still minimised until it is restored, so skipping this shows a window that is not on screen), and `Activate()` stops it coming back behind whatever the user was looking at.
- **The tray reads recording state from `RecordingController`, not from `RecordViewModel`.** Both are singletons so either compiles, but the controller is the source of truth and the Record page is a peer reading the same events — chaining one ViewModel off another would mean the tray silently stopped updating whenever the page's logic changed.
- **The level meter needed an automation peer to exist at all.** (Written of `WaveformView`; the control is now `LcdMeterView` — see the Record page display section below — and the finding carries over unchanged.) `FrameworkElement` creates none, so without it the meter is invisible to a screen reader and the `AutomationProperties.Name` the page sets on it reaches nothing. Reported as an image rather than a progress bar: claiming a progress bar promises a range pattern and a value this has no meaning for.
- **The two defects PR 3 found are still open and still deliberately unfixed here** — AAC output is an m4a container written to a `.aac` filename (`RecordingSettings.MediaFormatExtension` against `EncodingProfiles.For`), and nothing calls `OffstreamSettings.CaptureRuntimeState`, so the in-session file counter never reaches the disk and numbering restarts every run. Both are pipeline behaviour with their own tests to write; neither belongs in a shell PR. **Both fixed on 2026-08-12** — see the metadata pipeline section below.

**Redesign findings (2026-08-13):**

The user supplied two mockups — a Record tab and a Settings tab — and three constraints: the
Advanced tab gets the same treatment in two columns, neither settings page may scroll, and the
transport button belongs on the Record tab only. Two open questions were settled by the user:
refusals appear as a red bar above the display and the display carries no status row at all; the
window's minimum size is **1024 × 700**.

- **`NavigationView` is gone, and removing it paid twice.** The design asks for a flat label over
  an accent underline, which is not Fluent's navigation idiom — reproducing it meant retemplating
  the control down to a shape it does not have. Three `RadioButton`s in one group bound to a
  `ShellTab` enum through `EnumToBooleanConverter` is less machinery, and it took
  `NavigationViewContentPresenter` out of the tree with it, which is what had forced
  `ScrollViewer.CanContentScroll="False"` onto the Record page. `PageProvider` and the
  `INavigationService` registration are deleted.
- **The converter's `ConvertBack` returns `Binding.DoNothing` for an unchecked button.** Unchecking
  is what happens to the *outgoing* button when another is picked; writing anything back for it
  races the incoming button and settles on whichever the group updated last.
- **The three pages became `UserControl`s, and this is not cosmetic.** `Page` throws
  *"Page can have only Window or Frame as parent"* the moment a `ContentControl` hosts it — the
  crash lands in `MeasureOverride`, so it survives compilation and the first render and only
  appears when the shell arranges. Nothing is lost: a `Page` outside a navigation frame is a
  `UserControl` with a `Title` nobody reads.
- **WPF-UI reassigns `FluentWindow.Background` after load**, from the theme, when it applies a
  backdrop — including `WindowBackdropType="None"`. A colour set on the window is silently
  overwritten. The near-black is painted by the root `Grid` instead.
- **Pack URIs are assembly-qualified now** (`/Offstream;component/Assets/…`). Unqualified ones
  resolve against `Application.ResourceAssembly`, which is the *entry* assembly — the app when the
  app runs, the test host when anything else loads the same XAML — and it cannot be reassigned once
  set. Note the assembly is `Offstream`, not `Offstream.App`.
- **Field labels are top-aligned with a 7 px nudge, not centred.** On a one-control row the nudge
  lands on the control's centre line anyway; on a row whose control carries a hint or a validation
  message under it, centring pushed the label halfway down a stack and left it pointing at nothing.
  They wrap rather than trim for the same reason — a label ending in an ellipsis is a setting the
  user has to click to identify.
- **The token reference moved from a `CardExpander` to a flyout.** Ten rows of reference text
  pushed everything below off the page the moment it opened, which is not available on a page that
  promises not to scroll. A `ToggleButton` and a `Popup` share `IsTokenReferenceOpen`, and
  `StaysOpen="False"` means an outside click writes `false` back through the same property that
  pops the button out.
- **The minimum size is the no-scroll promise made structural.** Advanced's two columns need
  roughly 1024 wide; both pages lay out every field at once, which only holds above a size. Setting
  the floor to the design size means the window cannot be dragged to where a setting is
  unreachable.

### Metadata pipeline — closing Phase 4's open end (2026-08-12) — ✅ **complete**

Reported from a real recording session: audio and track detection worked, MP3 and Opus both encoded, and **every file carried an artist and a title and nothing else** — no album, no track number, no year, no art — with both Last.fm and Spotify selected in turn.

The cause was not a broken provider. **There was no pipeline stage at all.** `RecordingSession` handed `TrackRecorder` the `Track` scraped from the window title, `TrackRecorder` built the `EncodeRequest` straight from it, and `FFmpegArguments` faithfully wrote the two fields it had. `SpotifyMetadataProvider` existed but nothing resolved it (its own doc comment said so); no Last.fm provider class existed at all, only the pure `LastFmTrackMapper`; and `EncodeRequest.CoverArtPath` was never anything but null because nothing ever fetched `Track.AlbumArtUrl`. The Settings page offered a provider dropdown that was read, validated and saved, and then consulted by nobody.

- **`IMetadataProvider` is the seam, and it is the reference's `IExternalAPI` minus its authentication members.** Those existed because the interface doubled as the app's sign-in surface — a static `ExternalAPI.Instance` the form prodded into authenticating on demand. Offstream signs in on the Settings page and hands an already-authenticated client to the provider, so an enrichment call is only ever an enrichment call. `NoMetadataProvider` makes "none selected" and "found nothing" take the same path rather than a null check per call site.
- **`LastFmMetadataProvider` ports the two behaviours that make the common case work**, both easy to drop in a rewrite and invisible until half a library comes back untagged: retrying with the title stripped of its Spotify decoration ("(Remastered 2011)", " - Live at Wembley"), and looking a single up through `album.getInfo` when the track response has no album or attributes one to "Various Artists". Requests go over HTTPS through an injected `HttpClient` instead of `XmlDocument.Load(url)` over plain HTTP — that call fetched and parsed in one blocking step, with no timeout and no way to test it. The parser prohibits DTDs and resolves no external entities, because this is unauthenticated XML from the network reaching an `XmlSerializer`.
- **The Last.fm API key is the user's own, and is a new setting.** The reference shipped three of its own keys hard-coded in its source and picked one at random per run. Offstream does not appropriate another project's credentials, and a key shared by every install is rate-limited collectively and revocable by someone who is not the user. `metadata.lastFmApiKey` is the field; the Settings page links to where one is made.
- **A missing Last.fm key is a warning, not a validation error**, unlike the Spotify Client ID beside it. Last.fm is the *default* provider, so a fresh install is in that state before the user has touched anything — and `Persist()` refuses the whole document whenever any field is in error, so an error would mean a first run could not save its output folder until an unrelated API key was pasted in. Spotify is only ever reached by choosing it.
- **Spotify sign-in now exists, and is why that provider could never have worked.** The Client ID field identifies an app; it grants nothing. Nothing resolved `SpotifyAuthenticator` from the container, so no refresh token was ever obtained, so there was no token for a session to present. `ISpotifyAccount` builds the auth objects **per call rather than at startup**, because the Client ID is the user's own and changes without a restart — a singleton `SpotifyAuthOptions` captured when the window opened would sign the user in with a stale one. The startup registrations that were keyed off a configured `Spotify:ClientId` are gone with it.
- **Renewal is the SDK's job, and the rotated refresh token is persisted.** `PKCEAuthenticator` redeems an expired access token itself, which matters because a session easily outlives the one hour a token lasts. Spotify rotates the refresh token on every renewal; not writing the replacement back is how a long-running install silently stops working, so `TokenRefreshed` goes straight to `SettingsDocument`.
- **Enrichment starts when the track starts, and is joined just before the encode request is built.** A lookup takes about a second and a track plays for minutes, so overlapping them makes it free; doing it after the recording would add that second to every file. The join is at the last moment the metadata can still reach ffmpeg. It is also *bounded* — `TrackEnricher` carries a deadline and catches everything, so a provider that stops answering costs the tags and never the recording, and a recording discarded as too short or silent never waits at all.
- **The track is snapshotted before enrichment writes to it.** The poller owns the instance it hands over and keeps reporting against it; the copy is the recorder's alone.
- **The counter reaches the track-number tag through `EncodeRequest.TrackNumberOverride`, not by mutating the track.** `OrderNumberInMediaTagEnabled` and `OrderNumberAsTag` were settings nothing consumed. Overriding the tag rather than `Track.AlbumPosition` keeps the `{track}` filename token meaning the position within the album, which is what the reference did too.
- **The fetched cover is a scratch file with the same lifetime as the temp WAV**, deleted once the encode is done with it either way.
- **`RecordingSettings.MediaFormatExtension` now comes from the encoding profile** rather than the lower-cased enum member. That is the AAC defect: the profile encodes into an MP4 container, so the file is an `.m4a`, and calling it `.aac` produced something Windows and most players refuse to open.
- **`RecordingController` writes the file counter back when a session stops**, through `SettingsDocument` rather than the store, so the working copy the settings pages hold cannot revert it on the next edit. Starting a session now re-reads the file for the same reason — and it also means a `settings.json` corrected since startup works without a restart.

#### Follow-up from testing the fix: art that only VLC could see, and the tags still missing

Once metadata reached the files, a second report followed: the album art was in the MP3s and showed in VLC, but not in Windows Media Player and not in File Explorer — where it had worked in the predecessor. A hex dump of the header settled it: `ID3 04`.

- **ffmpeg writes ID3v2.4 by default; Windows has never read v2.4 cover art.** Explorer's thumbnail handler and Windows Media Player both ignore the APIC frame in a v2.4 tag, so a correctly-written picture is simply invisible in the two places most users look. VLC reads both versions, which is exactly why the symptom looked like a player quirk rather than a tagging defect. The predecessor tagged with TagLib#, whose default is v2.3 — so this was a regression introduced by moving tagging to ffmpeg, and it had nothing to do with the art itself. MP3's profile now carries `-id3v2_version 3`.
- **The flag lives on the profile, not in the argument builder.** `EncodingProfile.ContainerArguments` is a second, separate list from `CodecArguments`, applied after them and before the output path: these configure the file being written rather than the audio going into it, and only MP3 needs any. Verified with `ffprobe` that nothing is lost by the downgrade — the full `1997-03-04` date and a `4/12` track number both survive v2.3, which is the thing worth checking before pinning an older tag version.
- **`artist` was the album artist on every enriched file.** The tag was built from `Track.Artists`, which returns `AlbumArtists` whenever they are known — almost always, once a provider has run. So `artist` and `album_artist` came out identical and featured performers vanished. The reference wrote `Performers` → TPE1 and `AlbumArtists` → TPE2; `PerformerCredit` restores that split. The `{artist}` filename token still renders from `Track.Artists`, so no existing library's file names move.
- **Last.fm's `toptags` node was never read.** The mapper hard-coded `Genres = []`. With Spotify having stopped returning album genres for most of its catalogue, that left the genre tag empty under either provider. Only the three most-applied tags are written: Last.fm's tags are a folksonomy, so the tail of the cloud is listener bookkeeping ("seen live", "favourites") rather than genre.
- **Three tags added because the data was already in hand.** The album call that Spotify enrichment already makes carries the release date at full precision, the album's track total, and the copyright lines. `Track.ReleaseDate` keeps Spotify's precision for the tag while `Track.Year` stays an integer for the `{year}` token — a folder should not gain a month and a day. The track total gives the `4/12` form that tells a partial rip from a complete album. The copyright prefers the phonogram (`P`) line over the composition (`C`) line, because Offstream records audio.
- **The release date is shape-matched, not merely parsed.** `DateTime.TryParse` accepts `10/10/2010` happily, and writing that into a tag produces a date no two readers agree on. Only Spotify's three documented shapes — `2010`, `2010-10`, `2010-10-10` — pass through; anything else is dropped, since a malformed date is worse than none.
- **ISRC and publisher have no source and are deliberately absent.** Both would be worth writing. Spotify removed `external_ids` and `label` from its API, and `SpotifyAPI.Web` marks both obsolete with "field has been removed" — analyzers-as-errors caught the attempt. Last.fm never supplied either. Adding the properties would mean adding them with nothing to fill them.

#### Second follow-up: the guard was ported without the retry that makes it usable

A session log showed Spotify tagging two tracks and skipping the one between them — *"Spotify had no metadata for …"* — with a single `currently-playing` call and no album call after it. That narrows to exactly one exit: `MatchesDetectedTrack` returned false.

- **The window title and Spotify's backend are not the same clock.** The title changes the instant the desktop client advances; `/v1/me/player/currently-playing` is served from player state that trails it by a second or more. Asking once at a track boundary therefore gets the *previous* track back, the guard correctly refuses to tag the new recording with it, and the file is saved bare. Whether a given boundary lands inside that lag is luck, which is why two tracks in the same session tagged fine and one did not.
- **The reference solved this and the port dropped half of it.** `SpotifyAPI.UpdateTrack` opens with `await Task.Delay(100)` before its first poll, and on a failed `IsPlaybackTrackDetectedTrack` does `await Task.Delay(1000)` and retries once. Offstream kept `IsPlaybackTrackDetectedTrack` — the plan called it "reproduced exactly" — and kept neither delay. The guard without the retry is strictly worse than not having ported it: it turns a wrong tag into no tag, which is the right trade but only if something then asks again.
- **The retry belongs in the provider, not in `TrackEnricher`.** The original doc comment argued this was orchestration for the caller. That was wrong for a mechanical reason: `EnrichAsync` returns a bare `bool`, so the caller cannot tell "mismatch, ask again in a second" from "nothing is playing, asking again is pointless". Only the provider knows which it saw. Last.fm has no equivalent race — it is a search by title, with no player state to lag.
- **Four attempts rather than the reference's two**, because the budget is different: enrichment is bounded by `TrackEnricher.DefaultDeadline` (20s) and runs concurrently with a recording lasting minutes, so ~3s of chasing costs nothing. `SpotifyPollingOptions` carries the timings so tests exercise the retry without waiting them out.
- **A momentary empty answer is the same race**, so a 204 is retried too — the reference retried that case as well. A podcast episode is not, and fails on the first look: no number of retries turns an episode into a track.
- **The log line now says what Spotify answered.** "Spotify had no metadata for X" was true and useless; the mismatch is logged at debug with both titles, which is what would have answered this question from the log instead of from the source.

#### Third follow-up: the Record page display (2026-08-13)

Reported as a slow, laggy Record page whose elapsed counter ran behind Spotify, and a waveform
that was "almost always peaked". Three separate causes, and the last one changed what the page
looks like.

- **The freeze was `SpotifyPoller.Start()` capturing the dispatcher, not the drawing.** Start is
  called from a button click, so the WPF `SynchronizationContext` was current, and every `await`
  in the poll loop without `ConfigureAwait(false)` resumed on the UI thread — window-title reads
  fourteen times a second, and `StopCurrentRecorder`'s blocking wait on the capture buffer at
  every track change. Proven by hashing the frames of a screen recording: 2.53 s and 2.33 s of
  byte-identical frames, with the counter jumping 0:30 → 0:32 across one of them. The loops now
  start on the pool via `Task.Run`, and `SpotifyPollerTests` pins it with a counting
  `SynchronizationContext` that must never be posted to. **Three earlier diagnoses — geometry
  batching, `DrawingVisual`, log filtering — were all wrong**; each made the page cheaper to draw
  and none of them touched a blocked thread. The user's own read ("it's like it's waiting for
  something") was the signal that it was a block rather than a cost.
- **The counter drifted because it counted ticks instead of reading a clock.** `PeriodicTimer`
  never makes up a tick it delivered late, so ten seconds that produced four ticks read as four
  seconds and stayed four behind for the rest of the track. It now samples a monotonic clock
  through `TimeProvider`, which is also what makes the drift testable without waiting.
- **A waveform cannot work against a loudness-normalised source.** Peak over a 33 ms display
  interval is at or near full scale for essentially all mastered music, so every bar came out the
  same height and the control drew a solid block. Moving to RMS on a decibel scale fixed the
  measurement, but Spotify normalises to about −14 dBFS and the remaining dynamic range is a few
  decibels — not enough for a scroll to be worth the space.
- **What replaced it is a field recorder's display.** `LcdMeterView` draws L and R segment bars
  over a −50…0 dB scale with ticks at −50, −30, −20, −12, −6 and 0, and the held peak printed in
  dBFS; the transport state, the counter, what is playing and the output format sit above it on
  the same panel. The ruler is the part a progress bar cannot offer — it turns "about two-thirds
  along" into "near −12 dB". `AudioLevelMeter` gained per-channel accumulation to feed it, and
  drains through `LevelReading`, which carries the dBFS figure alongside the 0–1 level so the bars
  and the printed numbers are one measurement rather than two scales that agree by accident.
- **Unlit cells stay faintly visible, and that is the whole point of the control.** Everything
  else on the page comes from Spotify's window title, which keeps changing whether or not a
  sample reaches the encoder. A silent meter has to read as a working meter showing nothing
  rather than as one that has stopped — the failure this page exists to make visible is a folder
  of silent files discovered in the morning.
- **The indicator blinks while armed and holds solid while capturing**, which is the standby-
  against-rolling convention every recorder uses, and the only cue the page has for a distinction
  the transport buttons cannot show — armed and capturing are both "running" and both offer Stop.
  Discrete key frames rather than a fade, because an LCD segment has no in-between and a
  cross-fade reads as a glow; one second per cycle, well under the rate that matters for
  photosensitivity; and gated on `SystemParameters.ClientAreaAnimation`, so a user who turned
  animations off gets the static outlined block, which still differs from capturing's inverted one.
- **The palette is fixed rather than themed**, because a physical LCD looks the same in a dark
  room. The transport buttons stay in the app's own style, outside the panel: an LCD is not
  clickable, and styling a button to look like one would be a lie about what can be pressed.
- **The segment grid is a tiled mask over continuous bars**, one draw call at any width, with the
  pitch derived from the width to hold about forty cells. At a fixed 5 px a wide panel came out as
  a hundred hairlines that read as texture on a solid bar rather than as discrete steps.
- **The log pane grew in two independent ways.** `LogLines` — the collection the `ListBox` binds
  to — was never trimmed even though the backing buffer was, so an overnight session ended with a
  pane holding far more lines than the sink retained. Separately, WPF-UI's
  `NavigationViewContentPresenter` reads `ScrollViewer.GetCanContentScroll(page)` on navigation
  and swaps in a template that wraps the page in a `DynamicScrollViewer` — and its static
  constructor defaults that property to `True` for every `Page`. Inside that scroller the page is
  measured with infinite height, so the star row resolved to its content's size instead of the
  viewport and the card grew off the bottom of the window. `ScrollViewer.CanContentScroll="False"`
  on the Record page opts out; Settings and Advanced keep the default because they are long forms
  that genuinely want to scroll as a whole. ***The second half is obsolete as of the redesign
  (2026-08-13): removing `NavigationView` removed the presenter, and no page opts out of anything
  any more. The `LogLines` half stands, and the FlaUI test that pins the log inside the window is
  kept.***

### Phase 7 — Windows integration polish (3–4 days)
- **Raise the TFM to `net10.0-windows10.0.22621.0` first.** SMTC needs WinRT projections, which a bare `net10.0-windows` TFM does not provide. This is now free: Windows 11's floor is build 22000, so a versioned TFM costs no supported users. Expect the change to be mechanical (one property in `Directory.Build.props`) but verify the routing interop still binds afterwards — it is hand-rolled COM and the projections change what the compiler generates around WinRT types.
- SMTC (`Windows.Media.Control`) as primary track source with title polling as fallback — works when Spotify has no visible window.
- Device hot-plug handling.
- Long-path (`\\?\`) support.
- VB-CABLE detection, and — **subject to the licence constraint below** — elevated installer invocation.

> **VB-CABLE licence constraint (blocks the shipping form of this item and Phase 8).** VB-CABLE is donationware owned by Vincent Burel, not open source. Its bundled `readme.txt` permits redistribution of the package **"AS IS without any modification"** but states plainly: *"It is not allowed to integrate the VB-CABLE package in another software installation procedure without Author agreement."* It also asks that any distribution mention the origin (`www.vb-cable.com`) and that it is donationware.
>
> The predecessor ships the whole package in `EspionSpotify/Drivers/` — both setup executables, the control panel, and the raw `.inf`/`.sys`/`.cat` payload — copies it next to the executable at build time, and launches VB's own setup elevated (`Verb = "runas"`) from a link in its UI. Shelling out to the unmodified vendor setup plausibly falls under "copy and diffuse AS IS"; folding those files into a WiX/Inno sequence in Phase 8 clearly does not. The predecessor also displays neither required attribution.
>
> **Until open question 9 is answered, build detection only.** Detection is unencumbered: enumerate endpoints and match the device name, exactly as the predecessor's `ExistsInAudioEndPointDevices` does. If the cable is absent, link the user to vb-audio.com rather than bundling an installer. Whichever option is chosen, the attribution line ships with it.

**Exit:** verified on a clean VM; track detection survives Spotify minimised to tray; VB-CABLE presence is detected and its absence degrades to a documented link, with no vendor binaries in the repo pending question 9.

### Phase 8 — Packaging and release (4–5 days)
- WiX v4 (or Inno Setup) per-user installer; app itself requires no admin.
- **Code signing** — start certificate procurement in Phase 0; unsigned binaries trip SmartScreen and undercut "professional".
- Bundled ffmpeg with licence and source offer.
- **Do not chain the VB-CABLE installer into the installer sequence** — that is the specific act its licence forbids without the author's agreement (Phase 7 note). If question 9 lands on bundling, ship the vendor package unmodified alongside the app and let the user launch it, mirroring how the predecessor does it.
- Third-party notices page: ffmpeg (LGPL + source offer), the predecessor's MIT notice, and VB-CABLE attribution if it ships at all.
- Auto-update: signed manifest, background download, signature verification, apply on restart.
- Tag-driven GitHub Actions release pipeline.

**Exit:** clean-VM install → record → update → uninstall, no leftovers; third-party notices complete and accurate for whatever actually shipped.

### Phase 9 — Hardening (5+ days, ongoing)
- 12-hour soak; memory and handle profiling.
- Fault injection: device removed mid-recording, disk full, ffmpeg killed, Spotify closed, sleep/resume.
- Accessibility: keyboard navigation, UIA names, contrast.
- Budget: < 2% CPU idle, < 8% recording+encoding, < 200 MB RSS.
- User guide, FAQ, troubleshooting, developer setup.

**Exit:** soak clean; every fault case degrades gracefully with a clear console message.

**Total: ~5–7 weeks** focused single-developer, Phase 6 carrying the widest variance.

---

## 11. Improvements

Deliberate, and this is the complete list — anything else goes to a backlog.

1. **Live filename preview** — render the template against a sample track as the user types.
2. **Inline validation** rather than modal dialogs.
3. **FLAC and AAC output.**
4. **SMTC track detection** — works without a visible Spotify window.
5. **Long-path support** — removes 260-char truncation.
6. **DPAPI-protected credentials** — the predecessor stores the client secret in plaintext.
7. **Structured rotating logs** — replaces console text stuffed into a settings string.
8. **Device hot-plug handling.**
9. **Self-contained publish** — no .NET Framework 4.6.1 dependency (out of support since April 2022).
10. **Signed installer and auto-update.**
11. **Testable UI** — ViewModels under test; the predecessor's form has zero coverage.
12. **A settings layer with no inherited vocabulary** — grouped JSON schema, clean slate, no importer (§6).

Explicitly **not** changing: the three-tab structure, the console-log metaphor, the recording model, or the filename template syntax. The app should feel familiar to anyone arriving from the predecessor — while carrying none of its names, its settings file, or its identifiers (§0).

---

## 12. Risks

| Risk | Impact | Likelihood | Mitigation |
| --- | --- | --- | --- |
| ~~Routing COM breaks under .NET 10~~ | High | ~~Low~~ **Occurred** | **Resolved in Phase 0** — it was certain, not low: `IAudioPolicyConfig` is WinRT/IInspectable-based and .NET 5 removed that support. Marshalling rewritten and proven; see DR-0001 |
| ~~Downlevel (Win10 22H2) routing unverified~~ | — | — | **Closed** — Windows 10 is out of scope (open question 6). Only the 21H2+ IID path ships, and it is proven |
| Routing proven by COM round-trip, not audibly | Low | Medium | This machine has one render endpoint. Confirm with a second endpoint (VB-CABLE or any second output) during Phase 7's clean-VM pass |
| NAudio 1.10 → 2.x behavioural drift in capture | Medium | Medium | Existing tests + Phase 0 capture spike |
| SpotifyAPI.Web 5→7 auth rework | Medium | High | Isolated behind the provider interface |
| WPF rewrite scope creep | Medium | High | §11 is closed; parity matrix is the gate |
| ffmpeg cover art unreliable for Ogg | Medium | Medium | Per-format ffprobe tests; TagLib# fallback for that container |
| Trimming/AOT breaking COM | High | Low | Explicitly disabled; documented in §2.2 |
| Code-signing cost and lead time | Medium | High | Begin procurement during Phase 0 |
| ffmpeg licence obligations | Medium | Low | LGPL-only build; ship licence + source offer |
| **VB-CABLE redistribution terms** | Medium | Medium | Detect-only by default (open question 9); never chain its setup into the installer; ship attribution if it ships at all |

---

## 13. Open questions

1. ~~.NET 10 confirmed as current LTS, and WPF-UI's support for it?~~ **Answered (DR-0001).** .NET 10 is LTS to 14 Nov 2028; WPF-UI 4.3.0 ships a `net10.0-windows7.0` target and compiles clean with CommunityToolkit.Mvvm 8.4.2.
2. ~~Bundle ffmpeg (recommended) or resolve at runtime?~~ **Answered (DR-0001).** Bundle an LGPL-only build with a runtime override; every required encoder (`libmp3lame`, `libopus`, `aac`, native `flac`) is present in a stock build, so no GPL component is needed.
3. Signing certificate: EV (immediate SmartScreen reputation, hardware token) or OV (cheaper, reputation accrues)?
4. Auto-update wanted at all, given the predecessor's updater was deliberately disabled there?
5. Keep French and add more languages, or English-only for v1?
6. ~~Is a Windows 10 floor acceptable, or must Windows 11 features stay optional?~~ **Answered 2026-08-11: Windows 11 only.** Windows 10 left support in October 2025. Consequences: Phase 0's downlevel gap is closed rather than deferred; Windows 11 features need no optional path; and the TFM may be raised to `net10.0-windows10.0.22621.0`, which unlocks the WinRT projections SMTC track detection needs (§5.3, Phase 7) — see the note there before Phase 7 starts.
7. Should FLAC/AAC ship in v1, or wait until parity is proven in the field?
8. Does the clean-slate settings decision (§6) need a one-page "moving from the predecessor" note in the docs, listing which preferences to re-enter?
9. **How does Offstream handle VB-CABLE?** Its licence permits redistributing the package *as is* but forbids integrating it into another installation procedure without the author's agreement, and asks for origin and donationware attribution (Phase 7). Three options:
   - **(a) Detect only** — link users to vb-audio.com. Zero licence exposure, one extra manual step. **Recommended for v1**, and the assumption Phase 7 builds against.
   - **(b) Ship the vendor package unmodified** next to the app and launch its setup elevated, as the predecessor does — with the attribution the predecessor omits. Plausibly permitted; adds ~3 MB of unsigned third-party kernel-mode driver payload to the release.
   - **(c) Ask VB-Audio for written agreement** to bundle properly. Cleanest if granted; unknown lead time, so it cannot gate a release.

   This needs deciding before Phase 7 finishes, not at Phase 8, because it determines whether any vendor binaries enter the repo at all. Note the shipping question is separate from the *runtime* one: whether recording quality without a virtual cable is good enough to make it optional at all is a Phase 0 measurement.
