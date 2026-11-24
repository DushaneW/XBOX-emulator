# ADR-002: Local persistence

- Status: Accepted
- Date: 2026-11-22

## Context
Library entries, profiles, per-game settings, input mappings and compatibility reports must persist locally,
survive crashes, and evolve via migrations.

## Decision
**SQLite via `Microsoft.Data.Sqlite` (MIT)**, hand-written SQL with parameters, and a small ordered migration
runner tracking `PRAGMA user_version`. Each migration runs in a transaction. Database file:
`%LOCALAPPDATA%\XboxPc\xboxpc.db`. WAL journal mode, foreign keys on.

## Alternatives considered
1. **EF Core**, migrations tooling, but heavy dependency and code-gen for ~6 tables.
2. **JSON files**, no transactions, poor concurrency, ad-hoc migrations.
3. **LiteDB**, document store; less standard tooling, smaller ecosystem.

## Consequences
+ One small dependency, transparent SQL, easy integration tests against a temp file.
− Mapping code is hand-written; must be covered by repository tests.
