# ADR-001: Desktop UI framework and target runtime

- Status: Accepted
- Date: 2026-11-22
- Decision owners: project lead

## Context
The shell must be a Windows 11 desktop app, controller-first, with keyboard focus navigation, dark theme, and
UI smoke tests. The dev host has only the .NET 9 SDK (no .NET 10, no Windows App SDK workload, no MSVC).

## Decision
**WPF on `net9.0-windows`**, MVVM without third-party MVVM libraries. Retarget to `net10.0-windows` via
`Directory.Build.props` when the .NET 10 SDK is installed.

## Alternatives considered
1. **WinUI 3 (Windows App SDK)**, modern Fluent look, but needs the Windows App SDK packages/runtime, MSIX or
   bootstrapper deployment, and has weaker headless test support. More moving parts for no requirement we have.
2. **Avalonia**, cross-platform (not required), extra dependency footprint.
3. **WPF**, in-box with the SDK, mature focus/keyboard navigation (`KeyboardNavigation`, `FocusVisualStyle`),
   can be instantiated in STA tests for smoke tests, no extra deployment runtime beyond .NET Desktop.

## Consequences
+ Zero extra UI dependencies; simple xcopy/installer deployment; testable view-models.
− Not Fluent/WinUI-native visuals; we style it ourselves. Migration to WinUI 3 later would rewrite views only
  (view-models and everything below stay).
