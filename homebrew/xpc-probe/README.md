# xpc-probe: original Original Xbox homebrew test program

Written for the XBOX PC emulator project. Built with the open-source nxdk SDK (no Microsoft code).
It computes values at run time and prints them through the kernel's `DbgPrint`; the same source compiled for the
host PC (`-DXPC_HOST`) produces the expected output used as the emulator's oracle.

Build (MSYS2 MINGW64 shell): `NXDK_DIR=/c/Users/<you>/nxdk ./build.sh`, see `docs/engine/05_NXDK_TOOLCHAIN.md`.
Run in the emulator: `xpcemu run-xbe bin/default.xbe --trace`.

The committed test asset (`tests/XboxPc.Emulation.Tests/Assets/xpc-probe.xbe`) contains this program linked with nxdk
libraries (MIT / CC0-1.0 / Apache-2.0 / NCSA licensed; see nxdk `LICENSES/`).
