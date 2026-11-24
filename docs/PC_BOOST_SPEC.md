# PC Boost: performance tooling

## Purpose
Provide useful PC-side diagnostics and supported image presentation features. It is not a magic FPS multiplier and cannot generally turn a console's 30 rendered frames into 60 native rendered frames.

## MVP
- CPU/GPU utilization where available.
- GPU dedicated memory usage where available.
- Temperature and clock readings only through documented supported APIs.
- FPS and frame-time capture for the application's own rendering path.
- CSV/JSON diagnostic export.
- Per-display resolution and refresh-rate information.
- Quality / Balanced / Performance presets for the app's own rendering.

## Later research
- Upscaling through documented APIs.
- Frame interpolation only where the rendering pipeline and licensing permit.
- Vendor-specific integrations behind capability checks.
- Latency measurement and clear distinction between rendered and generated frames.

## Required labels
Show separate values for:
- Native rendered FPS
- Generated/interpolated FPS (if applicable)
- Frame time
- Input latency (only if actually measured)
- GPU/CPU utilization

Never display a made-up FPS increase. Do not inject into protected games, bypass anti-cheat, or alter DRM-protected processes.
