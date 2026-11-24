# Engine audit (2026-11-22)

Question asked: *does this repository contain any Xbox emulation code?*
**Answer before this change: no.** Not one line decodes or executes code of any Xbox CPU, models any Xbox
memory map, parses any Xbox executable format, or touches any Xbox GPU command format.

Method: line counts per project, `grep` for `x86|opcode|powerpc|xbe|xex|nv2a|xenos|rdna` over `src/` (only hits:
the XPCT test-language interpreter), and reading every runtime adapter.

## What actually works (real, tested)
| Component | Lines | What it is | Emulation? |
|---|---|---|---|
| Domain / Application / Infrastructure | ~1 400 | Library CRUD, profiles, SQLite migrations, compatibility-report rules, logs with redaction, path validation | No, frontend plumbing |
| DesktopApp (WPF) | ~2 900 | Dashboard, library editor, settings, performance page | No, UI |
| Input | ~230 | XInput polling, dead zone, remapping for the app's own menus | No, host input only |
| Performance | ~520 | CPU/RAM via Win32, GPU counters via PDH, frame-time stats of the WPF window, CSV/JSON export | No, host diagnostics, not a boost |
| Runtime.Abstractions + LaunchCoordinator | ~400 | Session lifecycle contract, timeouts, cancellation | No, management layer |

## What is only UI or placeholder
| Component | Truth |
|---|---|
| `XPC Reference Test Runtime` + `fibonacci.xpct` / `primes.xpct` | A home-made 13-instruction toy language. Proves the launch plumbing only. **Not emulation and never a substitute for it.** Kept as a contract-test fixture. |
| `SeriesXsResearchAdapter`, `XboxOneResearchAdapter` | Empty adapters that throw. Labels, not engines. |
| `Xbox360Adapter`, `OriginalXboxAdapter` | Empty adapters that throw. |
| Runtime cards on Home / About | Display text for the adapters above. |
| "Render presets" | Change WPF styling of the app itself. Nothing to do with game image quality. |
| Performance page | Measures the dashboard window. It does not upscale, interpolate or speed up anything. |

## Conclusion
The repository was a launcher. The engine work starts with this change: `src/XboxPc.Emulation.*`.
The frontend is kept (it is useful and tested) but is now secondary.
