# DR-0002 — Phase 1 solution scaffold

**Date:** 2026-08-11
**Status:** Accepted
**Phase:** 1 — Solution scaffold
**Verify with:** `.\build.ps1 -Clean -Test -IncludeDesktop`

## Result

| Gate | Status |
| --- | --- |
| Solution builds on .NET 10 | ✅ 0 warnings, 0 errors, analyzers as errors |
| Tests green | ✅ **14/14** — 13 unit, 1 FlaUI desktop |
| `dotnet format --verify-no-changes` | ✅ clean |
| Self-contained untrimmed publish | ✅ succeeds |
| App actually runs | ✅ WPF-UI Fluent shell, logging to `%APPDATA%\Offstream\logs\` |

## Structure

Six projects, all authored fresh — nothing converted from the predecessor's
`packages.config` projects, so the silent "file on disk but not in `<Compile Include>`"
failure mode cannot recur.

```
Offstream.slnx
├── src/Offstream.Core           domain + audio, no UI reference
├── src/Offstream.App            WPF shell, assembly name Offstream.exe
├── tests/Offstream.Core.Tests   xUnit
├── tests/Offstream.UI.Tests     FlaUI, Category=Desktop
├── tools/Offstream.FakeSpotify  window-title harness
└── spike/Offstream.Spike        Phase 0 spike, deleted when Phase 2 lands
```

**Central package management.** All versions live in `Directory.Packages.props`; project
files carry bare `<PackageReference Include="..." />`. One place to see and bump every
dependency, which is what §8's upgrade table needs.

**Shared settings** in `Directory.Build.props`: TFM, nullable, x64, analyzers as errors,
and `PublishAot`/`PublishTrimmed` pinned off with the reason inline.

## Decisions

### D1 — `.slnx`, not `.sln`

The .NET 10 SDK now emits `Offstream.slnx` (XML) by default rather than the classic
GUID-laden format. **Kept.** It suits a project whose stated purpose is escaping brittle
project files: no GUID soup, readable diffs when a project is added.

Verified working before committing: `dotnet build`, `dotnet test`, `dotnet format` and
`dotnet publish` all accept it. `build.ps1`, CI, `.vscode/tasks.json` and the plan's §0
naming table were updated to match.

### D2 — analyzers as errors, style as a separate gate

`TreatWarningsAsErrors` with `AnalysisLevel=latest-recommended`, but
`EnforceCodeStyleInBuild=false`. Style is checked by `dotnet format` in its own CI step, so
a misplaced brace reports as a formatting diff you can auto-fix rather than as a wall of
build errors mixed in with real defects.

This immediately earned its keep: the first solution-wide build failed on **seven** real
analyzer findings in the Phase 0 spike (unused HRESULTs, a method that should be static,
LINQ on an indexable collection). All fixed.

### D3 — desktop tests are opt-in, with one filter used everywhere

FlaUI tests carry `[Trait("Category", "Desktop")]`. CI and `build.ps1` both exclude them
with `Category!=Desktop`; `build.ps1 -IncludeDesktop` opts in. Hosted runners have no
interactive session, and a UI test that cannot pass on CI is worse than one that is
explicitly skipped there.

### D4 — CI guards the AOT/trimming constraint mechanically

A second CI job publishes self-contained and then greps the project files for
`PublishTrimmed=true` / `PublishAot=true`. These settings break audio routing **at runtime,
not at compile time** (built-in COM interop is unsupported under AOT), so the failure they
cause is invisible to every other gate. Comments alone are not enough.

---

## Finding 1 — `InvariantGlobalization=true` is fatal to WPF

I set it in `Directory.Build.props` for a smaller runtime footprint. The app then built
cleanly, started, logged "Offstream starting", and **died with no window ever appearing**:

```
System.TypeInitializationException: The type initializer for
  'MS.Internal.FontCache.MajorLanguages' threw an exception.
---> System.Globalization.CultureNotFoundException: Only the invariant culture is supported
     in globalization-invariant mode. 'en' is an invalid culture identifier.
   at MS.Internal.FontCache.MajorLanguages..cctor()
   at System.Windows.Media.Typeface.CheckFastPathNominalGlyphs(...)
```

WPF's font cache constructs `CultureInfo("en")` the first time it measures text. In
invariant mode that throws, and the process exits with `0xE0434352` before rendering
anything.

Two things make this worth recording rather than just fixing:

1. **The symptom points nowhere near the cause.** The build is clean, the host starts, the
   log looks healthy, and then the process vanishes. Nothing in the visible output mentions
   globalization; the evidence was only in the Windows event log.
2. **It would have broken §7 anyway.** Offstream ships en/fr resources with a key-parity
   test. Invariant globalization is incompatible with that by definition, so this setting
   was never viable here.

`InvariantGlobalization` is now explicitly `false` with the reason inline, so nobody
re-adds it for footprint reasons.

Knock-on: turning it off enabled **CA1305**, which correctly flagged the Serilog file sink
as locale-dependent. Log files now use `CultureInfo.InvariantCulture` — diagnostics should
read the same regardless of the user's locale.

## Finding 2 — FlaUI's `Application` disposes the `Process` you hand it

`Application.Launch(...)` followed by `app.Close()` in a `finally` throws
`InvalidOperationException: No process is associated with this object`, which converts a
run whose assertions all passed into a failure. `Application.Attach(process)` has the same
effect: FlaUI takes ownership and disposes the `Process`, after which every member throws —
including from the cleanup path, where it masks the real result.

The test now captures `process.Id` immediately and does teardown by id
(`Process.GetProcessById` → `Kill(entireProcessTree: true)`). Phase 6 adds many more of
these, so the pattern is worth establishing once: **never hold a `Process` across a FlaUI
call; hold the id.**

This also masked Finding 1 for two debugging rounds — the teardown exception surfaced
instead of the timeout that was actually happening.

## Finding 3 — line endings

Files written from WSL land with LF while `.editorconfig` specifies CRLF, so
`dotnet format --verify-no-changes` fails in CI for reasons unrelated to the change under
review. Added `.gitattributes` (`* text=auto eol=crlf`, LF for `.sh`/`.yml`).

---

## Outstanding

> **Resolved 2026-08-11.** Windows 10 is out of scope, so the VM below is no longer needed
> and Phase 1 has no outstanding items. Text kept for the record.

**The Windows 10 22H2 VM is still not done.** It was carried from Phase 0 into Phase 1 and
is now carried into Phase 2. It is the only unmet exit criterion across both completed
phases, and it does not block the Phase 2 port — but it must close before Phase 7 signs
off, and the longer it waits the more code rides on an unverified assumption.

Everything needed is ready: the spike is unchanged and self-contained, and
`Offstream.Spike accept` produces the pass/fail table with no arguments.
