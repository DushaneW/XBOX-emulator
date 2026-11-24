# nxdk toolchain: lawful Original Xbox homebrew (reproducible setup)

nxdk is an open-source Original Xbox SDK (github.com/XboxDev/nxdk). It contains no Microsoft code. Its components
are under MIT, CC0-1.0, Apache-2.0, NCSA and a Cg license (see `nxdk/LICENSES/`). Programs built with it are
lawful test material for this emulator.

## What was installed on the development host (2026-11-22)
| Component | Version | How |
|---|---|---|
| MSYS2 | winget package `MSYS2.MSYS2` | `winget install --id MSYS2.MSYS2 -e` -> `C:\msys64` |
| clang / lld / llvm | 23.1.5 (MSYS2 MINGW64) | `pacman -S mingw-w64-x86_64-clang mingw-w64-x86_64-lld mingw-w64-x86_64-llvm` |
| make, cmake, bison, flex, git, gcc | MSYS2 current | `pacman -S make cmake git bison flex mingw-w64-x86_64-gcc` |
| nxdk | commit `fafbe41b48fa00a31b79dd49ad1f86a30adcdd9e` (2026-11-12) | `git -c core.longpaths=true clone --recursive --depth 1 --shallow-submodules https://github.com/XboxDev/nxdk.git C:\Users\<you>\nxdk` |

nxdk is cloned to a short path on purpose: one submodule (libcxx tests) exceeds Windows' 260-character path limit
when placed inside this project's long path.

## Building the test program
From an MSYS2 **MINGW64** shell:
```
cd homebrew/xpc-probe
NXDK_DIR=/c/Users/<you>/nxdk ./build.sh
```
Outputs: `bin/default.xbe` (the XBE), `main.exe` (the PE before XBE conversion, used for disassembly),
`expected_output.txt` (host oracle: the same C source compiled natively for the PC).

The first build compiles all nxdk libraries (a few minutes). Toolchain warnings seen: `lld: warning: .edata=.rdata:
already merged into .edataxb` (harmless, from nxdk's linker script).

## Instruction inventory of a build
`tools/gen/insn_histogram.sh homebrew/xpc-probe/main.exe` disassembles with `llvm-objdump` and prints a mnemonic
histogram. For the probe: 22 415 instructions; FPU/SIMD used: `fld fldl flds fld1 fldz fstpl fildl fistpl fmul
fmulp faddp fdivp fldcw fnstcw`, `movss addss mulss divss cvtsi2ss cvttss2si sfence`; system: `rdtsc rdmsr cli hlt`;
12 `fs:`-relative loads (NT_TIB.StackBase for stack checks).

## Kernel export table
`tools/gen/gen_kernel_exports.py <nxdk>/lib/xboxkrnl/xboxkrnl.exe.def src/XboxPc.Emulation.OriginalXbox/KernelExports.g.cs`
regenerates the ordinal -> (name, calling convention, argument bytes) table (371 exports, CC0 source).
