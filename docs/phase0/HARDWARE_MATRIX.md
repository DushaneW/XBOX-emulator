# Target OS and hardware matrix

| Item | Supported (Phase 1) | Notes |
|---|---|---|
| OS | Windows 11 x64 (22H2+) | Windows 10 x64 probably works (WPF/.NET 9) but is not tested |
| Arch | x64 | ARM64 not tested |
| Runtime | .NET 9 Desktop Runtime | .NET 10 planned (ADR-001) |
| GPU | Any DirectX 11-capable GPU (WPF render tier 2) | App only renders its own UI |
| Controller | XInput devices (Xbox-compatible pads) | via `xinput1_4.dll`, max 4 slots |
| RAM | 4 GB+ | App footprint is small |

## Reference development host (where tests were run)
- CPU: AMD Ryzen 7 250 w/ Radeon 780M Graphics
- GPU: NVIDIA GeForce RTX 5060 Laptop GPU (driver 32.0.16.2101) + AMD Radeon 780M (32.0.21028.2002)
- RAM: 15.3 GB usable
- OS: Windows 11 Home, build 10.0.26300
- SDK: .NET 9.0.320
