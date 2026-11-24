# Emulator engine: architecture and implementation plan

The engine is the product. The WPF dashboard is a frontend that will host it.

## Layering
```
XboxPc.Emulation.Core          shared: physical memory bus, MMIO devices, I/O ports, fault types,
                               IClockedDevice / IInterruptController / IHardwareReport
XboxPc.Emulation.X86           Pentium III interpreter: integer, x87 (80-bit registers), MMX, SSE1; context save/restore
XboxPc.Emulation.Gpu.Nv2a      NV2A: PMC/PTIMER/PCRTC, scan-out, PFIFO DMA pusher, PGRAPH Kelvin subset (vertex programs,
                               combiners, depth) on a CPU reference renderer
XboxPc.Emulation.Audio         MCPX AC97 controller: PCM out + S/PDIF bus-master DMA, IRQ 6, IAudioSink
XboxPc.Emulation.Usb           OHCI host controller (USB0) + XID "Duke" controller function
XboxPc.Emulation.Storage       host-directory sandbox for NT device paths, FATX name rules
XboxPc.Emulation.Input         Input Engine: profiles, mapping, mouse->stick, focus/capture rules, arbiter, ProfileStore
XboxPc.Emulation.OriginalXbox  machine: memory map, XBE parser/loader, guest allocator, threads + deterministic
                               scheduler + virtual time, interrupts/DPCs, HLE kernel (82 of 371 exports, table
                               generated from nxdk's .def), EmulationSession (threaded, real-time-paced host)
tools/XboxPc.Emu.Cli           headless runner: xbe-info, run-xbe [--trace --hdd --keys --screenshot --wav], run-raw, bench, profile
XboxPc.DesktopApp              dashboard (secondary) + EmulatorWindow (frames, input, rumble, waveOut audio) + InputConfigWindow
homebrew/xpc-*                 original nxdk test programs (probe, video, fs, input, audio); XBEs are test assets
(planned) NV2A host backend     D3D12/Vulkan renderer checked against the CPU reference renderer
(planned) XboxPc.Emulation.PowerPC         Xbox 360 Xenon PPC interpreter -> recompiler
(planned) XboxPc.Emulation.Rdna2           RDNA 2 shader ISA decoder/translator (Series research)
(planned) XboxPc.Emulation.NativeX64       WHP-based native execution sandbox (One/Series research)
(planned) XboxPc.Boost                     presentation pipeline: FSR1 upscaling, DRS, frame pacing, frame generation research
```
The app hosts the machine directly through `EmulationSession` (Home -> *Run an Xbox program (.xbe)...*, or
`XboxPc.exe --run <file.xbe>`). The older `IRuntimeAdapter` library/launcher contract is not used for it yet.

## Original Xbox design decisions
- **CPU:** interpreter first (correctness, debuggability). Each instruction is verified by **differential tests
  against the host CPU** (same IA-32 semantics), the strongest oracle available without hardware. A block JIT is a
  later optimisation once coverage is broad.
- **Memory:** flat physical bus with 64 KB page dispatch; 64 MB RAM at `0x00000000` with the kernel's contiguous
  view mirrored at `0x80000000` (per public Xbox memory-map documentation). Paging/MMU is not modelled yet
  (titles run with an identity view), documented limitation.
- **Kernel:** HLE (like Cxbx-Reloaded), not LLE: no Microsoft BIOS/kernel is required or used. Imports from
  `xboxkrnl.exe` are resolved to trap addresses; executing a trap dispatches to a C# implementation or stops with
  "unimplemented kernel export #N".
- **Executables:** XBE parser hardened against malformed input. No signature checks are bypassed: the emulator
  simply does not need them, and it never modifies or re-signs titles.

## Plan, component by component (each item ships only with tests)
| # | Milestone | Done when |
|---|---|---|
| M1 | Bus + IA-32 integer core + XBE parse/load + import traps + first HLE exports + CLI | **done**, `04_M1_REPORT.md` |
| M2 | x87 FPU, MMX, SSE1 (Pentium III), CPUID/RDTSC/MSR; real nxdk XBE validation | **done**, see `09_M2_REPORT.md`; IDT delivery and paging investigated and deferred (§4 there) |
| M3 | Hardware + input: NV2A display, AC97, OHCI/XID, storage, interrupts/DPCs, Input Engine, emulator window, input screen | **done except NV2A 3D and APU**, `13_M3_REPORT.md`, `10_HARDWARE_M3.md`, `11_INPUT_ENGINE.md` |
| M4 | Kernel breadth (SEH dispatch, timers, more Io/Ob/Ps), paging/MMU, performance: lazy flags, decode cache, block JIT | next |
| M5 | NV2A PFIFO/PGRAPH pushbuffer, vertex programs, combiners -> CPU reference rasteriser -> D3D12/Vulkan | **started**: nxdk triangle sample renders; `xpc-3d-test` pixel-checked; textures/FF T&L/blending/GPU backend open, `12_NV2A_3D_ASSESSMENT.md` |
| M6 | MCPX APU (DirectSound voice processor) | APU test program output compared sample by sample |
| M7 | PC Boost on the emulator's own output: FSR 1 upscale, DRS, frame-time pacing; frame generation research with explicit generated-frame labelling | measured A/B frame-time captures |
| M8+ | Xbox 360 track (PowerPC), Series research items S-1...S-4 | per their own docs |

## Status
- M1: `04_M1_REPORT.md`. M2: `09_M2_REPORT.md` (CPU: `06_CPU_COMPATIBILITY.md`, kernel: `07_KERNEL_COMPATIBILITY.md`,
  performance & JIT plan: `08_PERFORMANCE.md`, toolchain: `05_NXDK_TOOLCHAIN.md`).
- M3: `13_M3_REPORT.md` (hardware: `10_HARDWARE_M3.md`, input: `11_INPUT_ENGINE.md`, NV2A 3D plan:
  `12_NV2A_3D_ASSESSMENT.md`, run transcripts: `reports/m3-runs.txt`).
- Generation isolation: everything Original-Xbox-specific lives in `XboxPc.Emulation.OriginalXbox`; the CPU and bus
  projects are generation-neutral. No Series X|S, Xbox One or Xbox 360 code paths exist or are implied.

## PC Boost: scope
Upscaling (FSR 1 is open source, spatial, needs no motion vectors) and dynamic resolution are feasible once the
emulator renders through its own pipeline (M5); the frame hand-off point (`EmulationSession.LatestFrame`) exists. DLSS Super Resolution / Frame Generation and FSR 3 frame
interpolation require motion vectors and depth from the renderer; for emulated games those must be reconstructed by
the emulator's GPU layer, research, not a checkbox. Until the emulator renders frames, there is nothing to boost,
and the app will not display any boost figure.
