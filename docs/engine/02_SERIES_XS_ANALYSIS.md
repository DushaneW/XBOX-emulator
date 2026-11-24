# Xbox Series X|S: technical feasibility (engine view)

This complements `docs/phase0/SERIES_XS_FEASIBILITY.md` (public hardware figures) with what it means for an
emulator design. Statements marked **[fact]** are public; **[inference]** are engineering conclusions; **[unknown]**
cannot be verified without material we may not obtain.

## 1. Component by component

### CPU: the easy part, but for an unusual reason
- **[fact]** 8 x AMD Zen 2 cores, x86-64. The Windows host is also x86-64.
- **[inference]** An instruction interpreter or recompiler is *not* the right tool: guest code can run natively on
  the host CPU. The proven model is the one Wine/Proton use for Windows games on Linux: run the title's machine code
  natively and re-implement the OS API it calls (HLE). Isolation can come from the documented **Windows Hypervisor
  Platform** (WHP) API, which lets a user-mode process create a VM partition and trap system calls / page faults.
- **[inference]** Host requirements: Zen 2 feature set (AVX2, BMI2, no AVX-512 dependency) is available on any recent
  desktop CPU, so instruction-set gaps are unlikely to be the blocker.
- Our IA-32 decoder (this repo) is still useful for the x86-64 path: fault decoding, patching, analysis. A 64-bit
  decoder extension is planned, but an x86-64 *interpreter* for Series titles would be pointless for performance.

### Memory
- **[fact]** 16 GB GDDR6 unified (10 GB fast + 6 GB slower) on Series X; 10 GB on Series S. CPU and GPU share it.
- **[inference]** On PC, CPU-visible and GPU-visible memory are separate. Titles that write GPU resources through CPU
  pointers need either resizable-BAR/host-visible heaps or shadow copies with dirty tracking. This is one of the
  biggest performance risks; it is solvable in principle (Xenia solves an analogous problem for Xbox 360).

### GPU: the hardest *technical* part
- **[fact]** Custom RDNA 2 GPU, DirectX 12 Ultimate feature level. Titles ship **precompiled GPU shader binaries**
  for the console GPU, not portable DXIL.
- **[fact]** AMD publishes the *RDNA 2 Instruction Set Architecture* reference guide.
- **[unknown]** The console command-buffer format (PM4 packets as used by the console D3D12 variant), descriptor
  layouts, and any console-specific shader-ISA extensions are not publicly specified.
- **[inference]** A host translation path RDNA2-ISA -> DXIL/SPIR-V is technically conceivable and **is lawfully
  researchable on its own** from AMD's public ISA document using shaders we compile ourselves for AMD GPUs on PC.

### System software
- **[fact]** The console runs a hypervisor with a game partition (Microsoft public talks, Hot Chips 2020).
- **[fact]** Titles use the Microsoft Game Development Kit (GDK). The PC GDK and its documentation are public; the
  console GDK is NDA-only.
- **[unknown]** Exact console-only API surface and kernel/system-call interface.

### Executables and content
- **[fact]** Retail titles are distributed as encrypted, signed packages bound to keys held in the console's security
  hardware.
- **[inference]** There is **no lawful way** for this project to obtain decrypted Series X|S title executables.
  Without them, a Series X|S execution path cannot be tested end-to-end. Obtaining them would require defeating
  platform security, which this project will not do.
- Self-built test titles would require the NDA console GDK; even then, its license terms would govern their use.

## 2. What can be implemented and tested now (lawfully)
| Work item | Testable with | Status |
|---|---|---|
| x86 decoder shared with Original Xbox | own machine code, differential vs host CPU | **started (IA-32)** |
| RDNA 2 shader ISA decoder -> later translator | AMD public ISA doc + shaders we compile for AMD PC GPUs | planned (S-1) |
| WHP native-execution sandbox for x86-64 guest code with syscall trapping | own x86-64 test programs | planned (S-2) |
| Unified-memory model / CPU↔GPU coherency prototype | synthetic workloads | planned (S-3) |
| PC GDK API surface inventory (public docs) | documentation only | planned (S-4) |

## 3. What cannot be verified today
- Booting or running any retail Series X|S title. **Cannot be demonstrated**, no lawful executables.
- Correctness of console-specific GPU command or API behaviour, no specification, no test corpus.
- Any performance claim for Series titles.

## 4. Biggest technical problems (ranked)
1. Lawful test corpus does not exist -> no end-to-end validation possible (blocker, not technical).
2. GPU: console command buffers + native RDNA2 shader binaries -> host API translation.
3. Unified memory semantics vs PC discrete memory.
4. Undocumented OS / API surface.
5. Timing and synchronisation between CPU threads, GPU queues and the OS.

## 5. Consequence for the roadmap
Series X|S stays the long-term research direction. The **first runnable emulation track is Original Xbox**, because:
its CPU is IA-32 (shares the decoder with the Series path), its executable format (XBE) is publicly documented, and
**lawful test executables exist**: homebrew built with the open-source **nxdk** SDK. Every component built for it
(bus, scheduler, HLE dispatch, GPU front-end pattern, presentation, PC Boost hooks) carries over to later
generations.
