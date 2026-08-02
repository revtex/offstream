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

## Prerequisites

- Windows 10 22H2 or Windows 11
- **.NET 10 SDK** — not currently installed on this machine (only a runtime is present)
- ffmpeg — bundling decision pending, see plan §5.1
- Visual Studio 2022+ or Rider, or `dotnet` CLI

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
