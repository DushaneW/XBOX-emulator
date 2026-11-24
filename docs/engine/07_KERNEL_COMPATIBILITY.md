# Kernel (xboxkrnl HLE) compatibility report: M2

57 of 371 exports exist (54 functions, 3 data). Every one was added because the real nxdk-built XBE called it, or
because it is the counterpart of one it called (e.g. release for create). Names, calling conventions and argument
sizes come from nxdk's `xboxkrnl.exe.def` via a generator; stack cleanup is derived from that table, never written
by hand. Each export is tested through the real guest call path (`KernelTests`).

## Implemented
| # | Export | Semantics / limits |
|---|---|---|
| 8 | DbgPrint (cdecl) | printf subset `%s %c %d %i %u %x %X %p %%`, flags `-`/`0`, width; output captured |
| 24 | ExQueryNonVolatileSetting | only 0xFFFF (whole EEPROM); emulated EEPROM is blank, no serials, MAC or keys |
| 46 | HalReadWritePCISpace | PCI config space not emulated: reads zero, writes ignored, **every access logged as unmodelled** |
| 49 | HalReturnToFirmware | stops the machine: TitleExited |
| 51-55 | InterlockedCompareExchange/Decrement/Increment/Exchange/ExchangeAdd (fastcall) | atomic w.r.t. guest threads |
| 67, 69, 203 | IoCreateSymbolicLink, IoDeleteSymbolicLink, NtOpenSymbolicLinkObject | object-namespace symbolic links (namespace starts empty) |
| 99 | KeDelayExecutionThread | virtual-time sleep; 0 = yield |
| 103, 104 | KeGetCurrentIrql (always PASSIVE_LEVEL), KeGetCurrentThread | |
| 128 | KeQuerySystemTime | fixed epoch 2001-11-15 + virtual time (reproducible) |
| 165, 166, 171, 178, 180 | MmAllocateContiguousMemory(Ex), MmFreeContiguousMemory, MmPersistContiguousMemory (no-op: no reboot), MmQueryAllocationSize | returns kernel-view 0x8xxxxxxx addresses, zeroed, aligned, range-checked |
| 184, 199 | NtAllocateVirtualMemory, NtFreeVirtualMemory | reserve/commit back with RAM (no paging); 64 KB granularity; decommit keeps memory |
| 186, 189, 205, 225 | NtClearEvent, NtCreateEvent, NtPulseEvent, NtSetEvent | notification / synchronization events |
| 187 | NtClose | |
| 192, 221 | NtCreateMutant, NtReleaseMutant | ownership, recursion, abandonment on thread exit |
| 193, 222 | NtCreateSemaphore, NtReleaseSemaphore | limits enforced |
| 219, 236 | NtReadFile, NtWriteFile | handle validation only (no file objects exist): invalid -> STATUS_INVALID_HANDLE, non-file -> STATUS_OBJECT_TYPE_MISMATCH |
| 224, 231 | NtResumeThread, NtSuspendThread | suspend of self / waiting threads stops (unsupported) |
| 233, 234, 235 | NtWaitForSingleObject(Ex), NtWaitForMultipleObjectsEx | any/all, timeouts (relative/absolute/zero/infinite), abandoned mutants; alertable waits & APCs not modelled |
| 238 | NtYieldExecution | |
| 246, 250 | ObReferenceObjectByHandle (thread objects only), ObfDereferenceObject (no refcounting) | |
| 255, 258 | PsCreateSystemThreadEx, PsTerminateSystemThread | stack, ETHREAD/KTHREAD, TLS (TlsData+4 16-aligned), SystemRoutine(StartRoutine, Context); ThreadExtensionSize>0 unsupported |
| 277, 291, 294, 306 | Rtl(Initialize/Enter/Leave/TryEnter)CriticalSection | state kept in the guest RTL_CRITICAL_SECTION |
| 289 | RtlInitAnsiString | |
| 301 | RtlNtStatusToDosError | partial table (codes this model produces); others -> 317 |
| 302 | RtlRaiseException | stops: structured exception dispatch not emulated |
| 335-337 | XcSHAInit/Update/Final | real SHA-1, state stored in the title's context buffer; verified against .NET SHA-1 |
| data 164, 259, 326 | LaunchDataPage (NULL), PsThreadObjectType (type token), XeImageFileName (`\Device\CdRom0\default.xbe`) | |

## Not implemented (314 exports)
Every other ordinal stops the machine with `KernelExportUnimplemented` and its name. Notably absent: file system
(`NtCreateFile`, `NtOpenFile`, `NtQuery*File`, ...), video (`AvSetDisplayMode` ...), interrupts/DPCs
(`KeConnectInterrupt`, `KeInsertQueueDpc` ...), timers, named objects, all key-bearing data exports
(`XboxEEPROMKey`, `XboxHDKey`, `XboxSignatureKey`, `XboxLANKey`, these will never be given real values).

## Execution model
Cooperative, deterministic scheduler; 20 000-instruction quantum; waits block the thread and are completed at the
next scheduling point; when all threads sleep, virtual time jumps to the earliest deadline; all-blocked without a
timeout is reported as a deadlock / missing hardware interrupt.
