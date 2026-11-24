# Roadmap

## Phase 0: Feasibility and design
Deliver:
- threat model,
- license policy,
- architecture decision records,
- target OS and hardware matrix,
- Series X|S public-information research summary,
- explicit go/no-go criteria.

Exit gate: reviewers agree the proposed scope is technically and legally defensible.

## Phase 1: Desktop shell
Deliver:
- Windows desktop app,
- controller-friendly navigation,
- library CRUD (only as a real library feature, not an emulator substitute),
- local profiles,
- settings,
- structured logging,
- database migrations,
- installer and CI.

Exit gate: reliable daily-use frontend with tests.

## Phase 2: Runtime platform
Deliver:
- versioned runtime contract,
- runtime discovery,
- capability negotiation,
- session lifecycle,
- fake runtime only in tests, never presented as an emulator,
- diagnostics and compatibility report schema.

Exit gate: contract tests and a real original test runtime fixture.

## Phase 3: First lawful compatibility implementation
Select a generation and a narrow test scope based on feasibility and legal access. The user has selected Series X|S as the research priority, but the team must not assume this makes it the best first implementation target.
Deliver a feasibility report and isolated experiments using original test programs / legally usable materials.

Exit gate: reproducible, independently reviewable execution evidence. If not feasible, document why and keep the adapter ResearchOnly.

## Phase 4: Performance and input
Deliver:
- controller support,
- keyboard/mouse mapping,
- performance telemetry,
- frame-time visualization,
- app rendering quality presets,
- supported upscaling research.

Exit gate: measured results with no misleading FPS claims.

## Phase 5: Additional generations
Evaluate Original Xbox and Xbox 360 as separate implementation tracks, with license and architecture review. Xbox One and Series X|S remain separate research efforts.

## Phase 6: Release readiness
Deliver:
- signed installer,
- update security,
- privacy policy,
- support diagnostics,
- compatibility database,
- reproducible release checklist.
