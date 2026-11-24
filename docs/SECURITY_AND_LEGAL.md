# Security, privacy, and legal engineering rules

1. Do not bypass DRM, secure boot, authentication, anti-cheat, or platform security controls.
2. Do not distribute copyrighted game images, firmware, keys, proprietary SDKs, or console system files.
3. Do not scrape or reuse Xbox/Microsoft branding and artwork without permission. Use original branding and user-provided/licensed artwork.
4. Microsoft account login, if ever added, must use official OAuth flows and minimum scopes. Never collect passwords.
5. Store tokens using Windows Credential Manager / DPAPI-backed secure storage, with clear user consent.
6. Keep user content paths local and user-selected. Do not silently upload game files.
7. Do not execute arbitrary files from downloaded metadata or artwork.
8. Validate archive paths and reject path traversal.
9. Sign release binaries and updates; verify signatures before installation.
10. Produce a threat model before enabling network features.
11. Keep telemetry opt-in, minimal, and documented.
12. Review licenses of every dependency and any reference emulator code before use.
