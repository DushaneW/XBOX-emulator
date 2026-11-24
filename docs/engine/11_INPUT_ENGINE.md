# Input Engine

Project `XboxPc.Emulation.Input` (no UI or OS dependencies, fully unit-tested) plus the host glue in the desktop
app (`EmulatorWindow`, `InputConfigWindow`).

## Data path
```
host keyboard / mouse (WPF events, relative mouse via cursor re-centring while captured)
host controllers (XInput 1.4, slots 1-4)                     ─┐
                                                               ├─► HostInputFrame ─► InputEngine.Update (profile rules)
                                                               │                         │  4 x XboxGamepadState
                                                               │                         ▼
                                     XidGamepad (USB function) ◄─ machine reads port state when the title polls
                                                               │                         │  20-byte XID report
                                                               ▼                         ▼
                                     OHCI controller ─► nxdk / title USB driver ─► game
rumble: title ─► XID OUT report ─► InputEngine.SetRumble ─► XInputSetState (app)
```
All engine calls happen on the emulation thread (`EmulationSession.Post`), so the engine, the USB controller and
the machine never need locks. The UI only collects host events.

## Modes (per profile)
| Mode | Port 1 | Ports 2-4 |
|---|---|---|
| Xbox Controller | physical controller 1 | physical controllers 2-4 |
| Keyboard and Mouse | keyboard + mouse (always connected, no controller needed) | physical controllers 2-4 ignored |
| Hybrid | controller 1 **and** keyboard/mouse merged (buttons OR-ed, analog max, larger stick deflection wins) | physical 2-4 |

## Mapping rules
- Every Xbox control (A, B, X, Y, Black, White, both triggers, D-pad, Start, Back, stick clicks, 8 stick
  directions) can be bound to any number of keys or mouse buttons (`MouseLeft/Right/Middle/X1/X2`, `WheelUp/Down`).
  A key drives exactly one control (validation rejects duplicates; the config screen moves a re-used key).
- Keyboard stick directions are normalised on diagonals (optional).
- Mouse -> stick: speed (px/s) x sensitivity / 2000 -> output dead zone offset -> response curve (exponent) ->
  inversion X/Y; the last deflection is held for a configurable time (0-500 ms) to bridge polling gaps.
- Physical controllers: radial stick dead zone with rescaling, trigger threshold, shoulder buttons -> White/Black
  (configurable), rumble on/off.

## Focus, capture and isolation
- Nothing reaches the game unless the emulator window is focused; losing focus releases capture and clears every
  held key/button (no stuck input).
- Keyboard reaches the game while focused (except in Controller mode). Mouse motion and buttons only while the
  mouse is captured: click the picture to capture (that click is not sent to the game); the release key (default
  `RightCtrl`, configurable, cannot be bound) releases it.
- Emulator shortcuts are reserved and never reach the game or a binding: `Pause` (pause/resume), `F11` (full
  screen), `F12` (screenshot).
- Dashboard vs game: `InputArbiter`, while an emulator window is active the dashboard's controller navigation is
  disabled (`MainWindow` checks `DashboardMayHandle`); keys pressed in the emulator window are marked handled so
  WPF never turns them into focus navigation or menu activation.

## Profiles and per-game assignment
`ProfileStore` keeps profiles as validated JSON files (`<data>\input-profiles\*.profile.json`, atomic write) and a
title-ID -> profile map. The emulator window loads the profile assigned to the running XBE's title ID (else
"Default"). The **Input...** screen edits mode, bindings (Rebind = press a key or mouse button, Escape cancels),
mouse sensitivity/curve/dead zone/hold/inversion and stick target, controller dead zone/trigger threshold/rumble,
the release key, and "use this profile for <game>". Save validates and applies the profile to the running session.

## Tests
- `tests/XboxPc.Emulation.Tests/Input/InputEngineTests.cs` (67): XID report layout, every controller button and
  axis, triggers, dead zones, every keyboard binding, diagonals, rebinding, mouse sensitivity/inversion/dead
  zone/curve/hold, hybrid merging, multiple controllers, disconnection, focus loss, capture/release, reserved keys,
  controller-mode isolation, rumble, arbiter, invalid profiles, profile persistence, per-game assignment, corrupt files.
- `Usb/XidGamepadTests.cs` (7): descriptors, address timing, XID descriptor, report-on-change, rumble paths, stall/reset.
- `OriginalXbox/HardwareProgramTests.Input_test_...` and `EmulationSessionTests.Posted_input_...`: end to end through
  nxdk's real USB stack inside the emulator (press -> report `a=255 ly=32767`, release -> neutral report, rumble
  `1234/5678` back to the engine).
- `tests/XboxPc.Tests/Unit/InputConfigViewModelTests.cs` (12): configuration screen logic.

## Input test application
`homebrew/xpc-input-test` (original, nxdk): enumerates the controller through OHCI, prints and draws every report
(buttons, analog buttons, triggers, sticks), sends rumble when A is first pressed. Run it in the app with
`XboxPc.exe --run homebrew\xpc-input-test\bin\default.xbe` or **Home -> Run an Xbox program (.xbe)...**.
