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
| Single-instance mutex | `Global\Offstream` |
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

Offstream stores settings at **`%APPDATA%\Offstream\settings.json`**, bound through `Microsoft.Extensions.Configuration`, with a `schemaVersion` field, validation on load, and atomic writes (temp file + `File.Move` with overwrite).

**There is no import from the predecessor.** Offstream does not read `%LOCALAPPDATA%\Spytify\user.config`, does not know its key names, and ships no migration code. A first run starts from defaults and the first-run experience is designed for that: sensible defaults for output path (`%USERPROFILE%\Music\Offstream`), format, and template, so the app is usable before the user opens Settings at all. Anyone moving over re-enters their preferences once.

This is a deliberate trade — it costs existing users one setup pass and buys a settings layer with no legacy key vocabulary anywhere in it.

The JSON schema is designed for Offstream, not transcribed from the old flat keys: grouped sections (`output`, `recording`, `metadata`, `app`) with nested objects rather than `settings_*` / `advanced_*` prefixes.

Two rules the schema must honour:

- **No log text in settings.** Logs go to a rotating Serilog file under `%APPDATA%\Offstream\logs\`. Unbounded console text in a settings string is fragile.
- **The Spotify API client secret is never written in the clear.** Protect it with DPAPI (`ProtectedData`, `CurrentUser` scope) before it reaches disk.

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
| Newtonsoft.Json | 13.0.1 | **System.Text.Json** | Source-generated contexts |
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
| `TranslationTests` | 4 | Phase 6 — en/fr key parity against Offstream's own `.resx` |

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

### Phase 4 — Dependency modernisation (4–5 days) — 🔄 **auth flow and mapping complete; manual verification outstanding**
- ✅ SpotifyAPI.Web 7.4.2 with PKCE + loopback redirect; EmbedIO dropped.
- ✅ `HttpClient`/`IHttpClientFactory` (the Spotify OAuth client routes through it); DI container wiring; options pattern (`SpotifyAuthOptions`).
- ⬜ `System.Text.Json` / `System.IO.Compression` — no concrete usage yet; nothing in the app writes JSON of its own before Phase 5, and nothing compresses anything before the Phase 8 updater. Left as a policy for those phases rather than forced in here.

**Exit:** contract tests pass; manual Spotify auth verified against a real app registration.
**Contract tests: met (99 new tests, all offline).** **Manual verification: outstanding — needs a human.** Nothing automated can complete an interactive OAuth browser sign-in against Spotify's real servers; `tools/Offstream.SpotifyAuthProbe` is the tool for whoever picks this up to run once, against a real Spotify Developer Dashboard app registration. Its README has the two-minute setup.

- **`SpotifyAPI.Web.Auth` is not referenced, on purpose.** It still depends on EmbedIO 3.5.2 even at 7.4.2 (verified against its published nuspec) — dropping EmbedIO is the point. The PKCE loopback redirect is caught by `Encoding.../Spotify/Auth/SpotifyLoopbackListener`, hand-rolled on the framework's own `HttpListener`. Binding to a literal loopback address rather than a wildcard prefix is what lets it run unelevated; verified with a real `HttpListener` bound to a real ephemeral port in CI, not mocked.
- **The state parameter is checked.** The reference implementation's `SpotifyAPI` did not validate it, which is how a stray or forged redirect could complete a sign-in it did not originate. `SpotifyAuthenticator` rejects a callback whose `state` does not match the one sent, with a test proving the token exchange never even runs in that case.
- **`SpotifyTrackMapper` replaces the reference's `SpotifyAPI.MapSpotifyTrackToTrack`/`MapSpotifyAlbumToTrack`**, reusing `SpotifyTitleParser.SplitTitle` for the title/subtitle split instead of a second copy of the same logic. Two real bugs surfaced while porting the reference's own test cases and fixed rather than carried forward:
  - `FullTrack.TrackNumber`/`DiscNumber` are non-nullable `int` on the current SDK, defaulting to 0 for a track it could not fully populate. The reference wrote that 0 straight into the tag; Spotify numbers both from 1, so 0 now maps to `null` instead of a literal (and wrong) zeroth track.
  - `DateTime.TryParse` rejects a bare four-digit year outright, silently dropping the release year for every album whose Spotify precision is year-only rather than a full date — the same call the reference made, with the same bug. Now parses "1987", "2010-10" and "2010-10-10" alike.
- **`SpotifyMetadataProvider` is the read half only** of the reference's `SpotifyAPI.UpdateTrack` — the retry-with-delay, fall-back-to-Last.fm-on-failure and reopen-the-auth-dialog orchestration is deliberately not ported here, because it belongs with whatever in the pipeline actually calls this, and nothing does yet. `RecordingSession` does not consume it: wiring a provider selection needs the settings screen (Phase 5) and the shell (Phase 6) that do not exist. The title-match guard against `RecordingSession`'s independently-racing detection *is* kept, reproduced from `IsPlaybackTrackDetectedTrack`.

**Reference test files still outstanding, and where each lands:** `SpotifyAPITests`' 9 cases are superseded by `SpotifyTrackMapperTests` (18 cases — the extra coverage is the two bugs above, plus `ChooseCoverUrl` edge cases the original never exercised).

### Phase 5 — Settings (1–2 days)
- JSON schema (grouped sections), atomic writes, validation, `schemaVersion` handling.
- First-run defaults; DPAPI for the client secret.
- No importer — §6.

**Exit:** regression suite 4 passes; a fresh profile round-trips and a corrupted `settings.json` fails with a clear message rather than a crash.

### Phase 6 — WPF shell (8–10 days) ← the largest phase
- App host, DI, navigation, Fluent theme, dark mode.
- **Record tab:** status, now-playing, elapsed, console log (with filter + copy), start/stop.
- **Settings tab:** output path, device, quality, min length, format (incl. FLAC/AAC), metadata provider.
- **Advanced tab:** tray, timer, counter, filename template with `?` reference **and live preview**, existing-file policy, detection options, tag options.
- Inline validation via `INotifyDataErrorInfo` instead of modal dialogs.
- i18n from `Offstream.App/Resources/Strings.resx` with an en/fr key-parity test (the reference tree's translation test, re-namespaced). **Resource keys are re-keyed for Offstream** — this is the file where inherited naming would otherwise survive longest.
- Tray icon, minimise behaviour, single-instance guard.

**Exit:** FlaUI suite covers every control; key-parity test passes; side-by-side behavioural review against the reference app (behaviour compared, chrome and wording deliberately not copied).

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
