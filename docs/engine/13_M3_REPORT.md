# M3 report: hardware and input

Date: 2026-11-23. Scope of M3: NV2A, audio, file system/storage, the Input Engine (mandatory), input
tests and an input test application, PC Boost preparation, tests, reports, documentation.
**The emulator is not complete. Commercial games are not expected to run.** What follows is exactly what exists.

## 1. Status per M3 item
| Item | Status | Evidence |
|---|---|---|
| NV2A display (mode set, framebuffer scan-out, vblank IRQ) | **Done** | `xpc-video-test`: pixels = formula, 30 vblanks = 499 ms |
| NV2A 3D (PFIFO, PGRAPH, shaders, D3D12/Vulkan) | **Not started** | Plan and evidence: `12_NV2A_3D_ASSESSMENT.md` |
| Audio: AC97 PCM + S/PDIF DMA, IRQ 6, host output | **Done** | `xpc-audio-test`: 76 800/76 800 frames exact, 1 600 ms |
| Audio: MCPX APU (DirectSound hardware) | **Not started** | reported as unmodelled if touched |
| File system / storage sandbox | **Done** (M3 scope) | `xpc-fs-test` + host sandbox check |
| USB OHCI host + XID controller | **Done** (no isochronous, no USB1) | nxdk USB stack enumerates and reads it unmodified |
| Input Engine (modes, bindings, mouse, focus/capture, isolation, profiles) | **Done** | 67 engine + 7 XID + 12 config-screen tests |
| Input test application inside the emulator | **Done** | `homebrew/xpc-input-test`, end-to-end tests |
| App: emulator window | **Done** | runs an XBE on its own thread, real-time pacing, frames, input, rumble, audio, pause, screenshots, full screen, live report |
| App: input configuration screen | **Done** | Xbox Controller / Keyboard and Mouse / Hybrid, rebinding, mouse & controller settings, per-game profile |
| PC Boost | **Preparation only** | frame hand-off point exists; no upscaling/frame generation (see `12_...` §PC Boost) |
| Kernel | 82 of 371 exports | grew from 57 (M2): interrupts/DPC, video (`Av*`), file I/O, `MmGetPhysicalAddress`, `MmLockUnlockBufferPages`, `KeTickCount`/`KeInterruptTime`/`KeSystemTime` data |

## 2. Real test programs (all original, nxdk, in `homebrew/`, binaries + SHA-256 in test assets)
| Program | Exercises | Result in the emulator |
|---|---|---|
| `xpc-probe` (M2) | CPU, CRT, threads, kernel basics | output identical to the same C compiled for the host |
| `xpc-video-test` | `XVideoSetMode`, framebuffer, vblank ISR -> DPC -> event | exits; frame pixel-exact; 499 ms |
| `xpc-fs-test` | D: read, refused write, listing; E: mkdir/write/read/seek/rename/space/delete | exits; output and host files exactly as expected |
| `xpc-input-test` | OHCI + XID via nxdk usbh/xid driver, reports, rumble | exits; reports follow held keys; rumble `1234/5678` received |
| `xpc-audio-test` | nxdk `hal/audio.c`: descriptors, DMA, IRQ 6, DPC refill | exits; every sample exact; 48 callbacks; 1 600 ms |
| nxdk `samples/triangle` (third party, not distributed) | pbkit 3D init | **stops**: unmodelled NV2A blocks, then kernel export #47 |
Full CLI transcripts: `reports/m3-runs.txt`.

## 3. Bugs found and fixed while bringing these programs up
- Idle loop could spin forever when a device event fell on the current cycle; the 1 ms clock rounded its next event
  down onto "now". Fixed: the clock always schedules strictly in the future and idle time always advances.
- `RootDirectory = (HANDLE)-3` (`ObDosDevicesDirectory`) in `OBJECT_ATTRIBUTES` was rejected -> nxdk `fopen("D:\...")`
  failed. Now resolved as `\??\`; rename honours `FILE_RENAME_INFORMATION.RootDirectory`.
- A test assumed `KeTickCount` (#156) was unimplemented; it now uses `XboxHDKey` (#323), which must stay
  unimplemented (key material is never provided).

## 4. Performance (host: the development PC; Release build)
| Measurement | Value |
|---|---|
| Interpreter micro-benchmarks | 48-96 MIPS (8.9 ns pure dispatch; 18-21 ns per ALU/load/store/x87/SSE/MMX op) |
| `xpc-video-test` (CPU-bound pattern fill) | ~29-30 MIPS; emulated 533 ms |
| `xpc-input-test` (300 frames, redraw on change) | ~36-40 MIPS, 1.7-1.9 s wall for 5.4 s emulated (idle skipping) |
| `xpc-audio-test` | 2.3 M instructions, 0.3 s wall for 1.6 s emulated |
| App, `xpc-video-test`, paced | 24 % of real time during the CPU-bound fill (~11 MIPS incl. UI/startup) |
A real Pentium III at 733 MHz retires several hundred million instructions per second: the interpreter is roughly
**10-20x too slow for CPU-bound real-time work**. Titles that mostly wait (vblank, audio DMA) run at full speed thanks
to idle skipping and real-time pacing; CPU-bound ones run in slow motion. The JIT plan in `08_PERFORMANCE.md`
remains the way forward; nothing was optimised before profiling.

## 5. Tests
| Suite | Count | Notes |
|---|---|---|
| `XboxPc.Emulation.Tests` | 325 | + hardware programs (3), session host (4), AC97 (6), XID (7) since the input work |
| `XboxPc.Tests` (frontend) | 183 | + input configuration screen (12) |
All pass; solution builds with 0 warnings (warnings are errors).

## 6. Not done / known limitations
NV2A 3D engine; MCPX APU; isochronous USB, USB1, hubs, memory units, other XID devices (steering wheels, light
guns); network; EEPROM content beyond blank; paging/MMU; ~289 kernel exports; JIT. The app's audio output was
verified to open the device and play the emulated stream; audible quality under CPU-bound slow motion will have gaps
(the emulator then produces less than real-time audio), reported as "ms played/dropped" in the window.
