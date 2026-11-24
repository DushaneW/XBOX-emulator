# M2 report: CPU compatibility and real XBE validation

Date: 2026-11-22. Host: AMD Ryzen 7 250, Windows 11 build 26300, .NET 9.0.320.

## 1. Real XBE validation (Priority 1)
- Toolchain: nxdk `fafbe41` + MSYS2 clang/lld 23.1.5, see `05_NXDK_TOOLCHAIN.md`.
- Test program: `homebrew/xpc-probe/probe.c`, original code written for this project (CRC32 over an xorshift
  buffer, 64-bit division through compiler runtime helpers, double-precision Basel series, single-precision
  recurrence), printing through the kernel's `DbgPrint`.
- Genuine XBE produced by nxdk's `cxbe`: 102 400 bytes, SHA-256 `4f70c728c6b6cae793784fc826c537e8b9c557c8aa779eefd71c3e090d4361a7`
  (stored as test asset `tests/XboxPc.Emulation.Tests/Assets/xpc-probe.xbe`).
- Full reproducible run log: `docs/engine/reports/xpc-probe-run.txt` (`xpcemu xbe-info` + `xpcemu run-xbe --trace`).

### Result
The XBE is parsed, loaded and executed **from its entry point to HalReturnToFirmware**:
692 769 guest instructions, 2 guest threads, 64 kernel calls, stop reason `TitleExited`.
Its output is **byte-identical** to the same C source compiled natively for the host PC:
```
XPC-PROBE begin
int crc32=614183ee last=f6e37410
u64 q_hi=02a0a046 q_lo=30b99c3a r=22
f64 basel_e9=1643934566
f32 value_e3=5187
XPC-PROBE end
```

### Everything unsupported that was encountered (in the order it was hit, and how it was resolved)
| # | Encountered | Resolution |
|---|---|---|
| 1 | kernel #192 NtCreateMutant | implemented (with release/wait) |
| 2 | kernel #335-337 XcSHA* (nxdk `rand_s` -> security cookie) | real SHA-1 implemented |
| 3 | kernel #24 ExQueryNonVolatileSetting(0xFFFF) | blank emulated EEPROM (no console data) |
| 4 | `RDMSR 0x2A` (nxdk TSC-frequency probe) | bus-ratio field modelled from public data; other bits 0 |
| 5 | kernel #46 HalReadWritePCISpace (bus 0, dev 0 fn 3, reg 0x6C) | **unmodelled hardware**: reads zero, logged; nxdk falls back to 733 333 333 Hz |
| 6 | kernel #54 InterlockedExchange (fastcall) | exposed a generator bug (fastcall exports were missing); fixed, 51-55 implemented |
| 7 | PsCreateSystemThreadEx, thread exit, TLS, KPCR/KTHREAD | thread model + scheduler implemented |
| 8 | kernel #289 RtlInitAnsiString, #203/#67 symbolic links (nxdk automount of D:) | implemented |
| 9 | kernel #236 NtWriteFile on stdout = INVALID_HANDLE_VALUE | handle validation implemented (real kernel answer) |
| 10 | kernel #49 HalReturnToFirmware(2) | clean stop |

Observed title behaviour (not an emulator defect): nxdk's `main_wrapper` closes the stdio mutexes before `exit()`
flushes the streams, so the final waits/releases return `STATUS_INVALID_HANDLE`, the trace shows this exactly.

## 2. CPU completeness (Priority 2)
See `06_CPU_COMPATIBILITY.md`. x87, MMX, SSE1 and the P-III MMX extensions are implemented and verified against the
host CPU (~1.25 M floating-point/SIMD comparisons + ~274 k integer). Bugs found by the oracle and fixed during M2:
double rounding at 24-bit precision (two forms), SNaN quieting by .NET float->double widening, x87 SNaN/QNaN
propagation rule, F2XM1 cancellation, RCPPS/RSQRTPS exceeding Intel's error bound.

## 3. Kernel expansion (Priority 4)
See `07_KERNEL_COMPATIBILITY.md`: 57/371 exports, each driven by the real XBE, each tested through the guest call path
with stack-cleanup verification.

## 4. Memory and execution (Priority 3): investigation and decisions
- **Paging.** The real kernel enables paging, but titles see a flat 32-bit space where the XBE sits at its link
  address and contiguous memory is visible at 0x80000000+. With HLE the kernel's page tables never exist in guest
  memory, so an identity view (+ the 0x80000000 alias) is sufficient for every access the probe and nxdk make.
  Paging becomes necessary for: titles that call `MmMapIoSpace`/`MmGetPhysicalAddress`-dependent paths, guard
  pages, and per-page protections. **Decision:** not implemented; the allocator already tracks allocations so page
  protections can be layered on the bus later.
- **Protection / access violations.** Unmapped and not-emulated regions (NV2A, flash, kernel traps) fault precisely
  (tested). Writes to read-only sections are *not* enforced yet (XBE section flags are parsed, not applied).
- **Interrupts / IDT.** Under HLE there is no guest IDT; hardware interrupts (timers, GPU, USB) would be delivered
  as kernel DPC/ISR callbacks registered through `KeConnectInterrupt`/`KeInitializeDpc` (imported but unused by the
  probe). A title that waits for such an event currently stops as "all threads blocked", which is the correct outcome.
- **CPU exceptions inside titles.** Precise faults stop the machine with the faulting EIP. Structured exception
  handling (fs:[0] chain dispatch) is not emulated; `RtlRaiseException` stops with the exception code.
- **State consistency.** Each guest thread has a full saved context (integer, x87/MMX, SSE, FS base); switches only
  happen at instruction boundaries or HLE calls (tested by multi-thread kernel tests).

## 5. Performance (Priority 5)
See `08_PERFORMANCE.md`. Measured: 8.2 ns fixed per-instruction overhead, 7-10 ns eager-flag cost on ALU ops,
0.9 ns bus reads; 35.4 MIPS on the real XBE. No optimisation was applied in M2 (by design); lazy flags and a decode
cache are the measured next steps, JIT architecture is specified.

## 6. Exactly what the emulator can execute today
- **Can:** nxdk-built Original Xbox homebrew whose code uses integer/x87/MMX/SSE1 instructions and only the 57
  implemented kernel exports, producing output through `DbgPrint`, verified with one real XBE end to end.
- **Cannot:** anything that draws to the screen (no NV2A, no `AvSetDisplayMode`), plays audio, reads controllers,
  opens files (no `NtCreateFile`), uses named kernel objects, structured exceptions, interrupts/DPCs/timers, or
  networking. nxdk's own `hello` sample (which calls `XVideoSetMode` and draws text) will stop at the first video
  export. **No commercial game has been run, and none is expected to run.**
- **Series X|S:** unchanged, research track only (`02_SERIES_XS_ANALYSIS.md`). Nothing in M2 is evidence of Series
  X|S, Xbox One or Xbox 360 compatibility; the Original Xbox implementation is isolated in its own projects.

## 7. Test totals
Engine 238/238 (integer + x87 + SSE + MMX differential, interpreter, XBE parser fuzzing, kernel exports, real XBE);
frontend 171/171. Release and Debug builds: 0 warnings.
