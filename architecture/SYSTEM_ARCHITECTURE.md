# System architecture

## High-level layers

### 1. Desktop shell
Responsibilities:
- dashboard navigation,
- library presentation,
- user preferences,
- accessibility,
- controller-first interaction,
- keyboard/mouse fallback.

Suggested stack: C#/.NET 10, WinUI 3 or WPF. Choose one after a short technical spike; do not build two UI stacks.

### 2. Application services
- LibraryService: local metadata and game entries.
- ProfileService: local profiles and preferences.
- InputService: device discovery and mapping.
- LaunchCoordinator: validates launch requests and invokes a runtime.
- CompatibilityService: stores test reports and status.
- UpdateService: signed application updates only.
- DiagnosticsService: structured logs and support bundles.

### 3. Runtime contract
Define a narrow interface such as:
- GetRuntimeInfo()
- GetCapabilities()
- ValidateContent(contentDescriptor)
- PrepareSession(sessionOptions)
- StartSession()
- StopSession()
- GetSessionState()
- GetDiagnostics()

The interface must not assume every generation supports the same features. Capabilities are explicit and versioned.

### 4. Generation adapters
Separate adapters:
- Original Xbox adapter
- Xbox 360 adapter
- Xbox One research adapter
- Xbox Series X|S research adapter

Initially, later-generation adapters may report ResearchOnly / NotImplemented. That is an honest state, not a failure.

### 5. Host platform services
- graphics abstraction and presentation,
- audio output,
- controller input,
- file access sandbox,
- timing and performance counters.

### 6. PC Boost
Separate from emulation correctness. It may provide:
- host performance monitoring,
- frame-time graphing,
- resolution and display settings for the app,
- supported scaling/post-processing for content the app itself renders,
- documented integration with supported vendor APIs where permitted.

Do not promise universal frame generation for arbitrary console games. Avoid process injection or protected-process modification.

## Suggested repository layout

```
/src
  /DesktopApp
  /Application
  /Domain
  /Infrastructure
  /Runtime.Abstractions
  /Runtime.OriginalXbox
  /Runtime.Xbox360
  /Runtime.XboxOne.Research
  /Runtime.Series.Research
  /Performance
  /Input
/tests
  /Unit
  /Integration
  /Contract
/docs
/tools
```

## Dependency direction
DesktopApp -> Application -> Domain.
Infrastructure implements application/domain ports.
Generation runtimes implement Runtime.Abstractions.
Performance and Input expose explicit interfaces; no UI dependency.
Avoid circular references.
