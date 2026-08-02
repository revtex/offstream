# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**Offstream** is a Windows Spotify audio recorder: the successor to **Spytify**, moving from .NET Framework 4.6.1 / WinForms to **.NET 10 + WPF**, with **ffmpeg performing every audio conversion**.

The two names are not interchangeable. "Offstream" is what is being built here; "Spytify" is the app being retired at `../spy-spotify`.

**Current state: pre-implementation.** Only planning documents exist. `docs/MODERNIZATION-PLAN.md` is authoritative for architecture, phases, and acceptance criteria — read it before proposing work, and keep it updated when decisions change.

## The app being retired is a reference, not a dependency

The predecessor is at `../spy-spotify`. Nothing here builds against it, but it is the source of truth for behaviour that took years to get right. Before writing anything that parses Spotify titles, buffers audio, assembles paths, or renders filename templates, **read the existing implementation first** — those files encode edge cases that are not obvious and not documented anywhere else.

The single most valuable asset there is `EspionSpotify/Router/AudioPolicyConfigFactory*`: per-application audio routing via the **undocumented `IAudioPolicyConfig` COM interface**, with two implementations because its vtable differs between Windows builds. It is ported verbatim. The impossibility of reproducing it safely in Go is why this is a .NET retarget rather than a Go/Wails rewrite (plan §1).

## Non-negotiable constraints

These are load-bearing; violating them breaks the app in ways that are not obvious at compile time.

- **Never enable NativeAOT.** The routing code depends on built-in COM interop (`ComImport`, `Marshal.GetDelegateForFunctionPointer`), which AOT does not support.
- **Never enable aggressive trimming.** WPF trims poorly and settings/localisation are reflection-driven. Publish self-contained and untrimmed.
- **`Offstream.Core` must not reference WPF or `System.Windows`.** The old code passed the form itself (`IFrmEspionSpotify`) into `Watcher` and `Recorder`; replace that with events or `IProgress<T>`.
- **All conversion goes through ffmpeg.** No NAudio.Lame, no bundled LAME DLLs.
- **Use `ProcessStartInfo.ArgumentList`, never a command string.** Track metadata comes from Spotify window titles and is untrusted. The argv array prevents argument injection structurally; the old app needed hand-written `CommandLineToArgvW` escaping because .NET Framework lacked `ArgumentList`.

## ffmpeg traps that have already bitten this project

Both were discovered the hard way in the predecessor. Cover them with tests, not assumptions.

- **Ogg/Opus stores tags at the _stream_ level**, not the container level. Verify with `ffprobe -show_entries stream_tags`. Using `-show_format` returns nothing and looks like the tags failed to write when they did.
- **Cover art** is a second input stream for MP3 (`-map 1:v -c:v mjpeg -disposition:v attached_pic`). ffmpeg's `METADATA_BLOCK_PICTURE` support for Ogg/Opus is weaker; TagLib# writes it correctly and is the documented fallback for that container.
- Drain `RedirectStandardError` **before** waiting on the process — a full stderr pipe deadlocks.

## Testing

The predecessor's **293 xUnit tests port across and are the safety net for the whole effort**. Phase 2's exit criterion is 293/293 green on .NET 10 *before* any behaviour changes. A failure there is a retarget defect, not an opportunity to redesign.

Beyond that: ffmpeg argv golden tests, encode-integration tests asserted with ffprobe, settings-migration fixtures, and ViewModel tests (the old UI had zero coverage). No network calls in unit tests — the old suite had one test hitting the live Last.fm API that fails offline; it becomes a fixture. Run with analyzers as errors.

## Conventions

- SDK-style projects and `PackageReference` only. The old app used `packages.config` with manual `<Compile Include>` lists, so files added on disk were silently not compiled — do not recreate that.
- Nullable reference types enabled.
- MVVM via CommunityToolkit.Mvvm source generators; no code-behind logic beyond wiring.
- Inline validation (`INotifyDataErrorInfo`), not modal dialogs.
- User-facing strings live in `.resx` with an en/fr key-parity test (ported from `TranslationTests`).
