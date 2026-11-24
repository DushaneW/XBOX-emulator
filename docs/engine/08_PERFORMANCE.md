# Interpreter performance: profile and JIT-ready architecture (M2)

Host: AMD Ryzen 7 250, Windows 11, .NET 9.0.320, Release, single thread. Reproduce with
`xpcemu profile homebrew/xpc-probe/bin/default.xbe` and `xpcemu bench 300000000`.

## Measurements (2026-11-22)
| Experiment | MIPS | ns / instruction |
|---|---|---|
| `bench` mixed integer loop (M1 workload) | 51.9 (M1) | 19.3 |
| loop of 1 NOP + dec/jnz | 75.6 | 13.2 |
| loop of 8 NOPs + dec/jnz | 102.9 | 9.7 |
| -> **pure NOP (fetch + decode + dispatch + retire)** |, | **8.2** |
| -> dec + jnz pair |, | 31.5 |
| add eax,ebx | 54.1 | 18.5 |
| mov eax,[esi] | 58.9 | 17.0 |
| mov [esi+8],eax | 53.1 | 18.8 |
| imul eax,ebx | 55.5 | 18.0 |
| fld st0 / fstp st0 | 48.9 | 20.5 |
| addss xmm0,xmm1 | 50.7 | 19.7 |
| paddw mm0,mm1 | 48.3 | 20.7 |
| `PhysicalBus.Read8` alone | 1 055 M/s | 0.9 |
| real XBE (xpc-probe, 692 769 instr., median of 10 warm runs) | 35.4 | 28 |

Method note: no sampling profiler was installed; conclusions come from controlled differential timing (slopes between
workloads that differ in one component). They are hypotheses backed by measurements, not sampled stacks.

## Bottlenecks (ranked by measured cost)
1. **Per-instruction framework overhead ~ 8 ns** (~35-40 host cycles) even for NOP: `Run` loop, `Step` entry,
   prefix loop, opcode switch, retire bookkeeping. Paid by every instruction -> only removable by translating blocks
   (JIT) or by caching decoded instructions.
2. **Eager flag computation ~ 7-10 ns per ALU instruction** (dec+jnz costs ~23 ns more than two NOPs): all six
   arithmetic flags are computed on every ALU op, though most are overwritten before being read. Lazy flags
   (store op/operands/result, materialise on demand) is the cheapest next step and is fully covered by the existing
   differential tests.
3. Memory access is **not** a bottleneck (0.9 ns per bus read).
4. Real code is slower than loops (35 vs ~55 MIPS): more prefixes, ModR/M/SIB forms, x87 and kernel calls.

Real-time target ~ 400-700 MIPS (estimate for a 733 MHz Pentium III). Gap: ~ 10-20x. Interpreter tuning (lazy
flags, decode cache) can plausibly give 1.5-2x; the rest requires dynamic translation.

## JIT-ready architecture (prepared, not implemented)
- The interpreter stays the **reference implementation**; a future block JIT is validated against it by running the
  same differential suites plus lock-step co-execution (interpreter vs JIT on identical guest states).
- Block discovery: decode from an entry EIP to the first control transfer, using the same decoder. Blocks are keyed
  by physical EIP.
- Invalidation: self-modifying/loaded code must invalidate blocks. Planned mechanism: `PhysicalBus` page flags for
  "contains translated code"; RAM writes to such pages notify the block cache (the bus already dispatches per 64 KB
  page; a 4 KB code bitmap will be added when the JIT exists).
- Exits: every block ends at a control transfer, a fault, an HLE trap address or the scheduler quantum, so the
  kernel/scheduler contract (traps at `0xFE000000+4·ordinal`, precise EIP on faults) is unchanged.
- Backend choice to evaluate: .NET `DynamicMethod`/IL emission (portable, no native code writing) vs. a native x64
  emitter. Decision deferred until lazy flags and a decode cache are measured.
