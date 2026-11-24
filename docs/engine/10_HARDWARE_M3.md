# Emulated hardware: status after M3

Every device implements `IMmioDevice` (registers) and, if it has time-based behaviour, `IClockedDevice` (events on
the virtual CPU-cycle clock, 733.33 MHz). Devices raise interrupts through `IInterruptController`; the machine
delivers them to the guest ISR registered with `KeConnectInterrupt` (called on a dedicated interrupt stack) and then
runs queued DPCs. Any register a title touches that is not modelled is **reported** (`IHardwareReport`) with the
register offset and engine name, shown by the CLI and in the app's emulator window, never silently invented.

| Device | Address / IRQ | Project | Status |
|---|---|---|---|
| NV2A | `0xFD000000`, IRQ 3 | `Emulation.Gpu.Nv2a` | Scan-out + vblank; PFIFO pusher + PGRAPH Kelvin subset on a CPU reference renderer (see `12_NV2A_3D_ASSESSMENT.md`) |
| MCPX AC97 audio | `0xFEC00000`, IRQ 6 | `Emulation.Audio` | PCM out + S/PDIF DMA, interrupts. No APU |
| OHCI USB host (USB0) | `0xFED00000`, IRQ 1 | `Emulation.Usb` | Full list processing for control/interrupt/bulk; 4 root ports |
| XID controller ("Duke") | USB device | `Emulation.Usb` | Descriptors, XID requests, input report, rumble |
| Storage (disc, hard disk) | NT object paths | `Emulation.Storage` | Host-directory sandbox, FATX name rules |
| System clock | 1 ms | `OriginalXbox` (`KernelClock`) | KeTickCount / KeInterruptTime / KeSystemTime kept current |

## NV2A display engine (`Nv2aDevice`)
Modelled: PMC interrupt status/enable (`0x100`/`0x140`), PCRTC interrupt status/enable (`0x600100`/`0x600140`),
PCRTC framebuffer start (`0x600800`), PRAMDAC palette port (`0x6813C8`/`0x6813C9`), vertical blank at the mode's
refresh rate raising IRQ 3, and `ScanOut()` -> BGRA frame for 32-bit A8R8G8B8 and 16-bit R5G6B5 / A1R5G5B5
framebuffers. Display modes are set through `AvSetDisplayMode` (kernel HLE) using nxdk's mode table.
Not modelled (reported with engine name: PFIFO, PGRAPH, PFB, PTIMER, PRAMDAC, ...): everything that draws.
Verified by: `xpc-video-test`, every sampled pixel equals the program's formula; 30 vblank waits = 499 ms of
system time through ISR -> DPC -> `NtPulseEvent`.

## AC97 audio (`Ac97Controller`)
Modelled per the Intel AC'97 controller register model (as QEMU/xemu implement it): for PCM out (`+0x110`) and
S/PDIF (`+0x170`), BDBAR, CIV, LVI, SR (DCH/CELV/LVBCI/BCIS/FIFOE, write-1-to-clear), PICB, PIV, CR (RPBM, RR,
LVBIE, IOCE, FEIE); 32-entry descriptor lists; resume on LVI write; empty descriptors; global control (cold reset)
and status (codec ready, channel interrupt bits). Samples are consumed at exactly 48 kHz stereo of virtual time
(96 samples per millisecond tick); PCM-out samples go to an `IAudioSink` (WinMM `waveOut` in the app, WAV file in
the CLI with `--wav`, capture buffer in tests).
Not modelled (reported): PCM in (no capture), codec mixer registers, and the **MCPX APU** (the DirectSound voice
processor at `0xFE800000` that commercial titles use), an unrelated, much larger device.
Verified by: `xpc-audio-test` (nxdk `hal/audio.c`), all 76 800 stereo frames equal the program's formula, 48
buffers / 48 DPC callbacks, 1 600 ms of virtual time; register-level unit tests.

## OHCI USB host controller (`OhciController`)
OHCI 1.0a operational registers `0x00-0x60` and 4 root-hub ports (connect/enable/reset/suspend/power, change bits,
write-1-to-clear semantics), HCCA (frame number, done head), periodic (interrupt table), control and bulk ED/TD
lists, general TDs with two-page buffers, short packets with buffer rounding, data toggle, STALL -> halted ED,
done queue with WDH, SF/RHSC interrupts with MIE masking. A frame runs every 1 ms of virtual time.
Not modelled (reported): isochronous TDs, the second controller USB1 (`0xFED08000`), and register `0x20C` that
nxdk's driver reads (outside the OHCI operational register set; returns 0).
Verified by: `xpc-input-test`, nxdk's USB stack (usbh core + xid driver) enumerates, configures and reads the
controller through these registers, unmodified.

## XID controller (`XidGamepad`)
Device descriptor (VID `045E`, PID `0202`), configuration with one interface (class `0x58`, subclass `0x42`), EP1
IN interrupt (20-byte report), EP2 OUT interrupt (6-byte rumble). Standard requests (GET_DESCRIPTOR, SET_ADDRESS
applied after the status stage, SET/GET_CONFIGURATION, GET_STATUS, SET_INTERFACE, CLEAR/SET_FEATURE), XID requests
(XID descriptor `0xC1/6/0x4200`, GET_CAPABILITIES in/out, GET_REPORT, SET_REPORT for rumble). Reports are sent only
when the state changed (NAK otherwise), as the hardware does. Each of the four ports carries a controller whenever
the Input Engine reports that port connected (hot-plug -> OHCI connect-status change).

## Storage (`VirtualStorage` + `KernelFiles`)
`\Device\CdRom0` -> the XBE's directory (read-only; writes fail with `STATUS_MEDIA_WRITE_PROTECTED`), 
`\Device\Harddisk0\Partition1` -> a writable host directory (per title in the app:
`<data>\harddisk\<TitleID>`). Path resolution never escapes the mapped directory; FATX name rules (42 characters,
allowed characters) are enforced. Kernel: `NtCreateFile/NtOpenFile` (all create dispositions), `NtReadFile`,
`NtWriteFile`, `NtQueryInformationFile`, `NtSetInformationFile` (position, end of file, rename, delete, basic info),
`NtQueryDirectoryFile` with wildcards, `NtQueryVolumeInformationFile`, `NtQueryFullAttributesFile`, `\??\` symbolic
links. Verified by `xpc-fs-test` plus a check of the host sandbox contents afterwards.

## Interrupts and time
`KeConnectInterrupt`/`KeDisconnectInterrupt`, `HalGetInterruptVector`, `KeInitializeDpc`/`KeInsertQueueDpc`/
`KeRemoveQueueDpc`, `KeRaiseIrql`/`KfLowerIrql`. ISRs run on a separate interrupt stack; DPCs run after all pending
ISRs. Idle time (all threads waiting) is skipped to the next device event, so a title waiting for vblank costs no
host CPU. `CycleLimit` lets a host (the app's `EmulationSession`) pace virtual time to real time.
