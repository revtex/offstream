# Offstream — Modernization Plan (.NET 10 + WPF)

**Offstream** is the successor to **Spytify**; the two names appear throughout this
document and are not interchangeable — "Spytify" always means the app being retired.

**Target:** move the app from .NET Framework 4.6.1 / WinForms to **.NET 10 (LTS) with a WPF + Fluent UI**, delegating **all audio conversion to ffmpeg**, while keeping every current capability and the existing three-tab layout.

**Shape of the work:** this is a *retarget and UI replacement*, not a rewrite. The audio stack, the undocumented COM routing, the domain logic and the 293-test suite all carry over. That is what makes it tractable.

---

## 1. Alternatives considered

A Go + Wails rewrite was scoped first (see git history for `docs/REWRITE-GO-WAILS.md`). It was rejected, and the reason matters for understanding this plan.

The app's hardest component is `Router/AudioPolicyConfigFactory*` — per-application audio routing through the **undocumented `IAudioPolicyConfig` COM interface**, whose vtable differs between Windows builds. The codebase already carries two implementations (`...ImplFor21H2`, `...ImplForDownlevel`). Reimplementing that in Go means hand-writing COM vtables with no header support and no compiler help, with a real chance of failure; the Go plan needed a 3–5 day de-risking phase and a documented fallback in case it proved impractical.

Staying on .NET removes that risk entirely — the working implementation is kept as-is. The same applies to WASAPI capture, session control, and process interop.

| | Go + Wails | **.NET 10 + WPF** |
| --- | --- | --- |
| Undocumented COM routing | Rewrite, may fail | **Kept verbatim** |
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
| TFM | `net10.0-windows` | Windows-only app; unlocks WinRT projections |
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

### 2.2 Constraints to respect

- **Do not enable NativeAOT.** The app depends on built-in COM interop (`ComImport`, `Marshal.GetDelegateForFunctionPointer`) for audio routing; AOT does not support it.
- **Do not enable aggressive trimming.** WPF's trimming support is limited, and reflection-driven settings/localisation will break. Publish **self-contained, untrimmed** (or `PartialTrim` only after measuring).
- **`System.Configuration.ConfigurationManager`** is a NuGet package on modern .NET, and the `Settings.settings` designer model is legacy. §6 replaces it outright rather than carrying it forward.

---

## 3. Architecture

Split the monolith into a testable core plus a thin UI.

```
Offstream.sln
├── src/Offstream.Core/           net10.0-windows  — no UI references
│   ├── Audio/                  capture, devices, sessions, ring buffer
│   ├── Encoding/               ffmpeg profiles, runner, probing
│   ├── Metadata/               Spotify/Last.fm providers, tag mapping
│   ├── Naming/                 filename template engine, path assembly
│   ├── Recording/              watcher state machine, recorder
│   ├── Settings/               model, persistence, migration
│   ├── Spotify/                process/title polling, SMTC
│   └── Interop/                CsWin32 + COM (routing, power, processes)
├── src/Offstream.App/            net10.0-windows  — WPF, MVVM only
│   ├── Views/                  XAML
│   ├── ViewModels/             CommunityToolkit.Mvvm
│   ├── Services/               dialogs, tray, navigation
│   └── Resources/              themes, i18n .resx
├── tests/Offstream.Core.Tests/   xUnit — the ported 293 + new
├── tests/Offstream.UI.Tests/     FlaUI end-to-end
└── tools/FakeSpotify/          test harness (ported)
```

**Rule:** `Offstream.Core` must not reference WPF or `System.Windows`. Today `IFrmEspionSpotify` is passed into `Watcher` and `Recorder` so they can write to the console pane; replace that with an event/`IProgress<T>` abstraction so the core is UI-agnostic and testable without a form mock.

### 3.1 Recording pipeline (unchanged in shape)

```
WASAPI loopback ─► ring buffer ─► temp .wav ─► ffmpeg ─► final file (+ tags + cover art)
       │                              │
   AudioThrottler              per-track Recorder
```

The existing design — one capture stream feeding a lock-guarded circular buffer, with per-track recorders draining slices and `SilenceAnalyzer` trim-start/trim-end semantics — is sound. Keep it. Modernise the plumbing only: `CancellationToken` throughout (already partly there), `Channel<T>` for the encode queue, `IAsyncEnumerable` where it reads better.

---

## 4. Asset disposition

| Component | Disposition |
| --- | --- |
| `Router/*` (IAudioPolicyConfig, both OS variants) | **Keep verbatim** — highest-value asset |
| `AudioSessions/*` (capture, throttler, circular buffer, MM devices) | Keep; retarget NAudio 2.x |
| `Native/NativeMethods.cs` | Replace with CsWin32-generated P/Invoke |
| `Native/ProcessManager.cs`, `FileManager.cs` | Keep |
| `Spotify/*` (process, status, title parsing) | Keep; add SMTC as primary source (§5.3) |
| `Models/*` (Track, UserSettings, FileNameTemplate, OutputFile) | Keep |
| `Naming` / template engine | Keep verbatim — recently written and fully tested |
| `API/*` (Last.fm, Spotify, MapperID3) | Keep logic; upgrade SDKs (§8) |
| `Recorder.cs` encode paths | **Replace** — ffmpeg owns all conversion |
| `MapperID3` (TagLib#) | **Mostly replace** — ffmpeg writes tags; see §5.2 |
| `frmEspionSpotify.*` (1,200 lines + designer) | **Replace** with XAML + ViewModels |
| `Properties/Settings.*`, `App.config` | **Replace** with JSON + migration (§6) |
| `EspionSpotify.Updater` | Replace (§10, Phase 8) |
| `EspionSpotify.Tests` (293 tests) | **Keep** — the safety net for the whole port |
| `EspionSpotify.FakeSpotify` | Keep, retarget |

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

Two decisions before Phase 3:

1. **Bundle ffmpeg, or resolve at runtime?** Bundling an **LGPL-only build** (libmp3lame, libopus, libvorbis, libflac; no GPL components) is ~35 MB stripped, gives zero-setup operation, and obliges shipping ffmpeg's licence plus a written source offer. Runtime resolution (app dir → PATH → configured) keeps the installer small but adds a setup step. **Recommendation: bundle, with runtime override.** A polished app should not ask users to install a codec toolchain.
2. **Pin and assert the version** at startup; log it in diagnostics. Encoder flags drift across major versions.

### 5.2 Tagging and cover art — two traps

Both were hit while adding Opus support to the current app; both must be covered by tests, not assumptions.

- **Ogg/Opus stores tags at the _stream_ level**, not container level. Verification must use `ffprobe -show_entries stream_tags`. Using `-show_format` shows nothing and produces a false "tags are missing" conclusion.
- **Cover art** for MP3 is a second input stream (`-i cover.jpg -map 0:a -map 1:v -c:v mjpeg -disposition:v attached_pic`). ffmpeg's `METADATA_BLOCK_PICTURE` support for Ogg/Opus is weaker. TagLib# **does** write Opus cover art correctly (verified against `TagLib.Ogg.File`). **Plan:** ffmpeg writes all textual tags; if per-format ffprobe tests show cover art failing for a container, retain TagLib# for that container only.

Full tag set (parity with `MapperID3`): title, subtitle, album, album artist, performers, genres, track number, disc number, year, front-cover picture.

### 5.3 Process discipline

Use `ProcessStartInfo.ArgumentList` — **available on modern .NET, unlike .NET Framework 4.6.1**. This eliminates by construction the argument-injection class of bug the current code needed hand-written `CommandLineToArgvW` escaping to avoid. Track metadata comes from Spotify window titles and is untrusted, so this matters.

Always: a `CancellationToken` with deadline, `RedirectStandardError` drained *before* waiting (a full stderr pipe deadlocks), and an exit-code check.

---

## 6. Settings and migration

Replace `Settings.settings` / `user.config` with `%APPDATA%\Offstream\settings.json`, bound through `Microsoft.Extensions.Configuration`, with a `schemaVersion` field and atomic writes.

**Migration is mandatory.** Existing users have `user.config` under `%LOCALAPPDATA%\Spytify\` holding their output path, template, and API credentials. On first run: locate the newest versioned directory, map all 23 keys, write `settings.json`, back up the original, log what was migrated, and **never delete** the old file.

Key mapping is 1:1 for most settings (`settings_output_path` → `output.path`, `advanced_file_name_template` → `output.template`, and so on). Two need care:

- `app_console_logs` is **dropped** — logs move to a rotating Serilog file. Storing unbounded console text in a settings string is fragile.
- `app_spotify_api_client_secret` is currently **plaintext**. Migrate it into DPAPI (`ProtectedData`, `CurrentUser` scope) and never write it back in the clear.

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
Three tabs (Spy / Settings / Advanced) · console log pane · minimise to tray · en/fr localisation · VB-CABLE installer · Spotify API credentials dialog · FAQ links · **auto-update (new)** · **local-only crash diagnostics (new, never auto-sent)** · analytics stays removed.

### Filename template — port verbatim
Tokens `{artist} {title} {album} {album_artist} {year} {track} {disc} {count} {date} {time}`, format specs (`{track:00}`, `{count:0000}`, `{date:yyyy-MM-dd}`), backslash for folders, empty-token collapsing with orphaned-separator cleanup, invalid characters stripped rather than substituted. **.NET format strings are unchanged**, so unlike the Go plan there is no date-layout translation problem.

---

## 8. Dependency upgrades

| Package | Current | Target | Work |
| --- | --- | --- | --- |
| NAudio | 1.10.0 | 2.2.x | Namespace/API shifts; moderate |
| NAudio.Lame | 1.1.6 | **removed** | ffmpeg replaces it; drop `libmp3lame.*.dll` |
| SpotifyAPI.Web | 5.1.1 | 7.x | **Breaking** — client construction and auth flow differ. Move to PKCE with loopback redirect |
| TagLibSharp | 2.2.0 | 2.3.x | Retained only if needed for Ogg cover art (§5.2) |
| System.IO.Abstractions | 13.2.8 | current | Minor |
| Newtonsoft.Json | 13.0.1 | **System.Text.Json** | Source-generated contexts |
| EmbedIO / Unosquare.Swan | 2.9.2 | **removed** | Was the OAuth loopback listener; use `HttpListener` or the SDK's built-in |
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

1. **Port the existing 293** unchanged. Any failure is a retarget defect, not a design change.
2. **ffmpeg argv golden tests** — exact argument arrays per format × bitrate × metadata. Catches flag drift without invoking ffmpeg.
3. **Encode integration** — per format: generate a `sine` via ffmpeg, encode, then assert codec, container, sample rate, channels, duration and **all tags** via ffprobe (`stream_tags` for Ogg). This is the test that catches the §5.2 traps.
4. **Settings migration** — fixture `user.config` files (fresh, underscore user, grouped-folders user, Spotify-API user) → expected `settings.json`, including DPAPI round-trip.
5. **Path edge cases** — 260-char budgeting, UNC paths, reserved names (`CON`, `NUL`), invalid characters, empty rendering, long-path opt-in.
6. **ViewModel tests** — validation, command enablement, template live preview.

### 9.3 Discipline

- **No network in unit tests.** The current suite has one test that calls the live Last.fm API and fails offline; convert it to a fixture.
- **CI on `windows-latest`**: build, `dotnet format --verify-no-changes`, analyzers as errors, `dotnet test` with coverage, ffmpeg pinned to the bundled version.
- **Coverage gate ≥ 75%** on `Offstream.Core`, excluding `Interop` (hardware-dependent — covered by the manual checklist).
- **Golden updates require an explicit flag** so diffs are always reviewed.

---

## 10. Phases

Each phase has an exit criterion; do not start the next until it is met.

### Phase 0 — Retarget spike (2–3 days)
Prove the risky parts survive the move before restructuring anything.
- New scratch `net10.0-windows` project referencing the existing `Router/`, `AudioSessions/`, `Native/` sources.
- Verify: WASAPI loopback capture works under NAudio 2.x; `IAudioPolicyConfig` routing works on **Win10 22H2 and Win11 23H2+**; session mute/volume works.
- Confirm .NET 10 is current LTS; confirm WPF-UI's .NET 10 support.
- Decide ffmpeg bundling and licence posture.

**Exit:** a console app captures 30 s of audio, routes Spotify to a chosen endpoint, and mutes a session — on both OS versions. Written decision record for each.

### Phase 1 — Solution restructure (3–4 days)
- Convert to SDK-style projects; `packages.config` → `PackageReference`; delete the `<Compile Include>` lists.
- Split `Offstream.Core` / `Offstream.App` / tests per §3.
- CI pipeline; analyzers; `dotnet format`.
- Serilog with rotating file + in-memory console sink.

**Exit:** solution builds on .NET 10, CI green, existing tests compile.

### Phase 2 — Core green (4–5 days)
- Port domain and audio code into `Offstream.Core`; remove all UI coupling (`IFrmEspionSpotify` → events/`IProgress<T>`).
- **Get all 293 tests passing.**
- Replace `NativeMethods` with CsWin32.

**Exit:** 293/293 green on .NET 10; `Offstream.Core` has no `System.Windows` reference.

### Phase 3 — ffmpeg encoding (3–4 days)
- ffmpeg resolution (bundled → PATH → configured) and version assertion.
- Format profiles, `ArgumentList`-based runner, worker queue, deadlines, stderr capture.
- Tags and cover art per format; delete NAudio.Lame and the LAME DLLs.

**Exit:** regression suites 2 and 3 pass for MP3, WAV, Opus, FLAC, AAC including cover art.

### Phase 4 — Dependency modernisation (4–5 days)
- SpotifyAPI.Web 7.x with PKCE + loopback redirect; drop EmbedIO.
- `HttpClient`/`IHttpClientFactory`; `System.Text.Json`; `System.IO.Compression`.
- DI container wiring; options pattern.

**Exit:** contract tests pass; manual Spotify auth verified against a real app registration.

### Phase 5 — Settings and migration (2–3 days)
- JSON schema, atomic writes, validation.
- `user.config` migration reader; DPAPI for the client secret.

**Exit:** regression suite 4 passes; a real pre-migration profile upgrades with identical output naming.

### Phase 6 — WPF shell (8–10 days) ← the largest phase
- App host, DI, navigation, Fluent theme, dark mode.
- **Spy tab:** status, now-playing, elapsed, console log (with filter + copy), start/stop.
- **Settings tab:** output path, device, quality, min length, format (incl. FLAC/AAC), metadata provider.
- **Advanced tab:** tray, timer, counter, filename template with `?` reference **and live preview**, existing-file policy, spy options, tag options.
- Inline validation via `INotifyDataErrorInfo` instead of modal dialogs.
- i18n from `.resx` with an en/fr key-parity test (port the existing `TranslationTests`).
- Tray icon, minimise behaviour, single-instance guard.

**Exit:** FlaUI suite covers every control; key-parity test passes; side-by-side visual review against the current app.

### Phase 7 — Windows integration polish (3–4 days)
- SMTC (`Windows.Media.Control`) as primary track source with title polling as fallback — works when Spotify has no visible window.
- Device hot-plug handling.
- Long-path (`\\?\`) support.
- VB-CABLE detection and elevated installer invocation.

**Exit:** verified on a clean VM; track detection survives Spotify minimised to tray.

### Phase 8 — Packaging and release (4–5 days)
- WiX v4 (or Inno Setup) per-user installer; app itself requires no admin.
- **Code signing** — start certificate procurement in Phase 0; unsigned binaries trip SmartScreen and undercut "professional".
- Bundled ffmpeg with licence and source offer.
- Auto-update: signed manifest, background download, signature verification, apply on restart.
- Tag-driven GitHub Actions release pipeline.

**Exit:** clean-VM install → record → update → uninstall, no leftovers.

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
6. **DPAPI-protected credentials** — plaintext today.
7. **Structured rotating logs** — replaces console text stuffed into a settings string.
8. **Device hot-plug handling.**
9. **Self-contained publish** — no .NET Framework 4.6.1 dependency (out of support since April 2022).
10. **Signed installer and auto-update.**
11. **Testable UI** — ViewModels under test; the current form has zero coverage.

Explicitly **not** changing: the three-tab layout, the console-log metaphor, the recording model, or the filename template syntax. Users should recognise the app.

---

## 12. Risks

| Risk | Impact | Likelihood | Mitigation |
| --- | --- | --- | --- |
| Routing COM breaks under .NET 10 | High | **Low** | Phase 0 spike; built-in COM interop is supported (just not under AOT) |
| NAudio 1.10 → 2.x behavioural drift in capture | Medium | Medium | Existing tests + Phase 0 capture spike |
| SpotifyAPI.Web 5→7 auth rework | Medium | High | Isolated behind the provider interface |
| WPF rewrite scope creep | Medium | High | §11 is closed; parity matrix is the gate |
| ffmpeg cover art unreliable for Ogg | Medium | Medium | Per-format ffprobe tests; TagLib# fallback for that container |
| Trimming/AOT breaking COM | High | Low | Explicitly disabled; documented in §2.2 |
| Code-signing cost and lead time | Medium | High | Begin procurement during Phase 0 |
| ffmpeg licence obligations | Medium | Low | LGPL-only build; ship licence + source offer |

---

## 13. Open questions

1. .NET 10 confirmed as current LTS, and WPF-UI's support for it?
2. Bundle ffmpeg (recommended) or resolve at runtime?
3. Signing certificate: EV (immediate SmartScreen reputation, hardware token) or OV (cheaper, reputation accrues)?
4. Auto-update wanted at all, given the upstream updater was deliberately removed from this fork?
5. Keep French and add more languages, or English-only for v1?
6. Is a Windows 10 floor acceptable, or must Windows 11 features stay optional?
7. Should FLAC/AAC ship in v1, or wait until parity is proven in the field?
