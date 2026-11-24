# M1 report: first real emulator components (Original Xbox track)

Date: 2026-11-22. Host: AMD Ryzen 7 250, Windows 11 (build 26300), .NET 9.0.320, Release build.

## What now exists and works
| Component | Project | Evidence |
|---|---|---|
| 32-bit physical bus: RAM, mirrors, MMIO devices, 64 KB page dispatch, atomic faults on straddling writes, x86 I/O port space | `XboxPc.Emulation.Core` | `InterpreterTests` |
| IA-32 integer interpreter (Pentium III model): ModR/M+SIB, 0x66 prefix, FS/GS bases, ALU with exact flags, INC/DEC/NEG/NOT, MUL/IMUL/DIV/IDIV with #DE, shifts/rotates incl. RCL/RCR, SHLD/SHRD, BT/BTS/BTR/BTC, BSF/BSR, BSWAP, MOVZX/MOVSX, SETcc/CMOVcc/Jcc, CALL/RET/RET n/LEAVE, PUSHAD/POPAD, string ops with REP/REPE/REPNE, XCHG/XADD/CMPXCHG/CMPXCHG8B, CPUID, RDTSC, IN/OUT, HLT, INT3/INT n as traps, precise faults (EIP restored, ESP untouched) | `XboxPc.Emulation.X86` | differential + unit tests |
| XBE parser (headers, certificate, sections, library versions, retail/debug entry & thunk decoding), hardened against malformed input | `XboxPc.Emulation.OriginalXbox` | `XbeParserTests` incl. 3 000 random corruptions |
| Original Xbox machine: 64 MB RAM + kernel view at 0x80000000; NV2A, KPCR, flash mapped as *reporting* regions | same | `MachineTests` |
| Loader: maps headers/sections, resolves kernel imports to trap addresses | same | `MachineTests` |
| HLE kernel dispatch: `DbgPrint` (#8), `HalReturnToFirmware` (#49); every other import stops with its ordinal | same | `MachineTests` |
| `xpcemu` CLI: `xbe-info`, `run-xbe`, `run-raw`, `bench` | `tools/XboxPc.Emu.Cli` | manual |

## Verification method
**Differential testing against the host CPU.** 59 instruction forms are assembled into native x64 thunks and executed
on the real processor with ~4 650 inputs each (648 edge-value combinations + 4 000 seeded random cases, random
incoming flags). Result registers and every *architecturally defined* flag must match the interpreter.
~ 274 000 comparisons, **0 mismatches**. Undefined flags (e.g. AF after logic ops, OF after multi-bit shifts) are
masked per the Intel SDM, never "fixed" to match.

End-to-end: a synthetic XBE (built byte-by-byte from the public format description by `XbeBuilder`) is parsed,
loaded, executes 11 guest x86 instructions, computes 19+23 in emulated code, calls `DbgPrint` through the kernel
thunk table (output: `Hello from emulated x86: 19 + 23 = 42 [ok]`), then exits through `HalReturnToFirmware`.

Test totals: emulation engine 95/95, frontend 171/171.

## Measurements
| Metric | Value | How |
|---|---|---|
| Interpreter throughput | **51.9 MIPS** | `xpcemu bench 300000000`, mixed load/ALU/store/branch loop, Release, 1 thread |
| Rough need for real-time Original Xbox | ~400-700 MIPS | estimate: 733 MHz P-III at 0.5-1 IPC (not measured on a console) |

Conclusion: correctness first is working; **speed is ~10x short of real time**. A block recompiler (x86->x64
dynamic translation) is required before games could run at full speed, planned after instruction coverage grows.

## Known limitations (all reported at runtime, none hidden)
- No x87 FPU, MMX, SSE -> stops with `UnimplementedInstruction` ("planned M2"). Almost every real title uses x87/SSE,
  so **no real XBE will get far yet**.
- 2 of ~366 kernel exports implemented. Kernel names are only listed for implemented ordinals (not guessed).
- No paging/MMU, no IDT delivery, no interrupts/timers, single CPU thread, no segment loads.
- No GPU (NV2A accesses stop the machine), no audio, no controllers, no video output.
- Not yet validated against a real nxdk-built XBE (no nxdk toolchain on this machine). Next validation step.
- CPUID reports the Pentium III signature but advertises only implemented features (TSC, CX8, CMOV).

## Next smallest steps
1. Build nxdk "hello" samples (open-source toolchain) and use them as the lawful test corpus; record each stop reason.
2. x87 FPU with differential tests (host x87 is available in 64-bit mode as well).
3. Kernel exports needed by nxdk CRT start-up, one by one, driven by the stop reasons.
