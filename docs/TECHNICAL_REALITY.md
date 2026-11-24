# Technical reality and feasibility

## Why Series X|S is the lead research target, not the first runnable target
Modern consoles combine custom hardware configurations, operating-system services, graphics and media stacks, storage behavior, security boundaries, and title-specific assumptions. Reproducing the complete environment on Windows is substantially more difficult than creating a dashboard or translating a few graphics calls.

A general-purpose emulator would need, at minimum:
- a validated model of the target CPU execution environment,
- memory and virtual-address behavior,
- GPU command and resource semantics,
- shader translation and synchronization,
- audio, input, storage, and timing behavior,
- operating-system and runtime interfaces required by titles,
- a lawful way to obtain and use test material,
- extensive per-title debugging and regression tests.

A successful boot of a test program is not proof that commercial games are playable. A game reaching a menu is not proof of stable gameplay. A performance overlay is not proof of performance improvement.

## Practical project strategy
Build the frontend and engineering infrastructure first. In parallel, create a research branch that documents public information, evaluates feasibility, and builds isolated experiments using original test programs and legally usable assets.

Use existing open-source emulators as research references only in accordance with their licenses. Do not copy code without license review and attribution.

## Compatibility vocabulary
- Not Tested: no valid test result.
- Researching: investigation underway; no execution claim.
- Does Not Boot: reproducible failure before meaningful startup.
- Boots: reaches a defined startup milestone.
- Intro: reaches an introductory sequence.
- In-Game: reaches interactive gameplay.
- Playable: sustained play under documented test conditions.
- Verified: repeatable results across documented configurations.
- Perfect: avoid using this label unless a strict, published test standard exists.

Every report must include emulator build, test asset identity/hash, host hardware, OS/driver versions, date, steps, observed result, known issues, and evidence. Never infer compatibility from a game title or another emulator's status.
