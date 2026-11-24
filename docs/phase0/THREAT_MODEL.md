# Threat model (phase 0/1)

Scope: the Phase 1 desktop app. **No network features exist**, no accounts, telemetry, updates, or metadata
downloads. Re-run this model before adding any of them.

## Assets
User library database, user-selected file paths, cover-art images, logs, diagnostic exports, runtime adapters.

## Trust boundaries & threats
| Boundary | Threat | Mitigation in this slice | Test |
|---|---|---|---|
| User-entered paths | Path traversal, device paths (`\\.\`, `\\?\`), reserved names (`CON`, `NUL`...), relative paths | `SafePath.Validate`: absolute only, no `..` segments, no device prefixes, no reserved names, invalid chars rejected | `SafePathTests` |
| Cover-art files | Non-image file with image extension, huge files | Extension allow-list + magic-byte check + 10 MB cap; decoded by WPF with failures shown; never executed | `ImageFileValidatorTests` |
| SQLite database | SQL injection | Parameterized queries only | repository tests with SQL-like names |
| Logs / exports | Secret leakage, Windows user name in paths | `LogRedactor` masks the user-profile path and `password=`/`token=`/`secret=`-style pairs | `LogRedactorTests` |
| Runtime adapters | Arbitrary code loading | Adapters are compiled in; **no dynamic plugin loading exists**. Future loading requires Authenticode verification (ADR-005) | n/a |
| Archives | Zip-slip | No archive handling exists. `SafePath.IsWithin` is provided for future extraction | `SafePathTests` |
| Reference test programs | Malformed/hostile program file | Interpreter has instruction budget, stack/register bounds, size cap, cancellation; no host I/O instructions | `ReferenceRuntimeTests` |
| Updates | Malicious update | No updater exists; ADR-005 defines requirements | n/a |
| Accounts | Credential theft | No account features. Passwords never collected; future OAuth tokens -> DPAPI / Credential Manager | n/a |

## Explicitly forbidden features
DRM / secure-boot / anti-cheat / authentication bypass, process injection into games, key extraction, downloading
games/firmware.
