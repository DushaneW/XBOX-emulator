# License policy

1. Project code license: to be chosen by the owner before first public release (recommendation: MIT or Apache-2.0
   for the frontend; a copyleft-compatible split may be needed if GPL emulator code is ever linked, see 4).
2. Allowed dependency licenses without extra review: MIT, BSD-2/3, Apache-2.0, MS-PL, zlib.
3. Require review: LGPL (dynamic linking only), MPL-2.0. Forbidden: proprietary SDKs, "non-commercial" licenses,
   anything requiring NDA access (e.g., console developer SDKs).
4. Reference emulators (xemu, GPL-2.0, Xenia, BSD-3-Clause, Cxbx-Reloaded, GPL-2.0) may be **studied**.
   Code may only be copied after license review, with attribution, and GPL code may only be used if the
   consuming component is itself GPL-compatible. Prefer launching such emulators as separate processes
   (aggregation) over linking.
5. Never bundle firmware, BIOS, keys, game images, dashboards or Microsoft artwork/branding.

## Current dependency inventory (this slice)
| Package | Version | License | Used by |
|---|---|---|---|
| Microsoft.Data.Sqlite | 9.0.x | MIT | Infrastructure |
| SQLitePCLRaw bundle (transitive) | 2.1.x | Apache-2.0 | Infrastructure |
| xunit, xunit.runner.visualstudio | 2.x | Apache-2.0 | tests |
| Microsoft.NET.Test.Sdk | 17.x | MIT | tests |

No other third-party code. The exact resolved versions are listed by `dotnet list package --include-transitive`.
