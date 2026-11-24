# Quality gates

A pull request is not complete until relevant gates pass.

## Build
- Reproducible local build instructions.
- Warnings treated seriously; no unexplained warning suppression.
- Dependency versions pinned or centrally managed.
- Release and debug configurations both compile.

## Tests
- Unit tests for domain/application logic.
- Contract tests for every runtime adapter.
- Integration tests for database and launch orchestration.
- UI smoke tests for core navigation.
- Regression fixtures for every claimed runtime behavior.
- No tests that merely assert a hard-coded success result.

## Documentation
- Feature status: implemented / partial / research / not implemented.
- User-facing limitations.
- Reproduction steps.
- Architecture decision records for significant decisions.
- Performance claims include hardware, build, methodology, and raw measurements.

## Security
- Dependency and license review.
- Secret scanning.
- Safe path handling.
- No credentials in logs.
- No DRM or anti-cheat bypass functionality.
