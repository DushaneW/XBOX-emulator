#!/usr/bin/env bash
# Builds every XPC homebrew test program with nxdk (MSYS2 MINGW64 shell). See docs/engine/05_NXDK_TOOLCHAIN.md.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
export NXDK_DIR="${NXDK_DIR:-/c/Users/$USER/nxdk}"
eval "$("$NXDK_DIR/bin/activate" -s)"
for d in xpc-video-test xpc-fs-test xpc-input-test xpc-audio-test xpc-3d-test xpc-texture-test xpc-ff-test xpc-ff2-test xpc-blend-test xpc-2d-test xpc-spin; do
  [ -f "$here/$d/main.c" ] || continue
  (cd "$here/$d" && make -j"$(nproc)")
done
# Unmodified nxdk sample used as third-party validation
(cd "$NXDK_DIR/samples/hello" && make -j"$(nproc)")
