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
| `EspionSpotify/Router/*` (IAudioPolicyConfig, both OS variants) | `Core/Interop/Routing/*` — **behaviour kept, marshalling rewritten**. The .NET Framework WinRT marshalling does not exist on .NET 10; see DR-0001 and `Core/Interop/Routing/` for the proven replacement |
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
| `EspionSpotify.Updater` | **Nothing** — question 4 (2026-08-14) dropped self-update from v1; the release page is how a new version is found |
| `EspionSpotify.Tests` (293 tests) | `tests/Offstream.Core.Tests` — **assertions kept**, namespaces and fixtures renamed; the safety net for the whole port |
| `EspionSpotify.FakeSpotify` | `tools/Offstream.FakeSpotify` — retargeted |

---

## 5. The ffmpeg boundary

All conversion goes through ffmpeg. Capture writes raw PCM WAV to a temp file; ffmpeg produces the final artefact.

### 5.1 Format profiles (data, not code)

| Format | Args | Notes |
| --- | --- | --- |
| MP3 | `-c:a libmp3lame -b:a {rate}k [-abr 1]` | ABR by default, CBR on request — see the 2026-09-02 finding; true VBR (`-q:a`) can be exposed later |
| WAV | `-c:a pcm_s16le` | Never a stream copy — the temp is float32, see the 2026-09-02 finding |
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

**This boundary is about conversion, and the Metadata page (2026-08-29) is not conversion.** Retagging a file that already exists cannot go through ffmpeg without remuxing the whole thing to change one string — rewriting audio that had nothing wrong with it. So that path is TagLib# end to end: `Metadata/Library/TagLibTagStore` reads and writes every field, text and picture together, in one session, for every container. "ffmpeg writes all textual tags" stays exactly true of the recording pipeline and was never a statement about files already on disk.

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
WASAPI loopback capture · device selection · device volume · per-track splitting · minimum recorded length · recording timer (hhmmss) · silence trim start/end · ~~mute ads~~ (**dropped 2026-08-28** — see below) · record everything (podcast) · record ads · skip/overwrite/duplicate existing · force Spotify to skip recorded track · listen to playback on default device · prevent sleep while recording.

### Output
MP3 128/160/256/320 · WAV · Opus · **FLAC (new)** · **AAC (new)** · filename template with tokens and folder support · counter with padding · output path · 260-char budgeting (**plus long-path support, new**).

### Metadata
Last.fm provider · Spotify Web API provider · None provider · cover art · counter as track number · extra title → subtitle · re-tag already-recorded tracks.

### Shell / UX
Tabs (**Record / Settings / Advanced**, plus **Logs** from 2026-08-14 and **Metadata** from 2026-08-29) · console log pane · minimise to tray · en/fr localisation · VB-CABLE **detection** (detection is the whole of it — question 9, 2026-08-14) · Spotify API credentials dialog · FAQ links · **third-party notices (new)** · **local-only crash diagnostics (new, never auto-sent)** · analytics stays removed.

> The tab *structure* carries over because it works. The **tab labels do not** — the predecessor's "Spy" tab is Offstream's **Record** tab, and its "spy options" are **detection options**. §0 applies to user-visible strings as much as to code.
>

### Finding: "mute ads" is dropped, not deferred (2026-08-28)

The setting shipped as **Mute advertisements**, on by default, and never muted anything: `AudioSessions.SetMute` had no callers. Its only effect was one `!` in `RecordingPolicy.IsRecordUnknownActive`, where it silently vetoed "record everything" — so the option could be turned on and would do nothing, with no indication why. The predecessor guarded against exactly that by unchecking mute-ads whenever record-everything was checked (`frmEspionSpotify.cs:732`); Offstream ported the policy expression and not the interlock.

Restoring it was considered and rejected. There is no single behaviour to restore: the predecessor's mute-ads muted **every other application** and held Spotify at full volume (`MainAudioSession.cs:185-195`), to catch video adverts playing through a separate audio session. It never silenced the advert itself, so its own label was wrong too. Reimplementing that would reach outside Spotify to mute unrelated applications — a side effect no one asked a recorder for.

So the three switches collapse into one choice, `recording.recordSelection`, with the three values they could actually express between them: `KnownTracksOnly` (the default), `EverythingExceptAds`, `Everything`. Of the four combinations the booleans allowed, two were behaviourally identical — which is the arithmetic behind the confusion, and the reason relabelling them was not enough. Session mute stays proven (§ Phase 0) and `AudioSessions.SetMute` stays in Core; nothing in the product depends on it.

The same reduction then applied a second time, on the user's prompt, to the existing-file policy. `output.skipAlreadyRecordedTracks` was a boolean beside a three-valued dropdown that did nothing under two of its three values — six combinations, four outcomes — so it is now a fourth policy, `ExistingFilePolicy.SkipAndMoveOn`. Two notes for anyone extending that enum. `Skip` keeps its name and its zero value because the member names are what `settings.json` stores and `JsonStringEnumConverter` throws on a value it does not recognise, which `SettingsStore.Load` turns into a hard "fix or delete the file"; a rename would fail every existing install to gain a tidier identifier. And the four call sites that asked `== Skip` — one of them negated, in `TrackRecorder.AlreadyOnDisk` — now ask `RecordingSettings.KeepsTheExistingFile` instead: a policy forgotten at a comparison like that does not fail the build or a test, it silently records over a file the user asked to keep.

### Finding: the Advanced page cannot be measured by a test (2026-08-29)

The bottom of that page has now been clipped three times, each caught by a screenshot rather than by the build, because nothing above it scrolls and nothing in the build knows how tall it is. A measuring test was attempted: construct `AdvancedPage` on an STA thread with the three dictionaries `App.xaml` merges, `Measure` at the shell's content width, assert `DesiredSize.Height`. It measures correctly, and the numbers are worth keeping even though the test is not: at the shell's content width of 976, the Advanced page needs **495** units against the Settings page's **536**. Those were measured on 2026-08-29 and are carried forward, not re-taken. Two edits later that day left them standing: the existing-file rework turned a row holding a dropdown and a switch into the same row holding only the dropdown, and the template presets went from two buttons to five in the row that already held them. The presets are the one loose end — they sit in a `WrapPanel`, so at the window's narrowest they may take a second line, which is about **36** units and puts the page at roughly **531**. Still under the Settings page's 536, which is the comparison the fit rests on, but the margin is now that difference rather than forty units. Any edit that adds a row outright invalidates the figure, and there is no test to say so. Settings has never been reported as clipped, so Advanced now fits wherever Settings does — which is the closest thing to a proof available without a shown window. The test itself **cannot be kept**, and the reason is not the STA thread.

`StaticResource` resolves at load time, so the dictionaries must be in place before the page's constructor runs, which means an `Application`. `Application.Current` is a process-wide singleton bound to the thread that created it, and the test's STA thread ends with the test — leaving every later test in the assembly marshalling onto a dead dispatcher. Nine `RecordViewModelTests` failed on the first run for exactly that reason. Measuring the shell instead does not work either: an unshown window arranges to nothing, so the host `ContentControl` reports zero.

This is the empirical form of the note already on `AppServicesTests` ("asserts what is registered, never what resolves"). The page's height stays a hand-checked property until either the suite gains an assembly-scoped STA fixture that owns the `Application` for its lifetime, or the page gains a `ScrollViewer` and stops needing to be measured at all.
> The log pane became a fourth tab on 2026-08-14 and retagging a fifth on 2026-08-29; see the Phase 7 findings and §11. The predecessor's structure is a starting point, not a ceiling.

### Filename template — behaviour preserved exactly
Tokens `{artist} {title} {album} {album_artist} {year} {track} {disc} {count} {date} {time}`, format specs (`{track:00}`, `{count:0000}`, `{date:yyyy-MM-dd}`), backslash for folders, empty-token collapsing with orphaned-separator cleanup, invalid characters stripped rather than substituted. **.NET format strings are unchanged**, so unlike the Go plan there is no date-layout translation problem. The engine's *syntax* is user-facing and stays byte-identical; its *implementation* lives in `Offstream.Core.Naming` under Offstream names.

---

## 8. Dependency upgrades

| Package | Current | Target | Work |
| --- | --- | --- | --- |
| NAudio | 1.10.0 | 2.2.x | Namespace/API shifts; moderate |
| NAudio.Lame | 1.1.6 | **removed** | ✅ ffmpeg replaces it; never added here, and no `libmp3lame.*.dll` was ever copied in |
| SpotifyAPI.Web | 5.1.1 | **7.4.2** | ✅ **Breaking, done** — `SpotifyClient`/`SpotifyWebAPI` replaced; PKCE with a hand-rolled loopback listener (§10 Phase 4) |
| TagLibSharp | 2.2.0 | **2.3.0** | ✅ retained — Ogg/Opus cover art on the recording path (§5.2), and every tag on the Metadata page, which retags finished files and so cannot use ffmpeg |
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

> **The spike project was deleted on 2026-08-14** (`spike/Offstream.Spike`), after phases 0–7. It had been scratch scaffolding from the start, and everything it proved now lives in `Offstream.Core` with tests of its own; what it retained afterwards was a hand-run acceptance harness for the one thing a build agent cannot exercise — routing against a real audio endpoint. That is now a manual check with no code in the tree, so a routing change is verified by running the app on a machine with two render endpoints. DR-0001 records the original findings and stands; its references to the spike are historical.

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
| ~~`Audio/WaveFormatExtensions` (MP3 limits)~~ | **Deleted 2026-09-02** — ported, never called, and ffmpeg makes the check unnecessary; see the encoding-profile finding |
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
- ✅ `System.Text.Json` — landed in Phase 5, where the app first writes JSON of its own (settings). ⬜ `System.IO.Compression` — no in-app use for it at all now that question 4 dropped the updater; the release zip is compressed by the build, not by the app.

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
- ✅ **Advanced tab:** tray *(setting; the icon itself is below)*, timer, counter, filename template with a token reference, **a live preview and five layout presets that show the names they would produce**, existing-file policy, recording options *(the "detection options" card, renamed on 2026-08-29 once it had stopped being only about detection)*, tag options.
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

#### Fourth follow-up: conformance to the Spotify Web API rules (2026-08-13)

The user set a written rule sheet for all Spotify Web API work — spec-derived endpoints, PKCE,
loopback-literal redirect, minimum scopes, secure tokens with refresh, 429 backoff honouring
`Retry-After`, no deprecated endpoints, per-status error handling, and the Developer Terms. An
audit of what Phase 4 shipped found most of it already conformant and **two real gaps**, both
invisible at compile time and both silent at runtime.

- **`SpotifyClientConfig.CreateDefault()` attaches no retry handler at all.** Verified by
  reflection, not assumed: `RetryHandler` is null. So a 429 threw `APITooManyRequestsException`,
  fell through `TrackEnricher`'s catch-all, and the track recorded untagged — rate limiting is the
  one API failure that is *supposed* to be recoverable, and it was the one being treated as fatal.
  Worse, the provider's own boundary-race retry (4 attempts, fixed 1s) sits *inside* that, so a
  throttled session would keep asking. `SpotifyRetryHandler` now honours `Retry-After` exactly for
  429 and applies exponential backoff (1/2/4/8s, capped) to a 429 with no usable header and to the
  5xx family. Nothing else is retried: a 404 asked five times is still a 404 and four wasted round
  trips.
- **`Retry-After` is obeyed, not negotiated.** Guessing shorter than Spotify asks is what gets an
  application throttled harder. The handler needs no timeout of its own because enrichment already
  runs under `TrackEnricher.DefaultDeadline` (20s) — a `Retry-After` longer than that cancels the
  lookup and the recording continues untagged, which is the correct trade. Header lookup is
  case-insensitive, because HTTP says the name is and the SDK hands over whatever casing the wire
  used; an ordinal match would silently miss and fall back to guessing.
- **Three scopes were requested and one was used.** `user-read-playback-state` and
  `user-read-recently-played` were asked for on the assumption a later feature would want them;
  nothing ever called `GetCurrentPlayback` or `GetRecentlyPlayed`. Offstream makes exactly two
  calls — `/me/player/currently-playing`, which needs `user-read-currently-playing`, and
  `/albums/{id}`, which needs no user scope. A scope requested ahead of its feature is a permission
  the user grants for nothing, on a screen where the extra lines look identical to the load-bearing
  one. `SpotifyAuthOptionsTests` pins the list so it cannot drift back.
  **Existing sign-ins keep working** — a stored refresh token carries the grant it was issued with,
  so narrowing what is *requested* costs nobody a re-authorisation; new sign-ins simply get the
  smaller grant.
- **A dead refresh token had no exit.** It dies when revoked from the account page, when the
  dashboard app is deleted, or when the granted scopes stop covering the request — none of which
  recover on their own. The 401 was logged like any other fault and retried on every subsequent
  track, while the Settings page went on claiming the account was connected. `AuthorizationExpired`
  now fires on exactly that status; the host clears the stored token, and because that runs through
  `SettingsDocument.Update` it raises `Changed`, which is what flips the Settings page back to its
  signed-out state and puts the sign-in button in front of the user. **Only a 401 does this** —
  treating a rate limit or an outage as an expired token would sign the user out over a transient
  fault, and there is a test for each.
- **Being throttled is logged as a warning, and that level is the feature.** The first cut logged
  it at `Debug`, which is invisible: the Record page's activity log shows Information and above by
  default, so the one condition a user most needs explained — files coming out untagged — was
  reported only to whoever thought to switch the filter to "All". 429 is now a `Warning` naming the
  wait Spotify asked for; a throttle that outlasts the retry budget gets a second, louder line
  saying the lookup was abandoned and that recording itself is unaffected. Transient 5xx stays at
  `Information` on purpose: it usually clears on the next attempt, and promoting it would make the
  Problems filter noisy enough to stop being read.
- **Quota and rate limit are different failures and read differently.** A 429 is Spotify throttling
  request rate; a 403 can mean the signed-in account is not on the dashboard app's allowlist *or*
  that the app has run past the user quota its mode allows. The body is the only thing that
  distinguishes them, so it is quoted rather than replaced.
- **The retry handler takes its logger the way it takes its delay.** `Log.Logger` is static, so
  asserting on these lines by reassigning it would leak into every test running beside it. Injecting
  `ILogger` keeps the log assertions honest and resolves per call, so production picks up whatever
  Serilog is configured with rather than whatever existed when the client was built.
- **The API's own error message is the user-facing one.** Spotify sends a reason in the error body
  and the SDK surfaces it as `Exception.Message`; that beats anything writable from a status code
  alone. These land in the Record page's activity log, so the wording is the user's answer rather
  than a stack trace. Every fault is still downgraded to "no metadata" — the status decides what
  the user is told to do, never whether the recording survives.
- **Attribution is on the Settings page, beside the provider that requires it**, not in an about
  box: it is visible at the moment the user turns Spotify on. The Developer Terms' caching clause
  was reviewed and **deliberately left alone at the user's direction (2026-08-13)** — writing tags
  and cover art permanently into recorded files is what the app is for, and that is the user's call
  to make, not a conformance defect to fix.
- **No deprecated endpoint is in use**, and none of the rule sheet's named ones (`/playlists/{id}/tracks`,
  the type-specific library endpoints) is reachable from anything Offstream does.

**906 tests green** (738 core + 168 UI), up from 864: 28 for the retry schedule and what it reports — asserted on the
intervals it *produces*, with the delay function injected so no test spends one — plus the provider's
error paths and the scope list.

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
> **Detection only, settled by open question 9 on 2026-08-14 — not a placeholder.** Detection is unencumbered: enumerate endpoints and match the device name, exactly as the predecessor's `ExistsInAudioEndPointDevices` does. When the cable is absent the user is linked to vb-audio.com; no vendor binary is ever redistributed, and no installer step ever invokes one. The attribution line ships either way, and does.

**Phase 7 findings (2026-08-13):**

- **The TFM raise was mechanical, and the routing warning was unfounded — verified rather than assumed.** `net10.0-windows10.0.22621.0`, one property. The plan flagged that CsWinRT projections change what the compiler generates around WinRT types and that `IAudioPolicyConfig` should be re-checked. It is unaffected *by construction*: the routing code names `Windows.Media.Internal.AudioPolicyConfig` only as a runtime-class **string** and otherwise uses `ComImport` and raw P/Invoke, so there is no WinRT type in the type system for the projections to claim. Confirmed at runtime anyway — still binds the 21H2 IID and completes a COM round-trip on build 26200.
- **22621, not the 22000 floor.** It is the oldest build still in support, so targeting lower gains nothing, and Windows 11 only (decided 2026-08-11) means it costs no supported user.
- **`build.ps1` held a second copy of the TFM** in its publish path and pointed at a directory that stopped existing the moment this changed. It reads the value from `Directory.Build.props` now. Worth remembering that the TFM appears in more places than the props file.
- **The TFM raise also stranded every build output under the old one, and nothing cleaned it up (found 2026-08-29).** `dotnet clean` only removes what the configuration and target framework you name would have produced, so the `net10.0-windows` folders left behind by the raise above were unreachable by every clean anyone ran - 485 MB of them, still there a fortnight later. Another 183 MB was `obj\scratch*` trees from builds that had redirected their output path to get around a locked DLL, which is what happens when the app is running while the solution rebuilds; quitting it is the fix, and redirecting the output just moves the lock and leaves the copy behind. All of it is git-ignored, so none of it ever showed up in `git status` and the working tree carried two thirds of a gigabyte nobody could see. `build.ps1 -Clean` deletes `bin\` and `obj\` outright now rather than delegating to `dotnet clean`. The general point is the one the finding above makes from the other direction: **changing the TFM invalidates paths that no longer appear anywhere in the build**, and the ones already written to disk have nothing left that knows their name.
- **SMTC is the primary source and the window title is the fallback, not the reverse.** The title is only readable while Spotify has a window — minimised to the tray it has none, which is the gap this closes. It is also *better* information: SMTC hands over separate artist, title and album fields, where the title is one string that has to be split on a separator that can legitimately appear inside either half. Confirmed live: the media session reported an album (`… (Expanded Edition)`) that no window title carries.
- **"Prefers" means "answers", nothing cleverer.** The preferred source wins whenever it returns a track at all — not by being newer or more detailed. An *idle* answer is still an answer and is not second-guessed; only a null, meaning "I cannot see Spotify", reaches the fallback. Anything more sophisticated means two detectors disagreeing mid-track and a recording whose tags change halfway through.
- **A failing SMTC is treated as a silent one.** It is a system service Offstream does not control, so a fault costs the better metadata and never the recording — logged once per transition rather than per poll, since this runs several times a second. Cancellation is exempt: that is the session stopping, and it propagates.
- **Advertisements are detected on the same two rules as the title path** — the placeholder title, or playing with no artist attached — because Spotify announces them the same way to both. A paused session is never an ad: the placeholder lingers after playback stops, and treating it as one would suppress the next real track.
- **The WinRT call is kept free of decisions.** Everything decidable lives behind `ISmtcSessions` in pure code; `WindowsSmtcSessions` only talks to the system. The session manager is fetched once and kept — `RequestAsync` is a cross-process call and this is polled several times a second. Spotify is matched by a loose app-id substring, which covers both the desktop (`Spotify.exe`) and Store (`SpotifyAB.SpotifyMusic_…!Spotify`) identities.
- **The media session is also a way to talk *to* Spotify, and that turned "keep the one on disk" into a complete answer (2026-08-28).** Declining to record a track the user already has left Spotify playing it to nobody for three minutes; SMTC carries the same skip the keyboard's next-track key sends, so Offstream can move the queue on. `IPlaybackControl` is deliberately a second interface rather than a method on `ISmtcSessions` — reading what is playing and changing what plays are different privileges, and a session handed no implementation simply never skips. **The Web API was the wrong route** even though `POST /me/player/next` exists: it wants `user-modify-playback-state`, a signed-in account and a Premium subscription, and a third scope on a consent screen this project holds to two. Three things it had to get right, each a distinct failure: the ask is **once per track, marked before the call** rather than compared afterwards, because Spotify keeps reporting the outgoing track for a moment after it accepts — the same staleness that costs a new recording its first few hundred milliseconds — and a per-observation decision skips twice, the second one past a song nobody has recorded; it happens at **two checkpoints**, since the early existing-file check is the one the finding below shows cannot see an enriched path, so wired only there it would never fire for exactly the libraries organised well enough to want it; it is **armed only from the second track a session admits**, because pressing record is not an instruction to rearrange what is already playing — and because the media session's opening report is the *previous* track with the play state already true, so a session acting on the first thing it sees fires the command at the song the user has just started; and it **stops after fifty in a row**, because a fully-recorded queue on repeat has no other terminating condition, with the budget restored only by a recording actually reaching the library — a recording merely *starting* is not evidence, or a template that matches only after enrichment resets the cap on every track and never reaches it.
- **Long paths go through the `\\?\` prefix, because Offstream does not write the file — ffmpeg does.** A `longPathAware` manifest plus the machine's `LongPathsEnabled` switch opts *a process* in, and ffmpeg is a separate process with its own manifest and no interest in ours. The prefix travels with the path through `ArgumentList` into whatever ffmpeg hands to `CreateFile`. Verified rather than assumed, since the feature rests on it: ffmpeg 8.1 wrote an MP3 to a 298-character destination through a prefixed path, on a machine with the registry switch **off**.
- **The prefix disables normalisation, which is the trap.** Windows stops resolving `.` and `..`, stops converting `/` to `\`, and stops trimming trailing dots and spaces — a merely untidy path becomes one the filesystem rejects. So it is applied only to fully-qualified paths, normalised first, and only when the path is long enough to need it.
- **Extended paths raise the total length, never the component length.** The 260 budget divided across template levels became a 32767 budget dividing into per-level allowances of thousands of characters, which renders folder names NTFS refuses outright — trading "truncated at 260" for "cannot be written at all". The per-level allowance is clamped to 255. This is the part that looks like it goes away and does not.
- **One existing test asserted a deep output root was too long.** It is not any more, which is the point; it now asserts the opposite, with a companion case keeping the check honest by proving it still fires on a root with genuinely no room left.
- **Hot-plug reports and stops; it does not re-route.** Moving a running capture to another endpoint sounds helpful and is not: the replacement can have a different sample rate and channel count, so the file in progress would gain a seam, or a tail its header does not describe. Losing the endpoint ends that recording cleanly and says why; choosing a different device is the user's call and the next recording picks it up.
- **The two ways of losing an endpoint look different in the notifications.** A pinned device is lost when that exact id goes away. A capture following the *default* is lost when the default **changes** — the old device may still exist and still enumerate, but Windows has moved playback elsewhere. That is the more confusing failure because nothing was unplugged, and watching for removals would never catch it. Without any of this the loss is silent: WASAPI simply stops delivering and the session keeps believing it is recording.
- **Endpoint callbacks arrive on a system thread from inside the audio stack**, so the watcher raises an event and returns — blocking one is a documented way to wedge the endpoint enumerator for the whole process. `OnDefaultDeviceChanged` is filtered to render + multimedia, because Windows raises it per role and listening to all three turns one headphone swap into three identical notifications.
- **The two points above were true of the watcher and false of the app, until 2026-08-14.** Every part of the detection was built and tested, and `IAudioCaptureSource.Stopped` — the event carrying the loss out of the capture — had no subscriber anywhere, so none of it reached `RecordingSession`. Losing an endpoint mid-recording ended exactly as it did before the watcher existed: silently, with the session still believing it was recording. `RecordingSession` now subscribes, finishes the track in flight, and raises `Ended` so the controller releases the session and the page stops offering to stop it; the same `Ended` closes the identical gap on the recording-timer path, which had been stopping sessions the controller went on holding. **A detector with no consumer is worth nothing and looks finished** — the acceptance criteria asked whether the loss was detected, which it always was.
- **A handler that ends a capture cannot run on the notification thread.** `IMMNotificationClient` forbids blocking there, waiting on a synchronisation object, and releasing the last reference to an audio object; ending a capture does all three, against the endpoint whose disappearance caused the notification. The watcher's original note said only "raise an event and return", which is what it did — the blocking was in the handler, which did not exist yet. Notifications are now handed to a pool thread, so a change can arrive after the capture is disposed and both the capture and the watcher guard for that.
- **VB-CABLE is detection only, and that is a licence decision rather than an unfinished one.** The package is donationware whose readme forbids integrating it into another installation procedure without the author's agreement. The predecessor ships the vendor's setup executables in its own tree, launches them elevated, and displays neither the origin nor the donationware notice the licence asks for. Offstream carries no vendor binaries: it matches the driver's product name among the render endpoints, as the reference does, and points the user at vb-audio.com when it is absent. **Open question 9 answered 2026-08-14: this is the shipping form, and no other one is coming.**

- **The clean-VM pass is dropped from this phase (decided 2026-08-13).** It was written to catch the downlevel differences between Windows 10 and 11 — which OS versions the interop, the SMTC session manager and the endpoint notifications behave differently on. Windows 11 only (decided 2026-08-11) removes the question the pass existed to answer, and the development machine is the supported OS. Phase 8's clean-VM install → record → update → uninstall run stays: that one is about the installer leaving a machine clean, which is a different question and still open.
- **"Keep the one on disk" was doing the opposite of what it says, for every template that names an album.** The check ran the instant the track changed, before the metadata lookup had returned anything — so `{artist}\({year}) {album}\{track:00} {title}` rendered as `Artist\Title.mp3`, a path nothing is ever written to. It found nothing, every time, and the recording proceeded to a destination the rename then replaced without a word. The authoritative check moved into `TrackRecorder`, after enrichment and before the encode, where the destination is finally knowable; the early check stays as a shortcut for the simple templates it can still answer, and the rename has a last gate for two encodes of the same track racing. This is the failure mode to watch for elsewhere: a decision taken against a track that has not been enriched yet is a decision taken against different data than the one that writes the file.
- **The existing-file policy said nothing at all, whichever way it went.** Overwriting and adding a counter both look exactly like an ordinary save from the outside, which is what made the bug above invisible for as long as it was — a user has no way to tell a setting that is broken from one that is working silently. All three outcomes now name themselves and the file in the activity log.

- **The cable's notice sits beside the device picker, and says something either way.** It is not a mode or a feature to switch on — it is one more endpoint to record — so it belongs next to the choice it changes rather than in a section of its own. Both states are worth a line: a user who has installed it and cannot find it in the dropdown is told it is there, and a user without it is told what recording an ordinary output device actually captures, which is every sound the machine makes. Detection runs off the same list the dropdown was built from, so the notice and the options can never disagree, and it re-runs on the refresh button, since plugging the cable in with the page open is what that button is for. The link goes to vb-audio.com and that is the whole of the offer.
- **Switch rows read left to right again.** The label column had been pinned to a fixed width so the switch would stay near its label; the effect was a column of switches stranded in the middle of a wide card, with every label longer than the pin broken across two and three lines. Labels now take the room and the switches sit on the card's right margin, one line each, ellipsised with a tooltip below the width where that stops fitting. The recording timer's duration field moved to the *left* of its switch so that switch stays in the same line as every other one.


#### Record page and log placement (2026-08-14)

- **The activity log is a tab now, and that is the third place it has lived.** It was the whole Record page, then a fixed-height box on it, then a collapsed expander at the bottom of it — three answers to a question that has no answer at that scale, because a log wants either the whole surface or none of it and a shared page can offer neither. The tab gives it both on demand. `LogsPage` is a second view onto `RecordViewModel` rather than a ViewModel of its own: the lines, the filter and both buttons are already there and subscribed to the sink since startup, and a second ViewModel would mean a second subscription and two histories diverging by construction order.
- **The readiness chips are gone, along with `ReadinessProbe` and its fourteen resource strings.** They answered questions the page answers better by failing: a missing ffmpeg is already an `InfoBar` with a sentence in it, and a library folder is already unsaveable without one. Five green pills reporting that nothing is wrong is a row that is always there and never read, and it cost a constructor dependency on the audio-device catalog to produce.
- **`SavedCountText` was never notified, so the session total read "0 saved" forever.** `[ObservableProperty]` on `_savedCount` refreshed `HasSaved` and not the string derived from the same field — the count itself was correct the whole time. **This is the failure mode to look for in every computed property on this page:** a `[NotifyPropertyChangedFor]` that is missing produces a value that is right in the debugger and stale on screen, and nothing fails.
- **The LCD is backlit rather than reflective.** Pale grey ground, near-black segments — a calculator face, and the palest object in a nearly black window. Inverting it puts the panel a shade under the window and the segments near white, which is the transflective backlight every field recorder made this century actually has; contrast goes up rather than down. Two consequences that are easy to miss: the scanline brush had to flip from black to white, because a black line over a near-black ground is a line nobody can see, and the glass gradient had to flip too, since a top-dark gradient on a dark panel reads as a shadow rather than a diffuser. The meter's own drawing and its spectrum are untouched; only the ground it sits on moved.
- **The track name came off the display.** It was the one thing on the face that is neither a transport state nor a measurement — a proper noun of arbitrary length, ellipsised, sitting where an instrument prints a legend. It belongs in the **Currently recording** card underneath, beside the cover art and the album it goes with. The face is left with transport, format and the counter, which is what a panel like this is read for, and the counter gets the whole right-hand side and a printed `ELAPSED` legend: a bare `00:03:42` on a recorder could be elapsed, remaining, or a timestamp, and one small word settles it once instead of every glance.
- **Session totals moved onto the section heading they count, as `Count: n | Time: hh:mm:ss`.** They had been stacked in the now-playing strip, which put a running total in the middle of a card describing one track — two timescales in one block. The duration also stopped being prose (`4m 12s`): it sits inches from a fixed-width clock on the display, and two spellings of a duration on one screen make the reader convert between them to compare.
- **The FlaUI Desktop suite is deleted, not skipped.** The per-control page suites went on 2026-08-12 at the user's direction; the thirteen shell and Record-page tests that survived were pinned to exactly the layout this batch rewrote. CI's `--filter "Category!=Desktop"` and the `FlaUI.UIA3` package reference are gone with them. The automation ids stay on every control — they are a Phase 9 accessibility requirement in their own right, and they are what any future suite would need.


#### Genre, and where Spotify keeps it (2026-08-14)

- **Spotify has no genre on a track, at any endpoint.** It models genre as an attribute of the *artist*. `FullTrack` and `SimpleArtist` carry none; `FullAlbum` has the field but Spotify stopped populating it for most of the catalogue in late 2024, which `SpotifyTrackMapper` already documented. The net effect was that **every Spotify-tagged recording shipped with an empty genre tag** — the mapper faithfully copied an array that was always empty. `/v1/artists/{id}` is the one place the data still lives.
- **It costs no scope.** Artist data is public, so the shipped scope list stays exactly `user-read-currently-playing` and `SpotifyAuthOptionsTests` is untouched. Worth stating explicitly, because "add an endpoint" and "add a scope" usually travel together and here they do not.
- **The artist id is already in hand** — it is on the `SimpleArtist` the match guard just compared against — so this is one extra call and no extra lookup to find the subject of it.
- **Cached per session, keyed by artist id, including the misses.** An album is one artist repeated, so without a cache a fifteen-track album is fifteen identical requests against a rate limit shared with every other call the session makes. Caching an empty answer matters as much as caching a full one: an artist Spotify has no genres for has none on the next track either. Keyed by id rather than name because two artists genuinely share a name often enough to matter.
- **Capped at three, to match Last.fm.** Spotify lists up to a dozen for a well-known artist, shading from useful into hyper-specific. A library tagged from both providers should not have two different ideas of how long a genre tag is.
- **The honest limitation, and why there is a fallback at all.** Artist genres describe a body of work, not a recording — a ballad by a metal band is tagged metal, and a various-artists compilation is noise. Last.fm's top tags are *per track*, which is the question actually being asked. So the chain is **Spotify artist genres → Last.fm if a key is configured → empty** (user's call, 2026-08-14), and a user with both configured gets Spotify's albums with Last.fm's genres rather than having to choose between them.
- **The fallback runs over a throwaway copy of the track.** This is the load-bearing detail: the second provider is a *full* metadata provider whose mapper writes album, year, cover art and track number as readily as genres. Letting it near the real track would mix two catalogues' idea of one release into a single file — Spotify's album with Last.fm's artwork, or a year from a different pressing. Only `Genres` is read back off the copy.
- **It is a success-path step, into a gap only.** A provider that found nothing at all leaves a bare recording, and one genre on an otherwise untagged file is not worth a second request; a provider that did supply genres is not second-guessed. A fallback that throws is swallowed at `Debug` — everything else on the track is already correct by then, and losing an album because a genre lookup timed out would be the tail wagging the dog.
- **SMTC cannot help here, verified rather than assumed.** `GlobalSystemMediaTransportControlsSessionMediaProperties` does define a `Genres` field. Probed live against the running Spotify client, it comes back `count=0` while Title, Artist, AlbumTitle, AlbumArtist and TrackNumber are all populated. Reading it would be code that returns nothing.


#### The media session as a metadata floor (2026-08-14)

- **Precedence is API first, media session underneath** (user's call, 2026-08-14). Not the other way round, and not "client first with the API as fallback" — which sounds equivalent and is not. The two sources have asymmetric coverage: SMTC supplies title, artist, album, album artist and track number, and *never* supplies year, release date, genre, album track count, disc or copyright. The API is therefore the only source for half the tag set, so a literal client-first rule would still call it on every track and save nothing. What is worth having is the other direction: the provider wins wherever it answers, and what the client already knew fills the gaps it leaves.
- **The gap it closes is a real one, and it was silent.** When the match guard rejected all four attempts — or no provider was configured, or Spotify was down — the recording was written with nothing. The media session had already reported artist, title, album, album artist and position for that exact track, with more certainty than any lookup, and all of it was being discarded. Now it stands.
- **`SmtcSnapshot` gained `AlbumArtist` and `TrackNumber`**, both optional so the window-title path — which supplies neither — and every existing construction site keep working unchanged. A `TrackNumber` of 0 maps to null: Spotify numbers from 1, so zero means "not reported" rather than a zeroth track. A blank album artist maps to null rather than to an album credited to the empty string, which would otherwise beat a provider that did know.
- **The mappers fill; they no longer clear.** This is the load-bearing half, and it is a deliberate behaviour change in both `SpotifyTrackMapper` and `LastFmTrackMapper`: `Album`, `AlbumArtists` and `AlbumPosition` are only assigned when the provider actually has a value. The `SetArtistFromApi` / `SetTitleFromApi` idiom already worked this way for artist and title — this extends it to the three fields the media session can now supply.
- **One existing test asserted the opposite** and was updated rather than worked around: `ApplyAlbum_WithAnEmptyResponse` pinned that an empty album object wrote `""` and `[]` over whatever was there. That was harmless while the only other source was a window title, which carries no album at all. It stopped being harmless the moment the media session started carrying one, because a provider that could not answer would erase what the client had said for certain.
- **Cover art is deliberately not taken from the media session.** SMTC exposes a thumbnail, and it is much smaller than the 640×640 the Web API returns. As a last resort for a file that would otherwise have no art it would be defensible; as anything that could win over the API's image it is a downgrade users notice in a library. Left out until there is a reason to want it.
- **Genre is not part of this**, because the media session has none to give — probed live, Spotify returns `count=0` for it. See the genre findings above.

**Still to do in this phase:** nothing.

**Exit:** track detection survives Spotify minimised to tray; VB-CABLE presence is detected and its absence degrades to a documented link, with no vendor binaries in the repo — which question 9 made permanent on 2026-08-14 rather than provisional.

### Phase 8 — Packaging and release (4–5 days)

> **Four decisions taken 2026-08-14 fix this phase's shape** (open questions 3, 4 and 9, plus the installer technology the original text left as a choice). They are recorded in §13; the list below is what they leave.

- **Version from the tag, not from a file anyone edits.** `Directory.Build.props` has no `<Version>` today, and a tag-driven pipeline needs one that cannot drift from the tag that produced the build. Everything else in this phase depends on it.
- **Inno Setup per-user installer**, into `%LOCALAPPDATA%`, no admin prompt at any point — which matches an app that needs no admin to run. WiX/MSI was the alternative and buys policy-based enterprise deployment that nothing here asks for.
- **Bundled ffmpeg with its licence and a source offer.** An LGPL build (question 2, DR-0001), dropped into the `ffmpeg` subfolder `FFmpegLocator` already looks in. The source offer is a real obligation, not a line in `NOTICE`: the release has to carry or link source matching **the exact build shipped**, so the build's version and origin get pinned somewhere the release can point at.
- **No VB-CABLE payload, ever** (question 9 → detect only). The installer grows no driver step. The specific act its licence forbids is integrating the package into another installation procedure without the author's agreement, and the point of the answer is that Offstream does not go near that line.
- **Third-party notices reachable from inside the app**, not only as a file in the repo: ffmpeg (LGPL + source offer), TagLibSharp (LGPL-2.1-only), the predecessor's MIT notice, and the VB-CABLE origin and donationware attribution. `NOTICE` is the source of truth and ships with the build.
- **A signing step that is inert until a certificate exists** (question 3). It signs the executable and the installer when one is configured and skips silently when none is, so acquiring a certificate later is configuration rather than a pipeline change.
- **No update mechanism of any kind in v1** (question 4). Not an updater, and not an in-app version check either: the installer registers the releases page with Windows (`AppUpdatesURL`) and that page is where a new version is announced. An in-app check is a later version's problem, and adding one now would be a network call, a settings toggle and a pair of resource strings spent on something the release notes already say.
- **Tag-driven GitHub Actions release pipeline** — `v*` tag → publish → package → attach installer and portable zip to a GitHub release.

**Status (2026-08-15).** Built and green: the tag-driven pipeline, the per-user installer, the pinned ffmpeg with its source archive, the inert signing step, and the in-app notices. **Not done, and not doable from here:** the clean-VM install → record → uninstall pass. It needs a virtual machine and a person at it, and no amount of CI substitutes — the runner installs nothing and uninstalls nothing. Until that pass happens, the installer is *known to compile and known to produce an executable*, which is a weaker claim than the exit criterion makes.

**Exit:** clean-VM install → record → uninstall, no leftovers; third-party notices complete and accurate for whatever actually shipped. *(The original exit criterion said install → record → **update** → uninstall. Question 4 removed the update leg rather than reinterpreting it: there is nothing in the app to exercise. What replaces it is the installer's own upgrade path — installing a newer build over an older one and finding one entry in Apps & Features, not two — which is what a stable `AppId` buys and the only update behaviour v1 has.)*

**Phase 8 findings (2026-08-14 / 15):**

- **The tag is the only place a version is written down.** `Directory.Build.props` carries a `VersionPrefix` and a `VersionSuffix` of `dev`; a tagged build passes `-p:Version=1.2.3`, which overrides both. The alternative — a `<Version>` bumped in lockstep with the tag — is a file that eventually is not bumped, producing a build that claims a version nobody released. Worth knowing about the override: `-p:Version` beats *both* properties, so the `dev` suffix is dropped for tagged builds without a condition anywhere in the pipeline. Verified by building: no arguments gives `0.1.0-dev`, and `-p:Version=1.2.3-rc.1` gives `AssemblyVersion 1.2.3.0` with `InformationalVersion 1.2.3-rc.1+<sha>`.
- **Releasing is two steps, and deliberately not one.** The changelog section for the version is closed in an ordinary pull request; only then is the tag pushed. The workflow re-checks that the section exists **before it builds**, so the failure arrives in thirty seconds rather than after a full publish.
- **The release notes originally fell back to `[Unreleased]` when the version's section was missing.** That works exactly once and then republishes the previous release's entries forever. There is no fallback now — a missing section fails the run.
- **`$array -notmatch $pattern` is not `-not ($array -match $pattern)`.** It returns the elements that failed to match, which on any real changelog is a long, non-empty, entirely truthy array. The changelog check was written that way and would have thrown on every tag, including correct ones. Caught by extracting the script and running it both ways before merging, which is the only reason it was caught at all.
- **Only `ffmpeg.exe` is bundled, not `ffprobe.exe`.** `FFmpegLocator` resolves a probe path and one test asserts it; nothing at runtime ever runs it. Shipping just the encoder saves 108 MB. The build is pinned by digest in `build/windows/ffmpeg.json`, and `fetch-ffmpeg.ps1` **throws** on a fresh download whose digest does not match rather than updating the pin — the message says so, because the tempting fix is the wrong one.
- **The pinned build is genuinely LGPL, checked rather than assumed.** Its configure line has `--enable-version3` and no `--enable-gpl`, which makes it LGPL-3.0-or-later. `NOTICE` had implied LGPL-2.1 and claimed "no GPL build is bundled" at a time when nothing was bundled at all. Distributing an LGPL binary obliges distributing its source: every release **attaches** the source archive rather than offering to supply it, because a written offer has to outlive whatever was going to host it.
- **The installer is compiled in CI, not only at tag time.** A script that runs once per release is a script that breaks once per release. CI stages the existing publish output with a stand-in `ffmpeg/ffmpeg.exe` and compiles the real `.iss`.
- **Inno Setup 6.7.1 is preinstalled on `windows-latest`; the Windows SDK is not.** `iscc.exe` needs no install step; `signtool.exe` is not on `PATH` and has to be found under `Windows Kits\10\bin`. The signing script was exercised as far as this machine allows — it locates a certificate from the environment and refuses to proceed silently — but has **never signed anything**, because there is no SDK here and no certificate yet.
- **`PrivilegesRequired=lowest` moves `{autopf}` to `%LOCALAPPDATA%\Programs`,** which is what makes the per-user install admin-free. `AppMutex` is `Local\Offstream`, the same string as `OffstreamPaths.InstanceMutex`, so an install over a running copy asks rather than fails. `AppId` must never be regenerated: a new one turns an upgrade into a second, parallel installation.
- **Uninstall offers to remove `%APPDATA%\Offstream` and defaults to No,** and says plainly that recordings live elsewhere and are never touched. The one thing an uninstaller must not do is delete the output of the app.
- **The licence text is embedded in the executable rather than read from beside it.** Both obligations here — the predecessor's MIT notice, and the bundled LGPL ffmpeg — require the notice to travel with the software, and a loose file does not survive a zip unpacked selectively or a copy of just the `.exe`. `LICENSE` and `NOTICE` are `EmbeddedResource` items in `Offstream.App.csproj`, and a test asserts the shown text still contains both files' contents, so the window and the repository cannot drift.
- **Attribution and licensing are two different obligations and live in two different places.** Spotify's Developer Terms want content credited; that line belongs **beside the provider picker on Settings**, where it is obvious what it refers to, and it is empty when no provider is selected because crediting a service the app is not calling is a false statement. Licences belong in the notices window. Folding the first into the second would have satisfied neither well.
- **The notices entry point sits on the section header, and the first attempt was wrong.** A version line and a button under the ffmpeg path pushed both off the bottom of the window at its minimum size — the shell's `MinHeight` is a written promise that no setting is ever unreachable without scrolling, and this quietly broke it. Raising the floor was not available either: at 150% scaling the window is already 1050 physical pixels tall. The header line is the one place in a full card with room that costs nothing vertically. **Anything added to the Advanced page from here needs the same arithmetic done first.**
- **`System.IO` is imported explicitly in `Services/ThirdPartyNotices.cs`.** WPF compiles a throwaway project first to resolve XAML type references, and that pass does not inherit `ImplicitUsings` — so a type reached through them fails to compile there while compiling fine in the real assembly. The error names a `_wpftmp.csproj` that does not exist on disk.
- **The ffmpeg pin expires on its own, and the release is the only thing that notices (2026-08-31).** Cutting 0.2.0 failed at `Package`: the BtbN autobuild release the digest pins was gone, and the asset URL 404ed. Nothing had changed in the repository — BtbN deletes autobuild releases after about two weeks, so the pin rots on a timer whether or not anyone cuts a release. Two things make this invisible until a tag is pushed. CI runs the encode tests against **gyan.dev**, a different distributor with durable versioned URLs, and CI's installer job stages a stand-in `ffmpeg/ffmpeg.exe` on purpose, because it is testing the packaging layout and not a 146 MB download. So `fetch-ffmpeg.ps1` is the one step in the pipeline that only ever runs for real at tag time. `workflow_dispatch` with a `-dev` version builds everything and publishes nothing, and is the cheap way to find this before spending a version number on it. Re-pinning needs no download: GitHub's releases API carries a `digest` field per asset, so the sha256 can be read straight out of the API and the asset's short commit resolved against `FFmpeg/FFmpeg` for the source archive. Refresh to the newest autobuild **on the same ffmpeg branch** — reaching for a newer branch to fix a broken link is a toolchain change wearing the clothes of a 404.

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
10. **A real installer**, with signing wired and waiting on a certificate, and no self-update at all (questions 3 and 4, both settled 2026-08-14).
11. **Testable UI** — ViewModels under test; the predecessor's form has zero coverage.
12. **A settings layer with no inherited vocabulary** — grouped JSON schema, clean slate, no importer (§6).
13. **Retagging recordings already on disk** — the **Metadata** page (2026-08-29), added at the user's request. Both apps could only tag a file while recording it, so a recording made before a provider was configured, or from a window title the parser could not split, kept its thin tags forever.

Explicitly **not** changing: the console-log metaphor, the recording model, or the filename template syntax. The tab *structure* was on that list and no longer is — Logs made it four on 2026-08-14 and Metadata five on 2026-08-29 — but what the list was protecting holds: tabs across the top, one page each, no wizard and no ribbon. The app should feel familiar to anyone arriving from the predecessor — while carrying none of its names, its settings file, or its identifiers (§0).

### Finding: retagging a finished file is a second write path, and most of what it cost was invisible to the build (2026-08-29)

The **Metadata** page scans a folder, reads what tags each file already carries, looks the thin ones up, and writes the approved answers back. It lives in `Core/Metadata/Library/` plus two ViewModels and a page, and most of it was cheap because `IMetadataProvider` already had exactly the right shape — a provider enriches a `Track` in place, so the enriched `Track` *is* the suggested tags, and `LastFmMetadataProvider` works on this page **unmodified**: it looks up by artist and title, which is all a filename gives. What was not cheap is below.

**`SpotifyMetadataProvider` could not be reused, and the reason is not visible from its name.** It calls `/me/player/currently-playing` — it answers "what is playing right now", which is useless for a file at rest. `SpotifySearchMetadataProvider` is genuinely new code against `/search`, and it is the one provider here that has to *decline*: Spotify always returns something, and the top hit for a misparsed filename is routinely a different song. It requires the artist to agree before it accepts a match, because a confident wrong answer written into a file the user had correctly named is the only unrecoverable failure this page has. `/search` needs an access token and **no user scope**, so the two-scope list and `SpotifyAuthOptionsTests` are untouched.

**Which half of `Track` you seed decides whether Auto-Fetch does anything at all.** `Track`'s public setters write the *scraped* backing field and its getters read `_apiArtist ?? _artist` (`Track.cs`). So a file's existing tags are seeded through the ordinary setters — the local guess — and a provider's answer then wins, which is the behaviour wanted. Seeding the API side instead compiles, passes a naïve test, and makes Auto-Fetch silently hand back the tags the file already had. Nothing about it looks wrong when you read it.

**And the same precedence bites again from the other end, which is the bug this feature actually shipped with for a day.** A person correcting a wrong match writes through the ordinary setter, which lands *behind* the provider's answer — so the correction was accepted by the box, shown in the grid, and discarded at the point of writing. That is the exact failure the review step exists to prevent, and the first test written for it missed it: it scanned and then edited with no fetch in between, so `_apiTitle` was null and the getter fell through to the scraped field. It asserted the right thing in the wrong scenario. The row VM now writes **both** tiers on an edit (`LibraryTrackViewModel.OnTitleChanged`), and the test that pins it fetches first. The rule to carry forward is that `Track` has **three** notional tiers on this page — scanned, fetched, typed — over two backing fields, so anything writing a user's input into a `Track` has to say so on the API side or lose to whatever a provider said.

**TagLib# defaults to ID3v2.4 and this project ships v2.3**, so the store pins `Id3v2.Tag.DefaultVersion = 3` with `ForceDefaultVersion = true`. Without it a retag *upgrades* the tag, and the tags then vanish from Windows Explorer and Media Player on files that displayed correctly before Offstream touched them — worse than not tagging at all, and invisible to any assertion that reads the file back with TagLib# alone. The store also writes text and picture in **one** TagLib# session rather than calling `CoverArtWriter` after itself, which would save the file twice for one edit. `.wav` is excluded and *counted*: Offstream records it, so those files are in the folder, but WAV has no dependable tag container — listing them would show rows that look taggable and then fail on save, so the page says how many it passed over instead of hiding them.

**Three defects reached a running app, and the build saw none of them.** `MetadataPage.xaml` used `BooleanToVisibility` without declaring it; every other page declares its own converter in `UserControl.Resources` and there is no application-level one. The build was clean, `-VerifyFormat` was clean, and all 1,196 tests passed — the page threw `Cannot find resource named 'BooleanToVisibility'` the first time it was shown. A `StaticResource` is resolved at load time and nothing before that checks it, which is the same class of gap as the Advanced page's height (§7 finding): **launching the app is not optional verification for a new page.** The other two are below, and both needed a screenshot of a real library rather than a test.

**A `ScrollViewer` that permits horizontal scrolling measures its content at infinite width, and a star-sized column inside one is not a star-sized column.** The first layout was a seven-column table with `ScrollViewer.HorizontalScrollBarVisibility="Auto"` on the list. That does not mean "scroll if it overflows" — it means the row is measured with unbounded width, so the `*` column took the full unwrapped width of the longest file name in the folder and pushed everything right of it off the edge. `TextTrimming` never engaged, because a `TextBlock` given infinite width has nothing to trim to. One file called `01 Running Up That Hill (A Deal With God) - 2018 Remaster.mp3` reshaped the whole page. **The fix is `Disabled`, not a narrower column**, and it is the difference between trimming working and trimming being silently inert.

**And the page reported that every file in the library needed rewriting.** `LibraryTrack.HasChanges` ended with `Suggested.AlbumArtImage is not null`, meant as "a provider fetched artwork". But the scan reads the file's *own* embedded picture into `Existing`, and the copy constructor carries it into `Suggested` — so the clause was true of every well-tagged file there is. The visible symptom was a "will change" badge on all 127 rows; the invisible and worse one was that `LibraryTagWriter`'s skip-unchanged-rows rule was defeated for every one of them, so Save would have rewritten the entire folder to embed the artwork each file already had, moving every modified time in it. **Every test around it passed**, including one named `Save_SkipsARowWithNoChanges`, because not one of the fixtures gave a file a picture to begin with. The clause now compares the two images and only counts a provider's URL when the file has no picture of its own, which is exactly what the writer does with them. The general lesson is narrow and worth having: **a fixture that omits the field a rule is about will confirm the rule either way.**

The layout that replaced the table is one compact row per file — title, then artist and album, then the file name, all trimmed — with the editable fields opening underneath on demand. Two things about it are load-bearing. Every text element sets `TextTrimming` **and** `TextWrapping="NoWrap"`, because a wrapping row grows vertically instead of trimming and gives back the density the redesign was for. And the editor's field column is `*` with a `MaxWidth`, since a box eleven hundred pixels wide for the word "Renegade" puts the caret an inch from the label naming it.

### Finding: the same false-positive class had two more members, and showing the data is what found them (2026-08-30)

**WPF-UI's scroll bar is an overlay, and no layout can discover that.** It is painted over the viewport rather than taking width from it, so rows are measured as though it were not there and it lands on top of whatever is at the right-hand edge of every row it covers — here, the expand chevron. Measuring it settles the question in one step and is worth doing before adding padding, because padding would equally well hide a real layout fault: with UIAutomation, compare the `ListBox`'s `BoundingRectangle` to a `ListBoxItem`'s. They were identical — `right=2390` for both — so the bar occupies **0px** of layout and the fix is to reserve the room deliberately (`Padding="8,10,20,10"` on the row). A row narrower than its list would have meant the opposite, and a different bug.

**`HasChanges` had a second false positive, of exactly the same shape as the artwork one, and it survived the first fix.** `SpotifyTrackMapper.Apply(track, FullAlbum)` assigns `track.Genres = spotifyAlbum.Genres?.ToArray() ?? []` unconditionally. That is right for the recording path, where the track starts empty and Spotify is the only source there is. On this page the track starts as *the file's own tags*, and Spotify has returned an empty genre list for most of its catalogue since late 2024 — so a lookup blanked a genre the user had curated, `SameGenres` then reported a difference, and the row lit "will change" for something Save would never do (`TagLibTagStore` writes a genre only `if (track.Genres is { Length: > 0 })`). `Year` had the identical hole for an album with no release date. Both are now put back in `SpotifySearchMetadataProvider.ApplyAsync` when Spotify offers nothing, ahead of the artist-genre fallback, which also saves a request for every file that already had a genre.

The lesson is about the order the two were found in. The artwork bug was found by looking at the running app; **this one was found by building the feature that displays the data** — the "Also from the match" block exists because a row could say it would change while all three editable fields matched the file, and the first thing it drew on a real library was `Genre — was Hip-Hop, rap, hip hop`, an offer to erase a tag nobody had asked to erase. A value that is written on Save but rendered nowhere is a value no amount of looking at the page can check, and the suite could not check it either: the fixtures asserted what a lookup *adds*, never what it silently takes away.

### Finding: an automatic matcher cannot be its own escape hatch (2026-08-30)

`SpotifySearchMetadataProvider` builds its query from the file's own artist and title, and then discards any result whose artist disagrees with them. Both halves are right: Spotify always returns *something*, and the top hit for a misparsed filename is routinely a different song, so a strict gate is what stops the page writing a confident wrong answer into a correctly named file.

The consequence is that **the automatic path cannot fix a wrong artist, and Re-fetch is not the escape hatch it was documented as.** A file tagged `AC — Who Made Who` searches for that artist and rejects everything that is not it; pressing Re-fetch again asks the identical question and gets the identical answer. Auto-fetch will not touch the row either, because all three fields are filled in. The only remedy was to type the correct values by hand — which works, and forfeits the year, genre and artwork that come with a real match.

So `ILibraryMatchSearch` is a second, deliberately different shape: **the user's words instead of the file's, several results instead of a verdict, and no filtering on the way out.** A person reading a list can tell a live take from a studio one, and a filter that could hide the right answer is worse here than a list with a few wrong ones in it. Two details are load-bearing. The chosen candidate is fetched again through `/tracks/{id}` rather than tagged from the search result, because a search result is not a full track — the release date is on the album and the genre on the artist — and a hand-picked match arriving thinner than an automatic one would be a strange thing to have built. And it is the one place the "an embedded picture beats a provider's URL" rule is inverted: picking a different song says the file's own cover belongs to the track it used to claim to be, so it is dropped and the new URL is taken.

That last decision then exposed the display bug beneath it. `CoverArt` was bound to decoded bytes, but a lookup normally returns a **URL** and no bytes — the writer downloads it at save time — so the thumbnail showed the artwork the file already had while claiming to show what saving would do. On a hand-picked match it put the replaced track's sleeve on both sides of the before-and-after. The fix is to bind to either a decoded image or a `Uri` and let WPF's `Image.Source` fetch the second, which also keeps the network off the view model and off whatever thread a fetch finished on. **Both times on this page, the thing that made a bug visible was drawing the value rather than testing it.**

### Finding: the row editor was laid out as a form, and three WPF defaults were wrong underneath it (2026-08-30)

Four defects on one page, all of them invisible to the build and to the suite, and all found by running the app and looking at it.

**A star column inside a panel aligned `Left` is a content-sized column.** The editor was capped with `<StackPanel MaxWidth="820" HorizontalAlignment="Left">` around a two-column `Grid`. A `Left`-aligned element is *arranged* at its desired width, a `Grid`'s desired width is the sum of what its columns' content asked for, and a star column contributes its content — not its share of the available space. So every text box shrank to the width of the word inside it: a 130px box for "Thank Me" beside a 270px one for "Able Heart, Qveen Herby". The working idiom is the one already used elsewhere in the file — a star column carrying `MaxWidth` beside an empty `Auto` one, inside a Grid that is allowed to stretch. `MaxWidth` on a `Stretch` element is not the answer either: WPF centres what it cannot stretch, which pushes the whole panel off its own indent.

**`VirtualizingPanel.ScrollUnit` defaults to `Item`, which is wrong for any list whose rows are taller than a line.** Three items per wheel notch is a sensible default for a list of names; here a row is a three-line block and an opened one is a whole editor, so a notch moved most of a screen. `ScrollUnit="Pixel"` is a one-attribute fix and it works with `VirtualizationMode="Recycling"`. Measure it rather than trusting it: with UIAutomation, read a `ListBoxItem`'s `BoundingRectangle.Y`, send one `mouse_event` wheel notch over the list, and read it again. It moved **72 physical pixels** (48 DIP at 150%) against a row height of 115, where the default had been moving three whole rows.

**Removing a selection highlight means replacing the `ListBoxItem` template, not overriding its brushes.** The themed template paints its own accent bar as well as a background, so setters for `Background` and the `IsSelected` triggers leave a band behind. A `ControlTemplate` of a bare `ContentPresenter` removes the paint and keeps everything else: focus still moves, arrow keys still work, and the default focus rectangle still shows where the keyboard is. Worth doing on this page because nothing here acts on "the selected row" — the tick box and the chevron each carry their own meaning, and a third highlight tracking the arrow keys only competes with them.

**A behaviour hung off a `[RelayCommand]` is a behaviour the other route does not get.** The search box was seeded in `ToggleExpandCommand`, which only the row's title runs. Expanding by the chevron sets `IsExpanded` through its `IsChecked` binding and never touches the command, so the affordance that *looks* like the way to open a row left the box empty and a search from there asked Spotify for the empty string. `partial void OnIsExpandedChanged` is the seam that both routes cross. This is a general trap with `[ObservableProperty]` two-way bound to a control: the property is the event, the command is not.

And a fourth member of the genre false-positive family, which the earlier fix had missed because it was made in the wrong place. Putting the file's genre and year back when Spotify offers nothing was done inside `SpotifyCatalogEnricher` — correct for the Spotify path and no help at all when `FallbackMetadataProvider` falls through to Last.fm, whose `EnrichAsync` assigns `track.Genres = await ArtistTagsAsync(...)` just as unconditionally and returns nothing for an artist nobody has tagged. The rule belongs at the call site every library lookup passes through, `MetadataViewModel.FetchOneAsync`, where it holds for whichever provider answers and for any provider added later. The enricher keeps its own copy because the manual-match path does not go through `FetchOneAsync`.

One process note, because it cost a wrong diagnosis. Restoring a file with `mv` from a `.bak` preserves the backup's **mtime**, which can be older than the compiled output — so MSBuild considers the project up to date and the next test run silently exercises the previous build. A test that passed, then failed with the fix visibly present in the source, was that and nothing else. `touch` the file after any restore of this kind.

### Finding: an editor that opens inside a list row has to fit inside a list row, and this one never could (2026-08-31)

The row editor was rebuilt twice and tightened once, and every version was judged by looking at it. Measuring it instead settled the question in one reading. At the shell's minimum window of 1024×700, with UIAutomation reporting `BoundingRectangle` in physical pixels against a 150% scale factor:

| | DIP |
| --- | --- |
| Window | 700 |
| Page chrome above the list | 542 |
| **Track list viewport** | **347** |
| **One expanded row** | **539** |

An editor 1.55× the height of the container it opens in is not a spacing problem, and no amount of trimming reaches it. The page owns only about 130 DIP of that chrome — the intro paragraph and the folder, button and filter rows; the rest is the shell's title bar and nav strip and is not this page's to reclaim — so spending **all** of it still leaves a deficit, and the best case is a list showing exactly one file. The cheaper variant that was costed and rejected, moving the search and its results into a popover, lands an opened row at 347 DIP: precisely the viewport, which is the same failure at one decimal place.

So the editor became a pane beside the list rather than inside it. The list holds 5 rows at the minimum window and 11 maximized, the pane is the same size and in the same place whatever is picked, and choosing a different track reflows nothing — measured before and after a search that returned ten candidates, the list viewport read 373 DIP both times.

**The general rule is worth more than the fix: an expander is usable only when the thing it opens is smaller than the space it opens into.** That is a measurement, and it is available before the layout is written. Reach for master–detail when it is not, and reach for it early — two rebuilds of the panel went into making a shape work that could not have worked at any spacing.

Three second-order notes from doing it:

- **Selection had to come back, and removing it had been right on its own terms.** A highlight following the arrow keys while nothing acted on the selected row was noise. In a split view the selection is the pane's input, so the row has to say which one is showing — a tinted background and a narrow accent edge, not the themed container's saturated fill. "Nothing acts on the selection" was a fact about the layout, not about the control, and it stopped being true the moment the layout changed.

- **Re-filtering nulls the list's `SelectedItem`.** `ApplyFilter` clears and refills `VisibleTracks`, and the clear travels out through the two-way binding — so without capturing the pick and putting it back, the editor emptied on every keystroke in the filter box, including the keystrokes narrowing the list towards the row being edited.

- **One scroll region was the wrong instinct.** Letting the whole pane scroll put ten search results below the fold, and the only evidence the search had worked was the scroll bar getting shorter. The fields scroll and the search box and its results are pinned to the foot of the pane instead: the results are what the user just asked for, and the fields are what they will scroll back to, having already read them.

Commands bound from inside the pane reach the page with `RelativeSource={RelativeSource AncestorType=UserControl}`; the row template's old `AncestorType=ListBox` has no such ancestor once the editor is out of the list.

### Finding: a meter that reports has to be read exactly once (2026-09-02)

`AudioLevelMeter.Read` drains the interval it reports. That is deliberate and documented — it is
what lets a slow reader see the loudness of its whole interval rather than an instant — but it
makes the meter a single-consumer object, and nothing said so where a second consumer would look.

Adding the silence and clip lamps looked like a job for a second reader: poll `Read`, notice a
run of quiet or a full-scale figure, light a lamp. That would have worked in a test and been
wrong in the app. Two readers split the samples between them, so the bars would have reported a
fraction of the audio and the lamps the rest, with neither obviously broken — the meter would
simply have read low, by an amount that varied with how often each side happened to poll.

Both flags are folded in on the capture thread inside `Write` instead, where every sample passes
once, and neither is disturbed by `Read`. Two things fell out of doing it there. The clip flag
has to come off the **peak** sample, not off a reading: everything this meter publishes is RMS,
which for real music sits ten to twenty decibels below the peak, so a clip lamp driven from
`LevelReading.Decibels` would never light at all. And the clip threshold is 0.999 rather than 1.0,
because a converter out of headroom pins to the largest value the format holds — 32767/32768 for
16-bit — which never quite normalises to full scale.

The silence threshold is −80 dBFS rather than exact zero for the same class of reason: digital
silence is zeroes, but a capture graph with any analogue stage in it idles on a dither floor, and
calling that "not silent" would disable the warning on exactly the hardware that needs it.

Verified live, and by accident: a staged profile recording the default endpoint while Spotify
played to VB-CABLE reproduced the failure the lamp exists for, and the lamp lit at six seconds.

### Finding: a tag the recorder writes and the editor cannot reach is a tag nobody can fix (2026-08-31)

The page shipped with three editable fields — title, artist, album — and showed year, genre and artwork read-only beside them. The scope question "which fields belong here" has an answer that is not a matter of taste: **whatever the recording path writes**. `FFmpegArguments.MetadataArguments` writes title, artist, album, album artist, genre, date, track, disc and copyright, so a recording could carry a wrong disc number that no part of Offstream could correct. Composer, comment and BPM are in neither list, and that is what keeps this from drifting into a general-purpose tag editor.

Filling the gap turned up three defects underneath it, none of which were visible while the fields were absent.

**Ten fields do not stack.** 10 × 65 DIP is 650 in a 347 DIP pane — the same arithmetic as the finding above, discovered a day later on the pane that fixed it. Paired into two columns it is 390 DIP, so seven of ten are visible at the minimum window and all ten at any real size. Numeric boxes read fine at half width, which is what makes the pairing free.

**An emptied box is not a change.** `HasChanges` compared before against after, so clearing the album box lit **Will change** and then saved nothing — the writer has never written a blank over a value. The predicate now asks whether saving would alter the file, which is the question the badge was always claiming to answer.

**Writing one artist back over a tag that held two destroyed data.** ID3v2.3 separates artists with a slash, so the file recorded as `AC/DC` holds the two values `AC` and `DC`; `Read` took `FirstPerformer`, the box showed `AC`, and `Write` did `tag.Performers = [track.Artist]` — narrowing the tag on the page whose purpose is repairing tags. It had been there since the writer was written and was invisible until an album artist box appeared next to the artist box showing `AC, DC` beside `AC`. The fix writes the original list back verbatim when its first entry still matches the box, since that is where the box was filled from; a match satisfies this by construction, because the mapper sets the artist from the first performer it assigns.

The tempting fix is worse than the bug: splitting the box on commas the way the genre box splits reads `Earth, Wind & Fire` as three artists. **A list of names cannot be edited as a comma-separated line.** Genres can, because a genre list really is a list and commas inside one genre are vanishingly rare; names cannot, because commas inside one name are ordinary. Both artist boxes therefore keep the array they were filled from whenever the text still matches it, and take the whole line as a single value when it does not.

The never-erase rule from the day before became one helper, `LibraryLookup`, rather than two hand-written copies, and grew from genre and year to all seven fields a lookup can leave empty. There are still two call sites, and both are still load-bearing — `MetadataViewModel.FetchOneAsync` for every automatic lookup and `SpotifyCatalogEnricher` for the manual **Use this** path, which does not pass through it — but the rule itself now exists once, so the next field cannot be added to one copy and forgotten in the other. That is precisely how the bug survived its first fix.

### Finding: two of the four ways the encoding profiles differ from a comparable recorder are ours to keep (2026-09-02)

The profiles were audited flag by flag against another Windows Spotify recorder's, on the working
assumption that any difference was a gap. Two were. The other two are decisions this project had
already taken, and they are written down here rather than left to be rediscovered, because each
one reads from the outside as an obvious improvement that nobody has got round to.

> This finding first recorded **three** of the four as ours to keep, on the reasoning below about
> MP3 rate mode. That reasoning was sound about the trade-off and wrong about the conclusion, and
> the MP3 paragraph now records what was actually built. It is left in place rather than deleted
> because the argument it makes is what shaped the fix.

**The real gap: the attached picture was typed `Other`.** `-disposition:v attached_pic` marks a
stream as cover art and settles nothing else about it — in particular it leaves the picture type
at 0, which is `Other` in both ID3's `APIC` frame and FLAC's `METADATA_BLOCK_PICTURE`. Every file
Offstream had ever written carried its sleeve under that type, and software that looks for a
front cover specifically skips it.

Two `-metadata:s:v` arguments fix it, and the surprise is that **neither is free text in the way
it looks**. `comment` is read by the muxer as the picture *type*, matched against the spellings
the format defines: `Cover (front)` selects type 3, and anything unrecognised falls back to
`Other` without complaint — encoding with `comment=Sleeve test` produced a file reporting
`Other`, and the string was nowhere in it. `title` is the description and is genuinely free text.
Reading the result back with ffprobe is not enough to tell these apart, because ffprobe reports
the *type* through the same `comment` key the argument uses, so a file where the string was
stored verbatim and a file where it was interpreted look identical; the isolation run — each
argument alone, then a byte search of the output — is what separated them, and
`Encode_TypesTheCoverArtAsTheFrontCover` asserts it through TagLib# for that reason. M4A takes
both arguments and stores neither: the mov muxer keeps a cover as a bare atom with nowhere to put
a type or a description.

**MP3 takes `-abr 1`, and the constant rung it would have cost is a rung of its own.** The other
ladder passes `-abr 1` at every rung and keeps plain `-b:a 320k` for a top rung beside them.
Offstream's `-b:a {rate}k` with no `-abr` *is* that top rung, which is why the first reading of
this was "already there": the default output was the best MP3 that ladder can produce, and taking
the `-abr 1` on its own would have moved the default down a notch on the most-used format.

What that reading got wrong was treating the rung as unaffordable. The objection to adding it was
that the bitrate would stop being a plain kbps number, which is the `LAMEPreset`-shaped setting
§5.1 rejected on purpose — but a preset enum bundles the rate and the mode into one value, and
that is the part §5.1 rejected. Keeping them as two values, an `int` and a two-member
`BitrateMode`, is not that setting: `bitrateKbps` is still a number the file can be hand-edited
to, and validation still has a range to check it against. A single dropdown presents the pair as
one ladder, so the split costs the page nothing.

The flag itself is **profile data, not a branch**: `EncodingProfile.AverageBitrateArguments`
holds `["-abr", "1"]` for MP3 and is empty everywhere else, so `Build` appends whatever the
profile declares and never asks what format it is looking at. Empty is the honest answer for the
other lossy profiles rather than an omission — libopus and the native AAC encoder both vary their
rate unasked, so there is no switch to throw and no constant rung to offer beside them.
`SupportsBitrateMode` is derived from that list being non-empty, which is what keeps the dropdown
and the encoder from disagreeing when a format is added.

Two things worth knowing before measuring this. **ABR at a given rate produces a smaller file
than CBR at the same rate**, because the encoder is aiming for the number across the recording
instead of holding it — the nominal rate stops being a promise about the file's size. And **pink
noise is the wrong thing to measure it on**: two synthetic sources here, one at a steady level
and one with a slow 6 dB swell, came back within 0.1% of each other at ABR 320, because LAME
allocates on spectral demand and noise is equally easy to code at any level. Neither number says
anything about music, which is why none is quoted here or in the changelog.

**WAV stays `-c:a pcm_s16le`, and the stream-copy §5.1 used to offer is off the table.** The
other implementation copies the captured WAV instead of encoding it, which is the same idea. The
temp file never matches: `WasapiLoopbackCapture` reports the endpoint's shared-mode mix format,
which on Windows is 32-bit float — `codec_name=pcm_f32le`, confirmed with ffprobe. Copying it
would make Offstream's WAV output float32, twice the size of the 16-bit file it writes today, in
the one format people pick precisely because they are handing it to something else. The copy is
not a cheaper way to produce the same file; it produces a different file.

**Cover art stays `-c:v mjpeg`.** The other implementation pipes the downloaded bytes through
`-c:v copy`, which is a free generation of quality whenever the source is already JPEG. It is not
always JPEG here: `CoverArtFetcher.TempFileFor` keeps a `.png` extension deliberately, because
`CoverArtWriter` derives the picture's MIME type from it, and `copy` would put a PNG into an APIC
frame. The re-encode is normalisation, not waste.

One thing the audit turned up that is neither, and it is now gone:
`WaveFormatExtensions.GetMp3Restrictions` had no caller outside its own tests. It answered "which
MP3 limits does this capture format exceed" — more than two channels, or above 48 kHz — because
LAME had to be told, and the reference implementation resampled and reduced channels by hand
before handing it the buffer. ffmpeg does both unasked, so the answer had nowhere to go and the
method, its `Mp3Restriction` enum and its four ported tests are deleted.

That was checked rather than assumed, because libmp3lame really does refuse more than two
channels and really is capped at 48 kHz — if ffmpeg had passed the constraint through instead of
resolving it, the dead guard would have been pointing at a live crash on any 5.1 or high-rate
endpoint. Encoding a 6-channel 48 kHz float32 WAV and a stereo 96 kHz one through the exact
profiles this app ships:

| Input | MP3 | FLAC | AAC | Opus |
| --- | --- | --- | --- | --- |
| 5.1 @ 48 kHz | downmixed to stereo | 6 channels kept | 6 channels kept | 6 channels kept |
| stereo @ 96 kHz | resampled to 48 kHz | — | — | — |

Every case exits 0. Worth knowing for its own sake: **a multichannel endpoint survives as
multichannel in every format except MP3**, which is a real difference between the formats on the
Settings page and not something the app says anywhere.

---

## 12. Risks

| Risk | Impact | Likelihood | Mitigation |
| --- | --- | --- | --- |
| ~~Routing COM breaks under .NET 10~~ | High | ~~Low~~ **Occurred** | **Resolved in Phase 0** — it was certain, not low: `IAudioPolicyConfig` is WinRT/IInspectable-based and .NET 5 removed that support. Marshalling rewritten and proven; see DR-0001 |
| ~~Downlevel (Win10 22H2) routing unverified~~ | — | — | **Closed** — Windows 10 is out of scope (open question 6). Only the 21H2+ IID path ships, and it is proven |
| Routing proven by COM round-trip, not audibly | Low | Medium | This machine has one render endpoint. Confirm with a second endpoint (VB-CABLE or any second output) whenever one is available — Phase 7's clean-VM pass, which used to carry this, is gone (Windows 11 only), so it now rides on Phase 8's install run |
| NAudio 1.10 → 2.x behavioural drift in capture | Medium | Medium | Existing tests + Phase 0 capture spike |
| SpotifyAPI.Web 5→7 auth rework | Medium | High | Isolated behind the provider interface |
| WPF rewrite scope creep | Medium | High | §11 is closed; parity matrix is the gate |
| ffmpeg cover art unreliable for Ogg | Medium | Medium | Per-format ffprobe tests; TagLib# fallback for that container |
| Trimming/AOT breaking COM | High | Low | Explicitly disabled; documented in §2.2 |
| Code-signing cost and lead time | Medium | High | Begin procurement during Phase 0 |
| ffmpeg licence obligations | Medium | Low | LGPL-only build; ship licence + source offer |
| ~~**VB-CABLE redistribution terms**~~ | — | — | **Closed** — detect only, decided 2026-08-14 (question 9). No vendor binary is redistributed and no installer step invokes one, so there is nothing left to be exposed by. Attribution ships in `NOTICE` and on the Settings page |
| Unsigned installer trips SmartScreen | High | Medium | Accepted for v1 (question 3): no certificate yet, signing step wired and inert. The release notes say so rather than letting users discover it |
| ffmpeg LGPL source offer goes stale | Medium | Medium | Pin the exact bundled build's version and origin in the release, so the offer names something that still exists |

---

## 13. Open questions

1. ~~.NET 10 confirmed as current LTS, and WPF-UI's support for it?~~ **Answered (DR-0001).** .NET 10 is LTS to 14 Nov 2028; WPF-UI 4.3.0 ships a `net10.0-windows7.0` target and compiles clean with CommunityToolkit.Mvvm 8.4.2.
2. ~~Bundle ffmpeg (recommended) or resolve at runtime?~~ **Answered (DR-0001).** Bundle an LGPL-only build with a runtime override; every required encoder (`libmp3lame`, `libopus`, `aac`, native `flac`) is present in a stock build, so no GPL component is needed.
3. ~~Signing certificate: EV (immediate SmartScreen reputation, hardware token) or OV (cheaper, reputation accrues)?~~ **Deferred 2026-08-14: ship unsigned, with the signing step already in place.** There is no certificate yet and buying one is procurement rather than engineering, so the release pipeline carries a signing step that is a no-op while no certificate is configured and signs everything the moment one is. What this costs is stated plainly rather than hidden: SmartScreen warns on first run of an unsigned installer, and every user has to click through it. The EV/OV choice itself is still open — it just no longer holds the release.
4. ~~Auto-update wanted at all, given the predecessor's updater was deliberately disabled there?~~ **Answered 2026-08-14: no self-update in v1, and no in-app version check either.** The installer points Windows at the releases page and the release notes announce new builds; the app itself makes no update-related network call. The full item — signed manifest, background download, signature verification, apply on restart — is the largest piece of Phase 8 and its whole value rests on signatures Offstream does not yet have (question 3), so building it now would mean building the verification step against nothing to verify. A link is honest about that; a silent self-update over an unauthenticated channel would be worse than none.
5. Keep French and add more languages, or English-only for v1?
6. ~~Is a Windows 10 floor acceptable, or must Windows 11 features stay optional?~~ **Answered 2026-08-11: Windows 11 only.** Windows 10 left support in October 2025. Consequences: Phase 0's downlevel gap is closed rather than deferred; Windows 11 features need no optional path; and the TFM may be raised to `net10.0-windows10.0.22621.0`, which unlocks the WinRT projections SMTC track detection needs (§5.3, Phase 7) — see the note there before Phase 7 starts.
7. Should FLAC/AAC ship in v1, or wait until parity is proven in the field?
8. Does the clean-slate settings decision (§6) need a one-page "moving from the predecessor" note in the docs, listing which preferences to re-enter?
9. ~~**How does Offstream handle VB-CABLE?**~~ **Answered 2026-08-14: (a) detect only.** No vendor binaries enter the repo, in this release or a later one — the user installs VB-CABLE themselves from vb-audio.com and Offstream reports whether it is there. This is what Phase 7 already built, so nothing changes in the app; what it settles is that the installer never grows a driver step, and that the ~3 MB of unsigned third-party kernel-mode payload option (b) would have added never enters the release. The origin and donationware attribution the licence asks for ships regardless, and already does — in `NOTICE` and beside the device picker on the Settings page. The rejected alternatives were **(b) ship the vendor package unmodified** and launch its setup elevated, as the predecessor does, and **(c) ask VB-Audio for written agreement** to bundle properly.

   Note the shipping question was always separate from the *runtime* one: whether recording quality without a virtual cable is good enough to make it optional at all is a Phase 0 measurement.
