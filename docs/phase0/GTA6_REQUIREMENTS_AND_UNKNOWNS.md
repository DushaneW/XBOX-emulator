# GTA VI: requirements and unknowns

**Status: Not Tested.** No claim or implication that it runs is made anywhere in this project.

- **Platform/version:** announced by the publisher for PlayStation 5 and Xbox Series X|S. Release timing and any PC
  version must be checked against the publisher's official announcements at the time of reading.
- **Lawful access:** a user would need a legitimate copy *and* a lawful way to execute it outside the console, which
  does not exist for Series X|S content (see SERIES_XS_FEASIBILITY.md §2, §7).
- **Runtime dependencies:** full Series X|S OS/runtime, GPU, storage/decompression, online services, account checks.
  Bypassing DRM, account checks or anti-cheat is forbidden.
- **Input:** standard Xbox controller layout; nothing in this project can deliver input into the title.
- **Graphics/performance:** no official PC requirements are assumed; none are invented here.
- **Evidence required for any compatibility claim:** runtime build hash, lawful content identity/hash, host hardware,
  OS/driver versions, exact procedure, video + logs, independent reproduction, reviewer confirmation. The app's
  compatibility workflow enforces this: a status above "Researching" cannot be saved without evidence and reviewer.
- **Why a dashboard or frame-interpolation overlay is not proof:** the dashboard only lists entries; an overlay only
  measures or post-processes frames something else rendered. Neither executes the game.

If a user adds "GTA VI" to the library, it is a library entry only and its compatibility shows **Not Tested**.
