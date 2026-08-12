# DR-0001 — Phase 0 retarget spike

**Date:** 2026-08-01
**Status:** Accepted
**Phase:** 0 — Retarget spike
**Spike:** `spike/Offstream.Spike` (`dotnet run -- accept`)

Phase 0 asks whether the risky parts of the port survive .NET 10 *before* anything is
restructured. They do — but not the way the plan assumed. One load-bearing assumption was
wrong, and it is documented here in full because the plan was built on it.

## Verification environment

| | |
| --- | --- |
| OS | Windows 11, build **26200** (`10.0.26200.0`) |
| Runtime | .NET **10.0.10**, SDK 10.0.302 |
| Architecture | x64 |
| Elevation | **not** elevated — everything below works as a standard user |
| NAudio | 2.2.1 |
| Spotify | running, pid observed during the run |

## Acceptance run

`Offstream.Spike accept --seconds 30`, all checks green:

```
[PASS] Enumerate render endpoints         1 active
[PASS] Bind IAudioPolicyConfig            21H2 (ab3d4648-e242-459f-b02f-541c70306324)
[PASS] Locate a target process            Spotify (pid 33236)
[PASS] Route process to endpoint          → Speakers (5- Cirrus Logic XU)
[PASS] Restore default endpoint           cleared
[PASS] Mute session                       1 session(s) toggled and restored
[PASS] Capture 30s of loopback audio      11,512,320 bytes @ 48000Hz/2ch
[PASS] Captured audio is not silence      …\offstream-spike-accept-*.wav
```

The captured WAV validates under ffprobe as `pcm_f32le`, 48 kHz, 2 ch, 32-bit.

---

## Finding 1 — the routing code **cannot** be ported verbatim (assumption invalidated)

`CLAUDE.md` and plan §1/§4 said the `IAudioPolicyConfig` router is "ported verbatim" and
its logic "transfers unchanged". **That is not possible on .NET 10.** The reference
implementation depends on three things removed from the runtime in .NET 5, when built-in
WinRT support was deleted:

| Reference implementation uses | Status on .NET 10 |
| --- | --- |
| `[MarshalAs(UnmanagedType.HString)]` on the class id and the `deviceId` out-param | Removed — HSTRING marshalling unsupported |
| `[Out, MarshalAs(UnmanagedType.IInspectable)] out object factory` | Removed |
| `[InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]` on both interfaces | Casting an RCW to such an interface throws `PlatformNotSupportedException` **at cast time** |

Plan §12 rated "Routing COM breaks under .NET 10" as likelihood **Low**, reasoning that
built-in COM interop is supported and only AOT breaks it. The reasoning was right about
COM and wrong about WinRT: `IAudioPolicyConfig` is an *IInspectable*-based WinRT
activation factory, not a plain COM object. The likelihood was **certain**, not low.

### What works instead

All three are replaceable without giving up type-safe interop:

1. **Declare the interface as `InterfaceIsIUnknown`** and write `IInspectable`'s three
   methods (`GetIids`, `GetRuntimeClassName`, `GetTrustLevel`) out explicitly as the first
   three slots, so vtable offsets still line up. Every method is `[PreserveSig]`, including
   the nineteen reserved slots — the runtime must not try to read HRESULTs from signatures
   that do not really exist.
2. **Create and read HSTRINGs by hand** via `WindowsCreateString` /
   `WindowsGetStringRawBuffer` / `WindowsDeleteString` — the pattern Microsoft documents in
   *Native interoperability best practices*.
3. **Take the factory as `IUnknown`**, which is what Microsoft's own migration example for
   `RoGetActivationFactory` does.

Verified working: `SetPersistedDefaultAudioEndpoint` → `GetPersistedDefaultAudioEndpoint`
round-trips the exact device id, and `ClearAllPersistedApplicationDefaultEndpoints` clears
it. See `spike/Offstream.Spike/Routing/`.

### Consequence for the Go-versus-.NET decision (plan §1)

The Go/Wails option was rejected largely because it meant hand-writing COM vtables. It
turns out **.NET 10 also requires hand-writing the vtable** — the interface layout must be
spelled out slot by slot either way. That argument is weaker than the plan claims.

It does not reverse the decision. .NET still wins on: a compiler-checked interface
declaration rather than raw function pointers, the whole WASAPI/session/NAudio stack
carrying over untouched, and the 293-test suite. But the plan should stop describing this
code as "kept verbatim" — it is a **port with a known, documented rewrite of its
marshalling layer**, and that rewrite is now done and proven.

## Finding 2 — the two interface declarations differ only by IID, not by vtable

The project describes two implementations "because its vtable differs between Windows
builds". Diffing the reference files shows the only difference is the `[Guid]`; the method
order is byte-identical.

What actually varies is **which IID the activation factory answers to**. The spike
therefore probes: try the build-appropriate IID first, fall back to the other. This is
strictly more robust than the reference implementation's build-number check, which cannot
be right about a Windows build it has never seen.

## Finding 3 — WASAPI loopback delivers nothing at all when the endpoint is idle

Not buffers of silence — **no `DataAvailable` events whatsoever**. A first capture run
produced a valid WAV header and 0 bytes.

This matters beyond the spike: any "is capture working?" check that does not control its
own audio source cannot distinguish *broken* from *nothing was playing*. The spike now
generates a 440 Hz tone (`Audio/Tone.cs`) for the duration of the capture, making the
acceptance run self-contained and repeatable. Phase 9's fault-injection work should treat
"endpoint went idle" as a distinct state from "capture stopped".

## Finding 4 — no elevation required

Routing, session mute/volume and loopback capture all work from a standard non-elevated
process. Only VB-CABLE installation needs administrator rights (open question 9).

---

## Decisions

### D1 — .NET 10 confirmed as the target (plan open question 1, first half)

.NET 10 is **LTS, supported to 14 November 2028**. Confirmed against Microsoft Learn's
releases-and-support page. Proceed.

### D2 — WPF-UI confirmed (plan open question 1, second half)

**WPF-UI 4.3.0** ships a dedicated **`net10.0-windows7.0`** target — not merely
TFM-compatible, explicitly built for .NET 10. A probe project referencing WPF-UI 4.3.0 and
CommunityToolkit.Mvvm 8.4.2 restored and compiled clean on `net10.0-windows`, with the MVVM
source generator running.

Minor note for Phase 6: the **field-based** `[ObservableProperty]` syntax works. The newer
**partial-property** syntax failed to generate in this configuration (`CS9248`). Not a
blocker — use field-based syntax and revisit.

### D3 — bundle ffmpeg, LGPL-only, with runtime override (plan open question 2)

Confirmed the required encoders all exist in a standard build: `libmp3lame`, `libopus`,
`aac`, and native `flac`. Nothing exotic is needed, so an **LGPL-only build suffices** and
no GPL component is required for any format in plan §5.1.

> **Confirmed empirically 2026-08-12 (Phase 3).** The decision above rested on an encoder
> listing. It is now proven end to end: with `BtbN.FFmpeg.LGPL.8.1`
> (`n8.1.2`, configuration verified to contain no `--enable-gpl`), all five formats — MP3,
> WAV, Opus, FLAC, AAC — encode and tag correctly under regression suite 3, 13/13 green.
> **No GPL component is needed for anything Offstream ships.**

**Decision: bundle an LGPL-only build, with a runtime override** (bundled → `PATH` →
configured path), per the plan's recommendation. Ship ffmpeg's licence and a written source
offer. Pin and assert the version at startup.

Caution recorded for Phase 8: the build currently on this development machine is
`ffmpeg 8.1-essentials_build-www.gyan.dev`, configured `--enable-gpl --enable-version3`.
It is fine for development and **must not** become the bundled binary. `README.md` steers
new setups to `BtbN.FFmpeg.LGPL.8.1` for this reason.

### D4 — code signing

Unchanged from the plan: begin certificate procurement now, as lead time is the binding
constraint. No new information from the spike. Open question 3 (EV vs OV) still open.

---

## Gap — Windows 10 22H2 is NOT verified — ✅ **CLOSED 2026-08-11 by dropping Windows 10**

> **Superseded.** Offstream targets **Windows 11 only** (plan open question 6). The gap below
> is closed by removing the requirement rather than by satisfying it: there is no downlevel
> OS left to verify. The 21H2+ IID path is the only one that ships, and it is proven.
>
> The older IID remains in the code as insurance against Microsoft changing this undocumented
> IID again — it has happened once already — not as Windows 10 support.
>
> The original analysis is kept below because it explains why the code is shaped the way it
> is. **The secondary gap after it is still open.**

### Original analysis (superseded)

Phase 0's exit criterion asks for verification on **both** Win10 22H2 and Win11 23H2+.
Only **Windows 11 build 26200** was available. The downlevel path is therefore
**unverified**: it compiles and the IID-probing fallback is implemented, but no Windows 10
machine has exercised it.

This is the one Phase 0 criterion not met. It does not block Phase 1 (solution scaffold,
which is OS-independent), but it **must** be closed before Phase 7 signs off. Options:

- A Windows 10 22H2 VM, which is the cheapest path and also serves Phase 8's clean-VM install test.
- Deferring, and accepting that downlevel routing is untested until a user reports.

**Recommendation:** stand up the Win10 22H2 VM during Phase 1, where it is not on the
critical path, and run `Offstream.Spike accept` on it unchanged.

## Secondary gap — routing was proven, but not audibly

This machine has exactly **one** active render endpoint, so `SetPersistedDefaultAudioEndpoint`
was verified by COM round-trip (set → read back → clear), not by hearing audio move to a
second device. The interop is proven; the *audible effect* is not. Closing this needs a
second endpoint — VB-CABLE, or any second output device — and should be folded into the
same VM pass.
