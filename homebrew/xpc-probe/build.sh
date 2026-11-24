#!/usr/bin/env bash
# Builds the XPC probe with nxdk (run from an MSYS2 MINGW64 shell). See docs/engine/05_NXDK_TOOLCHAIN.md.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
export NXDK_DIR="${NXDK_DIR:-/c/Users/$USER/nxdk}"
eval "$("$NXDK_DIR/bin/activate" -s)"
cd "$here"
make -j"$(nproc)"
# Host oracle: same source compiled for the PC.
clang -O2 -DXPC_HOST -o probe_host.exe probe.c
./probe_host.exe > expected_output.txt
echo "XBE: $here/bin/default.xbe"
