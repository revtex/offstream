# Offstream

A Windows desktop recorder for Spotify audio on **.NET 10 + WPF**, with all audio conversion delegated to **ffmpeg**.

Offstream succeeds **Spytify**, the .NET Framework 4.6.1 / WinForms application it takes its behaviour from.

## Status

**Phases 0–6 complete.** The app records: pick a folder and a format, press Start, and tracks land on disk with their tags written. Phase 7 (SMTC track detection, device hot-plug, long paths) is next.

```powershell
.\build.ps1 -Clean -Test -IncludeDesktop     # 771/771 green
dotnet run --project src/Offstream.App
```

- **Phase 0** — the retarget spike: 8/8 checks green on Windows 11 build 26200, unelevated. Endpoint enumeration, `IAudioPolicyConfig` binding, routing a process to an endpoint and back, session mute, and 30 s of WASAPI loopback capture verified non-silent.
- **Phase 1** — six projects, CI, analyzers as errors, Serilog, and a WPF-UI Fluent shell that launches and is driven by a FlaUI test.
- **Phases 2–3** — the reference suite green on .NET 10, then the recording pipeline: capture, track detection, and ffmpeg encoding to MP3/WAV/FLAC/AAC/Ogg/Opus with tags and cover art.
- **Phase 4** — Spotify Web API metadata over PKCE, with the refresh token protected by DPAPI.
- **Phase 5** — settings at `%APPDATA%\Offstream\settings.json`: grouped schema, atomic writes, no importer for the predecessor's file.
- **Phase 6** — the shell: Record, Settings and Advanced tabs, inline validation, en/fr resources, a live waveform so silence is visible while it is happening, tray icon and single-instance guard.

**Offstream targets Windows 11 only.** Windows 10 left support in October 2025 and is out of scope.

Read in order: **[`docs/MODERNIZATION-PLAN.md`](docs/MODERNIZATION-PLAN.md)** for architecture, parity matrix and the ten phases; then **[DR-0001](docs/decisions/0001-phase-0-retarget-spike.md)**, which invalidates one of the plan's original assumptions and records what replaced it; then **[DR-0002](docs/decisions/0002-phase-1-solution-scaffold.md)**.

## Offstream owns its own names

Behaviour is inherited. **Naming is not.** Every namespace, type, file, folder, project, resource key and on-disk path in this repository is Offstream's own — nothing carries `EspionSpotify`, `Spytify`, or any other predecessor identifier, and a build-time test enforces it. Settings live at `%APPDATA%\Offstream\settings.json`, and Offstream ships **no importer** for the old app's `user.config`: first run starts from clean defaults.

Plan [§0](docs/MODERNIZATION-PLAN.md) is the full rule and mapping table.

## Relationship to the app being retired

The predecessor lives at `../spy-spotify` (a fork of the unmaintained [`jwallet/spy-spotify`](https://github.com/jwallet/spy-spotify)). It stays on disk as a **reference to read**, not a dependency. Nothing here builds against it, and nothing here is named after it.

It matters for three reasons:

1. **Source of truth for behaviour.** Spotify window-title parsing, ad and idle-state detection, the audio ring buffer and silence-trim semantics, and the filename template engine all encode years of edge cases. Port the logic; don't reinvent it — and rename it on the way in.
2. **Its test suite is the safety net.** 293 xUnit tests come across with their assertions intact under `Offstream.Core.Tests`. Phase 2's exit criterion is all of them green on .NET 10 *before* any behaviour changes.
3. **It owns the hardest asset.** `Router/AudioPolicyConfigFactory*` drives per-application audio routing through the undocumented `IAudioPolicyConfig` COM interface, with separate implementations for Windows 21H2 and downlevel builds. That logic transfers intact into `Offstream.Core.Interop.Routing` — it is the main reason this is a retarget rather than a rewrite, and the reason a Go/Wails rewrite was rejected.

Reference paths **in the old tree** (read-only; none of these names appear in this repo):

| Path | What it holds |
| --- | --- |
| `EspionSpotify/Router/` | Undocumented audio-routing COM interop |
| `EspionSpotify/AudioSessions/` | WASAPI capture, throttler, circular buffer |
| `EspionSpotify/Spotify/` | Process and window-title track detection |
| `EspionSpotify/Models/FileNameTemplate.cs` | Filename template engine |
| `EspionSpotify/Native/FileManager.cs` | Path assembly, length budgeting |
| `EspionSpotify.Tests/` | The 293-test suite |
| `EspionSpotify.FakeSpotify/` | Harness that simulates Spotify window titles |
| `BUILD.md`, `CLAUDE.md` | How the old app builds; its architecture notes |

## Development environment

### Prerequisites

| Requirement | Why | winget package |
| --- | --- | --- |
| **Windows 11** | WASAPI, undocumented audio-routing COM | — |
| **.NET 10 SDK** | Build, test, run | `Microsoft.DotNet.SDK.10` |
| Git | — | `Git.Git` |
| **ffmpeg + ffprobe** | Runtime encoding *and* integration tests | `BtbN.FFmpeg.LGPL.8.1` |
| Spotify desktop | Manual testing (`Offstream.FakeSpotify` covers most cases) | `Spotify.Spotify` |
| VB-CABLE *(optional)* | Testing the audio-routing path | not on winget — see step 4 |
| WiX v4 *(Phase 8 only)* | Installer | `dotnet tool install --global wix` |

### Setting up a fresh Windows 11 machine

Everything below runs in **Windows PowerShell or Windows Terminal on Windows itself** — not in WSL. This is a Windows desktop app using Windows-only COM and audio APIs; it cannot be built or run from a Linux shell, even though the repo may live on a drive both can see.

No step needs an elevated prompt except VB-CABLE.

**1. Confirm winget is available**

```powershell
winget --version
```

Windows 11 ships with it (via *App Installer*). If the command is not found, install **App Installer** from the Microsoft Store, then reopen the terminal.

**2. Install the .NET 10 SDK**

```powershell
winget install --id Microsoft.DotNet.SDK.10 --source winget
```

**Then close and reopen your terminal.** The installer edits the machine `PATH`, and an already-open session will not see it — the single most common reason `dotnet` "isn't installed" immediately after installing it.

The SDK is what matters here, not the runtime. A machine can have several .NET runtimes and still be unable to build anything:

```powershell
dotnet --list-sdks       # must list a 10.x entry — empty output means runtime-only
dotnet --list-runtimes   # informational; runtimes alone are not enough
```

> The runtime-only state is not hypothetical — this repo was created on a machine with the .NET 3.1 and 8.0 *runtimes* and no SDK at all, so nothing could be built. `.\build.ps1` fails fast with the install command when it sees that.

Install the **x64** SDK on an x64 machine and the **Arm64** SDK on Arm64 (Snapdragon X, etc.); winget picks correctly on its own, but a hand-downloaded installer may not. Check with `echo $env:PROCESSOR_ARCHITECTURE`.

**3. Install Git and ffmpeg**

```powershell
winget install --id Git.Git --source winget
winget install --id BtbN.FFmpeg.LGPL.8.1 --source winget
```

Reopen the terminal again afterwards, then confirm ffmpeg resolves:

```powershell
ffmpeg -version
ffprobe -version
```

Both must be on `PATH` — the encode-integration tests shell out to them and assert results with `ffprobe`.

On the ffmpeg build: `BtbN.FFmpeg.LGPL.8.1` is an **LGPL** build, which is the licensing posture Offstream must ship under (plan §5.1), so developing against it keeps dev and release consistent. `Gyan.FFmpeg` also works for local development but is a GPL build — don't let it become the bundled one.

**4. Install VB-CABLE (optional, needed only for routing work)**

Not available through winget. Download the VB-CABLE Virtual Audio Device from [vb-audio.com](https://vb-audio.com/Cable/), unzip, right-click `VBCABLE_Setup_x64.exe` → **Run as administrator**, then reboot. Skip this until you are actually working on audio routing or Phase 0's spike.

It provides a virtual endpoint that Spotify's audio session can be pinned to, so a recording captures Spotify alone with no notification sounds bleeding in.

> The predecessor **bundles** the whole VB-CABLE package and installs it from its own UI. Offstream does not, and no vendor binaries belong in this repo for now — VB-CABLE is donationware whose licence forbids integrating it into another installation procedure without the author's agreement. That is plan open question 9; until it is answered, Offstream **detects** the cable and links out. Install it yourself as above.

**5. Install Spotify (optional)**

```powershell
winget install --id Spotify.Spotify --source winget
```

`Offstream.FakeSpotify` simulates window titles for most test scenarios, so the real client is only needed for end-to-end manual passes.

**6. Verify**

From the repo root:

```powershell
dotnet --info
```

Expect an **SDK** section listing 10.x, not just runtimes. Then `dotnet build` and `dotnet test` complete the check.

### Setup troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| `dotnet` not recognised after installing | Terminal predates the `PATH` change — open a new one. |
| `dotnet --list-sdks` is empty | Runtime installed, SDK not. Install `Microsoft.DotNet.SDK.10`. |
| `NETSDK1045: current SDK does not support .NET 10` | An older SDK is winning on `PATH`, or a `global.json` pins an older version. Check `dotnet --version`. |
| `ffmpeg` not recognised in tests only | Your IDE inherited the pre-install environment. Restart the IDE, not just the terminal. |
| Build fails under WSL / on Linux | Expected — `net10.0-windows` and the COM interop are Windows-only. Build from Windows. |
| `winget` prompts about source agreements | Run `winget list --accept-source-agreements` once. |

---

### VS Code (primary workflow)

Open the folder; VS Code will prompt for the recommended extensions in `.vscode/extensions.json`:

- **C# Dev Kit** (`ms-dotnettools.csdevkit`) — solution view, test explorer, debugging
- **C#** (`ms-dotnettools.csharp`) — Roslyn language server
- **XAML Styler** (`ms-dotnettools.xaml`) — XAML formatting and IntelliSense
- **EditorConfig** — honours the repo's `.editorconfig`

`.vscode/tasks.json` and `.vscode/launch.json` are committed, so `Ctrl+Shift+B` builds and `F5` debugs the WPF app.

**One real limitation to plan around:** *VS Code has no WPF visual designer.* That is Visual Studio only. In practice this matters less than it sounds — MVVM means the XAML is declarative markup, and plenty of WPF work is done without the designer. Two things make it comfortable:

1. **XAML Hot Reload** works from the CLI — `dotnet watch --project src/Offstream.App` restarts on C# changes and applies XAML edits live.
2. Keep Visual Studio installed for the occasional layout-heavy view, and edit everything else in VS Code.

Given how much friction the old app's WinForms designer caused — geometry expressed as `tableLayoutPanel` row/column arithmetic, spread across generated code — hand-written XAML is a net improvement for this workflow, not a compromise.

---

### `build.ps1`

The usual tasks are wrapped in a script at the repo root, so you don't have to remember flags. It checks the environment first — .NET 10 SDK actually present (not just a runtime), ffmpeg on `PATH` before running tests — and fails with the fix rather than a compiler error.

```powershell
.\build.ps1                          # Debug build
.\build.ps1 -Configuration Release
.\build.ps1 -Test                    # build, then run the whole suite
.\build.ps1 -Test -Filter FileNameTemplate
.\build.ps1 -Clean -Test             # rebuild from scratch, then test
.\build.ps1 -Format                  # apply .editorconfig
.\build.ps1 -VerifyFormat            # what CI enforces
.\build.ps1 -Publish                 # self-contained win-x64 publish
.\build.ps1 -Run                     # build and launch
```

`-Publish` hard-codes `--self-contained true`, `PublishSingleFile=true` and `PublishTrimmed=false`. Those are not defaults to override — see the constraint below.

If PowerShell blocks the script, either unblock it once (`Unblock-File .\build.ps1`) or run it as `powershell -ExecutionPolicy Bypass -File .\build.ps1`.

### CLI

The script is a convenience, not a wrapper you're locked into:

```powershell
dotnet restore
dotnet build
dotnet test                                   # whole suite
dotnet test --filter FullyQualifiedName~FileNameTemplate
dotnet test -- --coverage                     # coverage report
dotnet run --project src/Offstream.App
dotnet watch --project src/Offstream.App      # hot reload
dotnet format                                 # apply .editorconfig
dotnet format --verify-no-changes             # what CI enforces
```

Publishing (self-contained, **untrimmed and non-AOT** — see below):

```powershell
dotnet publish src/Offstream.App -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=false
```

> **Do not enable `PublishTrimmed` or `PublishAot`.** The audio-routing code relies on built-in COM interop, which AOT does not support, and WPF trims poorly. This is a correctness constraint, not a preference — see `CLAUDE.md`.

---

### Visual Studio 2022+

1. Install with the **.NET desktop development** workload (brings WPF templates, the XAML designer, and the test runner).
2. Ensure the **.NET 10** individual component is checked.
3. Open `Offstream.slnx`, set `Offstream.App` as the startup project, press F5.

Use it when you want the XAML designer, the visual tree / live property explorer, or the memory and CPU profilers.

### Rider

Open `Offstream.slnx`. Configure the .NET 10 SDK under *Settings → Build → Toolset*. Rider has its own XAML preview and generally the best refactoring for a port of this size.

---

### Running the tests

The suite is the safety net for the whole port, so it should be fast and offline:

```powershell
dotnet test                                        # all
dotnet test tests/Offstream.Core.Tests             # unit + golden + integration
dotnet test tests/Offstream.UI.Tests               # ViewModels, resources, and the FlaUI suite
dotnet test --filter "Category!=Desktop"           # what CI runs
```

- **Integration tests shell out to real ffmpeg** and assert results with `ffprobe`, so both must be on `PATH`.
- **No test may hit the network.** Spotify and Last.fm are covered by recorded fixtures.
- **`Category=Desktop` tests launch the real window** and drive it with FlaUI, so they need an interactive session and cannot share a machine with someone using the keyboard. CI and `build.ps1` exclude them unless you pass `-IncludeDesktop`. They point the app at a throwaway `OFFSTREAM_HOME`, so a run never touches your own settings.
- CI runs the same commands on `windows-latest` with analyzers as errors.

### Referencing the app being retired

The predecessor at `../spy-spotify` is a .NET Framework 4.6.1 solution and **needs a different toolchain** — MSBuild from Visual Studio Build Tools plus `nuget.exe`, not the `dotnet` CLI. Its own `BUILD.md` covers this. You only need that toolchain if you want to run the old app side by side to compare behaviour; nothing in Offstream builds against it.

## Layout

```
spike/   Offstream.Spike — Phase 0 retarget spike (delete once Phase 2 lands)
src/     Offstream.Core (no UI refs) and Offstream.App (WPF)
tests/   Offstream.Core.Tests (xUnit), Offstream.UI.Tests (FlaUI)
tools/   Offstream.FakeSpotify (window-title harness)
build/   installer, signing, icons
docs/    modernization plan, decision records
```

## Licence

MIT. Portions of the logic derive from the predecessor, which is MIT-licensed; its copyright notices are retained in `LICENSE` alongside Offstream's. Attribution is a licence obligation and lives there — it is not a reason to keep the predecessor's identifiers in the source.
