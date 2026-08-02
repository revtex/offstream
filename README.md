# Offstream

A Windows desktop recorder for Spotify audio — modernized rewrite on **.NET 10 + WPF**, with all audio conversion delegated to **ffmpeg**.

Offstream supersedes **Spytify**, the .NET Framework 4.6.1 / WinForms application it is derived from.

## Status

**Pre-implementation.** The plan is written; no application code exists yet.

Start here: **[`docs/MODERNIZATION-PLAN.md`](docs/MODERNIZATION-PLAN.md)** — architecture, feature-parity matrix, dependency upgrades, testing strategy, and a ten-phase delivery plan with exit criteria.

## Relationship to the app being retired

The predecessor lives at `../spy-spotify` (a fork of the unmaintained [`jwallet/spy-spotify`](https://github.com/jwallet/spy-spotify)). It stays on disk as a **reference**, not a dependency. Nothing here builds against it.

It matters for three reasons:

1. **Source of truth for behaviour.** Spotify window-title parsing, ad and idle-state detection, the audio ring buffer and silence-trim semantics, and the filename template engine all encode years of edge cases. Port them; don't reinvent them.
2. **Its test suite is the safety net.** 293 xUnit tests come across. Phase 2's exit criterion is all of them green on .NET 10 *before* any behaviour changes.
3. **It owns the hardest asset.** `Router/AudioPolicyConfigFactory*` drives per-application audio routing through the undocumented `IAudioPolicyConfig` COM interface, with separate implementations for Windows 21H2 and downlevel builds. That code is kept verbatim — it is the main reason this is a retarget rather than a rewrite, and the reason a Go/Wails rewrite was rejected.

Useful reference paths in the old tree:

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

| Requirement | Why | Install |
| --- | --- | --- |
| Windows 10 22H2+ / Windows 11 | WASAPI, undocumented audio-routing COM | — |
| **.NET 10 SDK** | Build, test, run | `winget install Microsoft.DotNet.SDK.10` |
| Git | — | `winget install Git.Git` |
| **ffmpeg + ffprobe** | Runtime encoding *and* integration tests | `winget install Gyan.FFmpeg` |
| Spotify desktop | Manual testing (the `FakeSpotify` harness covers most cases) | `winget install Spotify.Spotify` |
| WiX v4 *(Phase 8 only)* | Installer | `dotnet tool install --global wix` |

Verify:

```powershell
dotnet --info          # expect an SDK, not just a runtime
ffmpeg -version
ffprobe -version
```

> The machine this repo was created on has the .NET **runtime** only — `dotnet --list-sdks` is empty. Install the SDK before Phase 1.

`Gyan.FFmpeg` is a GPL build, which is fine for development. Shipping requires an **LGPL-only** build — see plan §5.1.

---

### VS Code (primary workflow)

Open the folder; VS Code will prompt for the recommended extensions in `.vscode/extensions.json`:

- **C# Dev Kit** (`ms-dotnettools.csdevkit`) — solution view, test explorer, debugging
- **C#** (`ms-dotnettools.csharp`) — Roslyn language server
- **XAML Styler** (`ms-dotnettools.xaml`) — XAML formatting and IntelliSense
- **EditorConfig** — honours the repo's `.editorconfig`

`.vscode/tasks.json` and `.vscode/launch.json` are committed, so `Ctrl+Shift+B` builds and `F5` debugs the WPF app once Phase 1 has scaffolded the projects.

**One real limitation to plan around:** *VS Code has no WPF visual designer.* That is Visual Studio only. In practice this matters less than it sounds — MVVM means the XAML is declarative markup, and plenty of WPF work is done without the designer. Two things make it comfortable:

1. **XAML Hot Reload** works from the CLI — `dotnet watch --project src/Offstream.App` restarts on C# changes and applies XAML edits live.
2. Keep Visual Studio installed for the occasional layout-heavy view, and edit everything else in VS Code.

Given how much friction the old app's WinForms designer caused — geometry expressed as `tableLayoutPanel` row/column arithmetic, spread across generated code — hand-written XAML is a net improvement for this workflow, not a compromise.

---

### CLI

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
3. Open `Offstream.sln`, set `Offstream.App` as the startup project, press F5.

Use it when you want the XAML designer, the visual tree / live property explorer, or the memory and CPU profilers.

### Rider

Open `Offstream.sln`. Configure the .NET 10 SDK under *Settings → Build → Toolset*. Rider has its own XAML preview and generally the best refactoring for a port of this size.

---

### Running the tests

The suite is the safety net for the whole port, so it should be fast and offline:

```powershell
dotnet test                                        # all
dotnet test tests/Offstream.Core.Tests             # unit + golden + integration
dotnet test tests/Offstream.UI.Tests               # FlaUI, needs a desktop session
```

- **Integration tests shell out to real ffmpeg** and assert results with `ffprobe`, so both must be on `PATH`.
- **No test may hit the network.** Spotify and Last.fm are covered by recorded fixtures.
- CI runs the same commands on `windows-latest` with analyzers as errors.

### Referencing the app being retired

The predecessor at `../spy-spotify` is a .NET Framework 4.6.1 solution and **needs a different toolchain** — MSBuild from Visual Studio Build Tools plus `nuget.exe`, not the `dotnet` CLI. Its own `BUILD.md` covers this. You only need that toolchain if you want to run the old app side by side to compare behaviour; nothing in Offstream builds against it.

## Layout

```
src/     Offstream.Core (no UI refs) and Offstream.App (WPF)
tests/   Offstream.Core.Tests (xUnit), Offstream.UI.Tests (FlaUI)
tools/   FakeSpotify harness
build/   installer, signing, icons
docs/    modernization plan, ADRs
```

## Licence

MIT, as inherited. See the predecessor for prior copyright.
