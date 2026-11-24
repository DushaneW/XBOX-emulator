# Xbox Series X|S: feasibility study

Status of the Series X|S adapter after this study: **ResearchOnly / Not Implemented.**
Nothing in this repository executes Xbox Series X|S code.

## 1. Publicly disclosed characteristics
Sources: Microsoft, "Xbox Series X: A Closer Look at the Technology Powering the Next Generation" (Xbox Wire,
16 Mar 2020); Microsoft presentation "Xbox Series X SoC" at Hot Chips 32 (Aug 2020); Xbox Wire Series S
announcement (Sep 2020). Values below are as published by Microsoft; re-verify against those primary sources
before quoting externally.

| | Series X | Series S |
|---|---|---|
| CPU | 8 x custom AMD Zen 2, 3.8 GHz (3.66 GHz with SMT) | 8 x Zen 2, 3.6 GHz (3.4 GHz with SMT) |
| GPU | custom RDNA 2, 52 CUs @ 1.825 GHz, ~12.15 TFLOPS FP32 | 20 CUs @ 1.565 GHz, ~4 TFLOPS |
| Memory | 16 GB GDDR6: 10 GB @ 560 GB/s + 6 GB @ 336 GB/s | 10 GB GDDR6: 8 GB @ 224 GB/s + 2 GB @ 56 GB/s |
| Storage | 1 TB custom NVMe, 2.4 GB/s raw, hardware decompression ("Velocity Architecture") | 512 GB, same architecture |
| Graphics features | DirectX 12 Ultimate class: DXR ray tracing, VRS, mesh shaders, sampler feedback streaming | same |
| System software | Hypervisor with separate game and system partitions (described publicly by Microsoft) | same |

## 2. Host-PC differences that matter
- **CPU**: Zen 2 x86-64 is close to PC CPUs, so instruction emulation is *not* the hardest part; the hard parts are
  the OS/runtime environment, the GPU, and security.
- **Unified memory** (CPU and GPU share one GDDR6 pool) vs. PC split system RAM / VRAM over PCIe: coherency,
  residency and bandwidth assumptions differ.
- **GPU**: titles ship precompiled shaders and command streams for a fixed GPU and a console-specific D3D12 variant.
  These have no public specification; a PC GPU from any vendor would need translation.
- **Storage/decompression**: hardware decompression blocks and DirectStorage-style I/O; PC equivalents exist
  (DirectStorage for Windows) but console formats/behaviour are not publicly specified.
- **OS**: the console OS, its system services, and title runtime libraries are proprietary and not available
  for lawful redistribution or use outside the console.
- **Security**: content is encrypted and signed; execution requires keys held in console hardware. Obtaining them
  would require defeating platform security, **permanently out of scope** (SECURITY_AND_LEGAL.md rule 1).

## 3. Abstractions an emulator would need
CPU execution environment (likely near-native/virtualized), memory/virtual address model, GPU command processor
+ shader translation + synchronization, audio, input, storage/decompression, timing, and a reimplementation of the
OS/runtime interfaces titles call. Each is a multi-year workstream for a large team.

## 4. Uncertainty register
| # | Unknown | Impact | Resolvable lawfully? |
|---|---|---|---|
| U1 | Console GPU command format & shader ISA details beyond public RDNA 2 docs | Critical | Partially (AMD publishes RDNA ISA docs; console deltas unknown) |
| U2 | Title-facing OS/runtime API surface | Critical | No public spec |
| U3 | Executable/package format and encryption | Critical | No, encrypted, keyed to hardware |
| U4 | Lawful source of unencrypted test titles | Critical | Only self-built titles via the official developer program (NDA; bound by its terms) |
| U5 | Hypervisor/partition behaviour titles rely on | High | No |
| U6 | Timing/performance characteristics | Medium | Only from public statements |

## 5. What can be tested without proprietary material
- The **platform**: dashboard, library, profiles, runtime contract, lifecycle, diagnostics (implemented in this repo).
- Generic building blocks: execution of *our own* programs, D3D12 presentation on the host, frame-time
  measurement. None of these are "Series X|S emulation".
- Public-spec research: experiments based on AMD RDNA 2 ISA documentation using original shaders.

## 6. Proof-of-concept proposals (original programs only)
1. **PoC-A, Contract proof** *(implemented: `Runtime.Reference`)*: a home-grown deterministic test program run by an
   original reference runtime through the full LaunchCoordinator lifecycle (validate -> prepare -> start -> stop ->
   diagnostics). Proves the platform plumbing, **not** Xbox compatibility.
2. **PoC-B, Presentation**: D3D12 swap chain owned by a runtime process, presented in the shell, with measured
   frame times.
3. **PoC-C, Shader research**: compile original HLSL to DXIL and compare against RDNA ISA documentation; no console
   binaries involved.

## 7. Go / no-go criteria for *implementing* a Series X|S runtime
Go requires **all** of:
- a lawful, documented source of unencrypted test executables usable outside a console;
- a public or lawfully obtained specification of the title-facing runtime API sufficient to run them;
- no step requiring key extraction, signature bypass, or other circumvention;
- legal review sign-off.

**Current verdict: NO-GO for implementation; GO for platform work and public-spec research.**
Recommendation: if a runnable compatibility target is wanted, evaluate Original Xbox or Xbox 360 via existing
open-source emulators as *external, separately-licensed* runtimes (Phase 5), with users supplying their own
lawfully obtained content.

## 8. Explicit statement
This project **cannot currently demonstrate** execution of any Xbox Series X|S, Xbox One, Xbox 360 or Original Xbox
software. No compatibility, FPS, or "boots" claim is made for any title.
