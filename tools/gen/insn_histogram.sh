#!/usr/bin/env bash
# Disassembles an nxdk-linked PE (before cxbe conversion) and prints an instruction-mnemonic histogram.
# Usage (MSYS2 MINGW64 shell): tools/gen/insn_histogram.sh homebrew/xpc-probe/main.exe
set -euo pipefail
out="${1%.exe}.disasm.txt"
llvm-objdump -d --no-show-raw-insn "$1" > "$out"
echo "instructions: $(grep -cE '^ +[0-9a-f]+:' "$out")"
echo "fs-relative: $(grep -c 'fs:' "$out" || true)"
grep -E '^ +[0-9a-f]+:' "$out" | awk -F'\t' '{split($2,a," "); print a[1]}' | sort | uniq -c | sort -rn
