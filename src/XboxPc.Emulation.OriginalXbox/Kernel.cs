using System.Globalization;
using System.Text;
using XboxPc.Emulation.Core;
using XboxPc.Emulation.X86;

namespace XboxPc.Emulation.OriginalXbox;

public sealed record KernelCallRecord(long Cycle, int Thread, int Ordinal, string Name, uint[] Args, uint? Result);

/// <summary>
/// High-level emulation of xboxkrnl.exe. Only the exports registered in <see cref="_handlers"/> exist; every other
/// import stops the machine with its ordinal and name (from nxdk's export table). Calling convention and stack
/// cleanup come from the generated export table, never from per-function code.
/// Semantics follow the NT/Xbox kernel contracts as used by nxdk (lib/winapi, lib/pdclib/platform/xbox); limits of
/// each export are documented next to it.
/// </summary>
public sealed partial class KernelHle
{
    private readonly OriginalXboxMachine _m;
    private readonly StringBuilder _debugOutput = new();
    private readonly Dictionary<int, Action> _handlers;
    private readonly Dictionary<int, uint> _dataExports = [];
    private readonly Dictionary<uint, DispatcherObject> _handles = [];
    private readonly Dictionary<uint, GuestEvent> _criticalSectionEvents = [];
    private readonly List<KernelCallRecord> _trace = [];
    private readonly Dictionary<int, long> _callCounts = [];
    private uint _nextHandle = 0x10;

    public KernelHle(OriginalXboxMachine machine)
    {
        _m = machine;
        _handlers = new Dictionary<int, Action>
        {
            [8] = DbgPrint,
            [49] = HalReturnToFirmware,
            [99] = KeDelayExecutionThread,
            [103] = () => Return(0),                         // KeGetCurrentIrql: PASSIVE_LEVEL (IRQL is not modelled)
            [104] = () => Return(_m.Current!.EThread),       // KeGetCurrentThread
            [128] = KeQuerySystemTime,
            [165] = () => MmAllocateContiguousMemoryEx(Arg(0), 0, 0xFFFF_FFFF, 0),
            [166] = () => MmAllocateContiguousMemoryEx(Arg(0), Arg(1), Arg(2), Arg(3)),
            [171] = MmFreeContiguousMemory,
            [178] = () => Return(0),                         // MmPersistContiguousMemory: no reboot is emulated, nothing to persist
            [180] = () => Return(_m.Memory.SizeOf(Arg(0) & ~OriginalXboxMachine.KernelRamMirror) ?? 0),
            [184] = NtAllocateVirtualMemory,
            [186] = () => Return(WithObject<GuestEvent>(Arg(0), e => e.Signaled = false)),
            [187] = NtClose,
            [189] = NtCreateEvent,
            [192] = NtCreateMutant,
            [193] = NtCreateSemaphore,
            [199] = NtFreeVirtualMemory,
            [205] = NtPulseEvent,
            [221] = NtReleaseMutant,
            [222] = NtReleaseSemaphore,
            [224] = NtResumeThread,
            [225] = NtSetEvent,
            [231] = NtSuspendThread,
            [233] = () => Wait([Arg(0)], waitAll: false, Arg(2)),
            [234] = () => Wait([Arg(0)], waitAll: false, Arg(3)),
            [235] = () => Wait(ReadHandles(Arg(1), Arg(0)), waitAll: Arg(2) == 0, Arg(5)),
            [238] = () => { _m.SwitchRequested = true; Return(NtStatus.Success); }, // NtYieldExecution
            [246] = ObReferenceObjectByHandle,
            [250] = () => Return(0),                         // ObfDereferenceObject: objects are not reference-counted/freed
            [255] = PsCreateSystemThreadEx,
            [258] = PsTerminateSystemThread,
            [277] = RtlEnterCriticalSection,
            [291] = RtlInitializeCriticalSection,
            [294] = RtlLeaveCriticalSection,
            [301] = () => Return(StatusToDosError(Arg(0))),
            [302] = RtlRaiseException,
            [306] = RtlTryEnterCriticalSection,
            [24] = ExQueryNonVolatileSetting,
            // MmGetPhysicalAddress: no paging, so the title view is physical and the kernel view is RAM | 0x80000000.
            [173] = () => Return(Arg(0) & ~OriginalXboxMachine.KernelRamMirror),
            [168] = () => MmClaimGpuInstanceMemory(Arg(0), Arg(1)),
            [47] = () => Return(0),                          // HalRegisterShutdownNotification: no shutdown/reboot is emulated, the routine would never run
            [175] = () => Return(0),                         // MmLockUnlockBufferPages: guest memory is never paged out under HLE, locking is a no-op
            [67] = IoCreateSymbolicLink,
            [69] = IoDeleteSymbolicLink,
            [203] = NtOpenSymbolicLinkObject,
            [289] = RtlInitAnsiString,
            // Interlocked operations (fastcall). The interpreter runs one guest thread at a time and HLE calls are not
            // preemptible, so each call is atomic with respect to other guest threads.
            [51] = () => { var d = Arg(0); var old = Bus.Read32(d); if (old == Arg(2)) Bus.Write32(d, Arg(1)); Return(old); }, // InterlockedCompareExchange(Dest, Exchange, Comparand)
            [52] = () => { var d = Arg(0); var v = Bus.Read32(d) - 1; Bus.Write32(d, v); Return(v); },                     // InterlockedDecrement
            [53] = () => { var d = Arg(0); var v = Bus.Read32(d) + 1; Bus.Write32(d, v); Return(v); },                     // InterlockedIncrement
            [54] = () => { var d = Arg(0); var old = Bus.Read32(d); Bus.Write32(d, Arg(1)); Return(old); },               // InterlockedExchange
            [55] = () => { var d = Arg(0); var old = Bus.Read32(d); Bus.Write32(d, old + Arg(1)); Return(old); },        // InterlockedExchangeAdd
            [46] = HalReadWritePCISpace,
            [335] = () => { Sha1Context.Init(Bus, Arg(0)); Return(0); },                                   // XcSHAInit(PUCHAR ctx)
            [336] = () => { Sha1Context.Update(Bus, Arg(0), Bus.ReadBytes(Arg(1), (int)Math.Min(Arg(2), 1u << 26))); Return(0); }, // XcSHAUpdate(ctx, input, length)
            [337] = () => { Bus.WriteBytes(Arg(1), Sha1Context.Final(Bus, Arg(0))); Return(0); },         // XcSHAFinal(ctx, digest[20])
        };
        RegisterInterruptExports();
        RegisterVideoExports();
        RegisterFileExports();
    }

    public static string NameOf(int ordinal) => KernelExports.ByOrdinal.TryGetValue(ordinal, out var e) ? e.Name : "(unknown ordinal)";

    public IReadOnlyCollection<int> Implemented => _handlers.Keys.Concat(_dataExports.Keys).ToList();

    public static readonly IReadOnlySet<int> ImplementedOrdinals = new KernelHle(null!).ImplementedSet();

    private HashSet<int> ImplementedSet() => [.. _handlers.Keys, 164, 259, 326, 156, 120, 154];

    /// <summary>Text the title printed through DbgPrint.</summary>
    public string DebugOutput => _debugOutput.ToString();

    public long Calls { get; private set; }

    public IReadOnlyList<KernelCallRecord> Trace => _trace;

    public IReadOnlyDictionary<int, long> CallCounts => _callCounts;

    // ------------------------------------------------------------------ plumbing

    private X86Cpu Cpu => _m.Cpu;
    private PhysicalBus Bus => _m.Bus;
    private int _currentOrdinal;
    private uint[] _currentArgs = [];

    /// <summary>Argument i of the current export: fastcall passes the first two in ECX/EDX, the rest on the stack.</summary>
    private uint Arg(int i)
    {
        if (_currentKind == KernelExportKind.Fastcall)
        {
            if (i == 0) return Cpu[X86Cpu.ECX];
            if (i == 1) return Cpu[X86Cpu.EDX];
            i -= 2;
        }
        return Bus.Read32(Cpu[X86Cpu.ESP] + 4 + ((uint)i * 4));
    }

    private KernelExportKind _currentKind;

    /// <summary>Bytes the callee removes from the stack (stdcall: all arguments; fastcall: arguments beyond ECX/EDX).</summary>
    private static uint CalleeCleanup(KernelExportInfo info) => info.Kind switch
    {
        KernelExportKind.Stdcall => (uint)info.ArgumentBytes,
        KernelExportKind.Fastcall => (uint)Math.Max(0, info.ArgumentBytes - 8),
        _ => 0,
    };

    internal void Record(int ordinal, string name, uint[] args, uint? result = null)
    {
        if (_trace.Count < 5000) _trace.Add(new KernelCallRecord(_m.Cycles, _m.Current?.Id ?? 0, ordinal, name, args, result));
    }

    /// <summary>Returns to the caller with the export's calling convention (stack cleanup from the export table).</summary>
    private void Return(uint eax)
    {
        var info = KernelExports.ByOrdinal[_currentOrdinal];
        Cpu[X86Cpu.EAX] = eax;
        var retAddr = Bus.Read32(Cpu[X86Cpu.ESP]);
        Cpu[X86Cpu.ESP] += 4 + CalleeCleanup(info);
        Cpu.Eip = retAddr;
        if (_trace.Count > 0 && _trace[^1].Ordinal == _currentOrdinal && _trace[^1].Result is null)
            _trace[^1] = _trace[^1] with { Result = eax };
    }

    /// <summary>Returns now; the thread is blocked and its EAX will be set when the wait completes.</summary>
    private void ReturnBlocked()
    {
        var info = KernelExports.ByOrdinal[_currentOrdinal];
        var retAddr = Bus.Read32(Cpu[X86Cpu.ESP]);
        Cpu[X86Cpu.ESP] += 4 + CalleeCleanup(info);
        Cpu.Eip = retAddr;
    }

    internal void Dispatch(int ordinal)
    {
        Calls++;
        _callCounts[ordinal] = _callCounts.GetValueOrDefault(ordinal) + 1;
        _currentOrdinal = ordinal;
        _currentKind = KernelExports.ByOrdinal.TryGetValue(ordinal, out var exportInfo) ? exportInfo.Kind : KernelExportKind.Stdcall;
        if (!_handlers.TryGetValue(ordinal, out var handler))
        {
            Record(ordinal, NameOf(ordinal), ReadArgsForTrace(ordinal));
            throw new MachineStopException(MachineStop.KernelExportUnimplemented,
                $"Kernel export #{ordinal} {NameOf(ordinal)} is not implemented yet (called by thread {_m.Current?.Id} from 0x{Bus.Read32(Cpu[X86Cpu.ESP]):X8}).", ordinal);
        }
        _currentArgs = ReadArgsForTrace(ordinal);
        Record(ordinal, NameOf(ordinal), _currentArgs);
        handler();
    }

    private uint[] ReadArgsForTrace(int ordinal)
    {
        var n = KernelExports.ByOrdinal.TryGetValue(ordinal, out var e) ? (e.Kind == KernelExportKind.Cdecl ? 3 : e.ArgumentBytes / 4) : 0;
        _currentKind = e?.Kind ?? KernelExportKind.Stdcall;
        return Enumerable.Range(0, Math.Min(n, 10)).Select(Arg).ToArray();
    }

    private static MachineStopException Unsupported(string what) => new(MachineStop.KernelFeatureUnsupported, what);

    // ------------------------------------------------------------------ data exports

    /// <summary>
    /// Address placed in a thunk slot. Functions get a trap address; implemented data exports get the address of a
    /// real variable in guest memory; unimplemented data exports get a trap address so any access is reported.
    /// </summary>
    internal uint ResolveImport(int ordinal) => _dataExports.TryGetValue(ordinal, out var a) ? a : OriginalXboxMachine.TrapAddress(ordinal);

    internal void InitializeDataExports()
    {
        _dataExports.Clear();
        var block = _m.Allocate(GuestMemory.PageSize, "kernel data exports");
        // LaunchDataPage (#164): PLAUNCH_DATA_PAGE variable; NULL = the title was not launched with launch data.
        _dataExports[164] = block;
        Bus.Write32(block, 0);
        // PsThreadObjectType (#259): OBJECT_TYPE used as a type token by ObReferenceObjectByHandle. Contents are not
        // modelled (zero-filled); only its address identity is meaningful.
        _dataExports[259] = block + 0x40;
        // XeImageFileName (#326): ANSI_STRING { USHORT Length; USHORT MaximumLength; PCHAR Buffer; }.
        var path = "\\Device\\CdRom0\\" + _m.ImageFileName;
        var text = block + 0x100;
        Bus.WriteBytes(text, Encoding.ASCII.GetBytes(path + "\0"));
        _dataExports[326] = block + 0x80;
        Bus.Write16(block + 0x80, (ushort)path.Length);
        Bus.Write16(block + 0x82, (ushort)(path.Length + 1));
        Bus.Write32(block + 0x84, text);
        // Time variables updated by the 1 ms system clock (see KernelClock): KeTickCount (#156, ULONG ms),
        // KeInterruptTime (#120) and KeSystemTime (#154) as KSYSTEM_TIME { LowPart, High1Time, High2Time }.
        _dataExports[156] = block + 0x200;
        _dataExports[120] = block + 0x210;
        _dataExports[154] = block + 0x220;
    }

    internal uint TickCountAddress => _dataExports[156];
    internal uint InterruptTimeAddress => _dataExports[120];
    internal uint SystemTimeAddress => _dataExports[154];

    // ------------------------------------------------------------------ handles and objects

    private uint NewHandle(DispatcherObject o)
    {
        var h = _nextHandle;
        _nextHandle += 4;
        _handles[h] = o;
        return h;
    }

    private DispatcherObject? Lookup(uint handle)
    {
        if (handle is 0xFFFF_FFFE) return _m.Current; // NtCurrentThread()
        return _handles.GetValueOrDefault(handle);
    }

    private uint WithObject<T>(uint handle, Action<T> action) where T : DispatcherObject
    {
        var o = Lookup(handle);
        if (o is null) return NtStatus.InvalidHandle;
        if (o is not T t) return NtStatus.ObjectTypeMismatch;
        action(t);
        return NtStatus.Success;
    }

    private void RequireUnnamed(uint objectAttributes)
    {
        // OBJECT_ATTRIBUTES { HANDLE RootDirectory; PANSI_STRING ObjectName; ULONG Attributes; }
        if (objectAttributes != 0 && Bus.Read32(objectAttributes + 4) != 0)
            throw Unsupported($"{NameOf(_currentOrdinal)} with a named object (the object namespace is not emulated yet)");
    }

    private void NtClose()
    {
        var h = Arg(0);
        if (h is 0xFFFF_FFFE or 0xFFFF_FFFF) { Return(NtStatus.Success); return; }
        if (!_handles.Remove(h, out var o)) { Return(NtStatus.InvalidHandle); return; }
        if (o is FileObject f) f.File.Dispose(); // also performs delete-on-close
        Return(NtStatus.Success);
    }

    // NTSTATUS NtCreateMutant(OUT PHANDLE, IN POBJECT_ATTRIBUTES, IN BOOLEAN InitialOwner)
    private void NtCreateMutant()
    {
        RequireUnnamed(Arg(1));
        var m = new Mutant();
        if ((Arg(2) & 0xFF) != 0) m.Acquire(_m.Current!);
        Bus.Write32(Arg(0), NewHandle(m));
        Return(NtStatus.Success);
    }

    // NTSTATUS NtReleaseMutant(IN HANDLE, OUT PLONG PreviousCount OPTIONAL)
    private void NtReleaseMutant()
    {
        if (Lookup(Arg(0)) is not Mutant m) { Return(NtStatus.InvalidHandle); return; }
        if (m.Owner != _m.Current) { Return(NtStatus.MutantNotOwned); return; }
        if (Arg(1) != 0) Bus.Write32(Arg(1), (uint)(1 - m.Recursion)); // previous SignalState
        if (--m.Recursion == 0) m.Owner = null;
        Return(NtStatus.Success);
    }

    // NTSTATUS NtCreateEvent(OUT PHANDLE, IN POBJECT_ATTRIBUTES, IN EVENT_TYPE (0 Notification, 1 Synchronization), IN BOOLEAN InitialState)
    private void NtCreateEvent()
    {
        RequireUnnamed(Arg(1));
        if (Arg(2) > 1) { Return(NtStatus.InvalidParameter); return; }
        Bus.Write32(Arg(0), NewHandle(new GuestEvent(Arg(2) == 1, (Arg(3) & 0xFF) != 0)));
        Return(NtStatus.Success);
    }

    // NTSTATUS NtSetEvent(IN HANDLE, OUT PLONG PreviousState OPTIONAL)
    private void NtSetEvent()
    {
        var prev = Arg(1);
        Return(WithObject<GuestEvent>(Arg(0), e =>
        {
            if (prev != 0) Bus.Write32(prev, e.Signaled ? 1u : 0u);
            e.Signaled = true;
        }));
    }

    // NTSTATUS NtPulseEvent(IN HANDLE, OUT PLONG PreviousState OPTIONAL): wake satisfiable waiters, then reset.
    private void NtPulseEvent()
    {
        var prev = Arg(1);
        Return(WithObject<GuestEvent>(Arg(0), e =>
        {
            if (prev != 0) Bus.Write32(prev, e.Signaled ? 1u : 0u);
            e.Signaled = true;
            foreach (var t in _m.Threads.Where(t => t.State == ThreadState.Waiting && t.WaitObjects?.Contains(e) == true && !t.WaitAll))
            {
                var index = t.WaitObjects!.ToList().IndexOf(e);
                t.Context.Regs[X86Cpu.EAX] = (uint)index;
                t.WaitObjects = null;
                t.WaitDeadline = null;
                t.State = ThreadState.Ready;
                if (e.Synchronization) break;
            }
            e.Signaled = false;
        }));
    }

    // NTSTATUS NtCreateSemaphore(OUT PHANDLE, IN POBJECT_ATTRIBUTES, IN LONG InitialCount, IN LONG MaximumCount)
    private void NtCreateSemaphore()
    {
        RequireUnnamed(Arg(1));
        int initial = (int)Arg(2), max = (int)Arg(3);
        if (max <= 0 || initial < 0 || initial > max) { Return(NtStatus.InvalidParameter); return; }
        Bus.Write32(Arg(0), NewHandle(new Semaphore(initial, max)));
        Return(NtStatus.Success);
    }

    // NTSTATUS NtReleaseSemaphore(IN HANDLE, IN LONG ReleaseCount, OUT PLONG PreviousCount OPTIONAL)
    private void NtReleaseSemaphore()
    {
        if (Lookup(Arg(0)) is not Semaphore s) { Return(NtStatus.InvalidHandle); return; }
        var release = (int)Arg(1);
        if (release <= 0) { Return(NtStatus.InvalidParameter); return; }
        if ((long)s.Count + release > s.Limit) { Return(NtStatus.SemaphoreLimitExceeded); return; }
        if (Arg(2) != 0) Bus.Write32(Arg(2), (uint)s.Count);
        s.Count += release;
        Return(NtStatus.Success);
    }

    private List<uint> ReadHandles(uint pointer, uint count)
    {
        if (count is 0 or > 64) throw Unsupported($"NtWaitForMultipleObjectsEx with {count} handles");
        return Enumerable.Range(0, (int)count).Select(i => Bus.Read32(pointer + (uint)(i * 4))).ToList();
    }

    /// <summary>Waits; the timeout argument is a PLARGE_INTEGER (NULL = infinite). Alertable waits / APCs are not modelled.</summary>
    private void Wait(IReadOnlyList<uint> handles, bool waitAll, uint timeoutPointer)
    {
        var objects = new List<DispatcherObject>();
        foreach (var h in handles)
        {
            var o = Lookup(h);
            if (o is null) { Return(NtStatus.InvalidHandle); return; }
            objects.Add(o);
        }
        long? timeout = timeoutPointer == 0 ? null : (long)(Bus.Read32(timeoutPointer) | ((ulong)Bus.Read32(timeoutPointer + 4) << 32));
        var status = _m.BeginWait(objects, waitAll, timeout);
        if (status is { } s) Return(s);
        else ReturnBlocked();
    }

    // NTSTATUS KeDelayExecutionThread(IN KPROCESSOR_MODE, IN BOOLEAN Alertable, IN PLARGE_INTEGER Interval)
    private void KeDelayExecutionThread()
    {
        var p = Arg(2);
        var interval = (long)(Bus.Read32(p) | ((ulong)Bus.Read32(p + 4) << 32));
        if (interval == 0) { _m.SwitchRequested = true; Return(NtStatus.Success); return; }
        var status = _m.BeginWait([], waitAll: false, interval);
        if (status is { } s) Return(s);
        else ReturnBlocked();
    }

    private void KeQuerySystemTime()
    {
        var t = (ulong)_m.SystemTime100ns;
        Bus.Write32(Arg(0), (uint)t);
        Bus.Write32(Arg(0) + 4, (uint)(t >> 32));
        Return(0);
    }

    // ------------------------------------------------------------------ threads

    /// <summary>
    /// NTSTATUS PsCreateSystemThreadEx(OUT PHANDLE ThreadHandle, IN ULONG ThreadExtensionSize, IN ULONG KernelStackSize,
    /// IN ULONG TlsDataSize, OUT PHANDLE ThreadId OPTIONAL, IN PKSTART_ROUTINE StartRoutine, IN PVOID StartContext,
    /// IN BOOLEAN CreateSuspended, IN BOOLEAN DebuggerThread, IN PKSYSTEM_ROUTINE SystemRoutine OPTIONAL).
    /// The new thread runs SystemRoutine(StartRoutine, StartContext) (stdcall), or StartRoutine(StartContext) when
    /// SystemRoutine is NULL. ThreadExtensionSize &gt; 0 is not supported.
    /// </summary>
    private void PsCreateSystemThreadEx()
    {
        if (Arg(1) != 0) throw Unsupported("PsCreateSystemThreadEx with ThreadExtensionSize > 0");
        uint startRoutine = Arg(5), startContext = Arg(6), systemRoutine = Arg(9);
        var thread = systemRoutine != 0
            ? _m.CreateThread(Arg(2), Arg(3), systemRoutine, [startRoutine, startContext])
            : _m.CreateThread(Arg(2), Arg(3), startRoutine, [startContext]);
        if ((Arg(7) & 0xFF) != 0) { thread.State = ThreadState.Suspended; thread.SuspendCount = 1; }
        if (Arg(0) != 0) Bus.Write32(Arg(0), NewHandle(thread));
        if (Arg(4) != 0) Bus.Write32(Arg(4), (uint)thread.Id);
        Return(NtStatus.Success);
    }

    private void PsTerminateSystemThread()
    {
        Record(258, "PsTerminateSystemThread", [Arg(0)]);
        _m.TerminateCurrent(Arg(0)); // does not return to the caller
    }

    internal void OnThreadTerminated(GuestThread t)
    {
        foreach (var m in _handles.Values.OfType<Mutant>().Where(m => m.Owner == t))
        {
            m.Owner = null;
            m.Recursion = 0;
            m.Abandoned = true;
        }
    }

    // NTSTATUS NtResumeThread(IN HANDLE, OUT PULONG PreviousSuspendCount OPTIONAL)
    private void NtResumeThread()
    {
        if (Lookup(Arg(0)) is not GuestThread t) { Return(NtStatus.InvalidHandle); return; }
        if (Arg(1) != 0) Bus.Write32(Arg(1), (uint)t.SuspendCount);
        if (t.SuspendCount > 0 && --t.SuspendCount == 0 && t.State == ThreadState.Suspended) t.State = ThreadState.Ready;
        Return(NtStatus.Success);
    }

    private void NtSuspendThread()
    {
        if (Lookup(Arg(0)) is not GuestThread t) { Return(NtStatus.InvalidHandle); return; }
        if (t == _m.Current) throw Unsupported("NtSuspendThread on the calling thread");
        if (Arg(1) != 0) Bus.Write32(Arg(1), (uint)t.SuspendCount);
        if (t.State == ThreadState.Waiting) throw Unsupported("NtSuspendThread on a waiting thread");
        t.SuspendCount++;
        if (t.State == ThreadState.Ready) t.State = ThreadState.Suspended;
        Return(NtStatus.Success);
    }

    // NTSTATUS ObReferenceObjectByHandle(IN HANDLE, IN POBJECT_TYPE ObjectType OPTIONAL, OUT PVOID *ReturnedObject)
    private void ObReferenceObjectByHandle()
    {
        var o = Lookup(Arg(0));
        if (o is null) { Return(NtStatus.InvalidHandle); return; }
        if (o is not GuestThread t) throw Unsupported($"ObReferenceObjectByHandle for a {o.Kind} (only thread objects have guest-visible bodies)");
        if (Arg(1) != 0 && Arg(1) != _dataExports[259]) { Return(NtStatus.ObjectTypeMismatch); return; }
        Bus.Write32(Arg(2), t.EThread);
        Return(NtStatus.Success);
    }

    // ------------------------------------------------------------------ critical sections (state lives in guest memory)

    private uint Current => _m.Current!.EThread;

    private GuestEvent CsEvent(uint cs) => _criticalSectionEvents.TryGetValue(cs, out var e) ? e : _criticalSectionEvents[cs] = new GuestEvent(synchronization: true, signaled: false);

    private void RtlInitializeCriticalSection()
    {
        var cs = Arg(0);
        Bus.Write8(cs, 1);                 // Type = SynchronizationEvent
        Bus.Write8(cs + 2, 4);             // Size (in dwords)
        Bus.Write32(cs + 4, 0);            // SignalState
        Bus.Write32(cs + 8, cs + 8);       // WaitListHead.Flink = self
        Bus.Write32(cs + 12, cs + 8);      // WaitListHead.Blink = self
        Bus.Write32(cs + GuestLayout.CsLockCount, 0xFFFF_FFFF);
        Bus.Write32(cs + GuestLayout.CsRecursionCount, 0);
        Bus.Write32(cs + GuestLayout.CsOwningThread, 0);
        _criticalSectionEvents[cs] = new GuestEvent(synchronization: true, signaled: false);
        Return(0);
    }

    private void RtlEnterCriticalSection()
    {
        var cs = Arg(0);
        var lockCount = (int)Bus.Read32(cs + GuestLayout.CsLockCount) + 1;
        Bus.Write32(cs + GuestLayout.CsLockCount, (uint)lockCount);
        if (lockCount == 0) { TakeCs(cs); Return(0); return; }
        if (Bus.Read32(cs + GuestLayout.CsOwningThread) == Current)
        {
            Bus.Write32(cs + GuestLayout.CsRecursionCount, Bus.Read32(cs + GuestLayout.CsRecursionCount) + 1);
            Return(0);
            return;
        }
        // Contended: wait on the section's synchronization event; ownership is taken by the releasing thread's
        // signal (see Leave) and completed here when the waiter resumes.
        var e = CsEvent(cs);
        var status = _m.BeginWait([e], waitAll: false, null);
        if (status is not null) { TakeCs(cs); Return(0); return; }
        _pendingCsOwner[_m.Current!] = cs;
        ReturnBlocked();
    }

    private readonly Dictionary<GuestThread, uint> _pendingCsOwner = [];

    /// <summary>Called by the scheduler path: a thread that was woken from a critical-section wait becomes owner.</summary>
    internal void CompletePendingCriticalSection(GuestThread t)
    {
        if (!_pendingCsOwner.Remove(t, out var cs)) return;
        Bus.Write32(cs + GuestLayout.CsOwningThread, t.EThread);
        Bus.Write32(cs + GuestLayout.CsRecursionCount, 1);
    }

    private void TakeCs(uint cs)
    {
        Bus.Write32(cs + GuestLayout.CsOwningThread, Current);
        Bus.Write32(cs + GuestLayout.CsRecursionCount, 1);
    }

    private void RtlLeaveCriticalSection()
    {
        var cs = Arg(0);
        var recursion = Bus.Read32(cs + GuestLayout.CsRecursionCount) - 1;
        Bus.Write32(cs + GuestLayout.CsRecursionCount, recursion);
        var lockCount = (int)Bus.Read32(cs + GuestLayout.CsLockCount) - 1;
        Bus.Write32(cs + GuestLayout.CsLockCount, (uint)lockCount);
        if (recursion == 0)
        {
            Bus.Write32(cs + GuestLayout.CsOwningThread, 0);
            if (lockCount >= 0) CsEvent(cs).Signaled = true; // a waiter exists: hand over
        }
        Return(0);
    }

    private void RtlTryEnterCriticalSection()
    {
        var cs = Arg(0);
        var lockCount = (int)Bus.Read32(cs + GuestLayout.CsLockCount);
        if (lockCount == -1)
        {
            Bus.Write32(cs + GuestLayout.CsLockCount, 0);
            TakeCs(cs);
            Return(1);
        }
        else if (Bus.Read32(cs + GuestLayout.CsOwningThread) == Current)
        {
            Bus.Write32(cs + GuestLayout.CsLockCount, (uint)(lockCount + 1));
            Bus.Write32(cs + GuestLayout.CsRecursionCount, Bus.Read32(cs + GuestLayout.CsRecursionCount) + 1);
            Return(1);
        }
        else Return(0);
    }

    // ------------------------------------------------------------------ memory

    // PVOID MmAllocateContiguousMemoryEx(SIZE_T NumberOfBytes, ULONG_PTR LowestAcceptableAddress,
    //     ULONG_PTR HighestAcceptableAddress, ULONG_PTR Alignment, ULONG ProtectionType). Returns a kernel-view
    //     (0x8xxxxxxx) address like the real kernel; NULL when no block fits.
    /// <summary>Top of RAM reserved at load for the NV2A instance memory (the real kernel reserves it the same way).</summary>
    public const uint GpuInstanceMemorySize = 0x1_0000;

    // PVOID MmClaimGpuInstanceMemory(IN SIZE_T NumberOfBytes, OUT SIZE_T* NumberOfPaddingBytes)
    //   Returns the end of the GPU instance memory (kernel view of the top of RAM) and the padding the kernel keeps
    //   below it. The NV2A model keeps PRAMIN as its own 1 MB store (as xemu does); titles address it through PRAMIN.
    private void MmClaimGpuInstanceMemory(uint numberOfBytes, uint paddingPtr)
    {
        if (paddingPtr != 0) Bus.Write32(paddingPtr, GpuInstanceMemorySize);
        _ = numberOfBytes;
        Return(OriginalXboxMachine.KernelRamMirror | (OriginalXboxMachine.RamSize - GpuInstanceMemorySize));
    }

    private void MmAllocateContiguousMemoryEx(uint size, uint lowest, uint highest, uint alignment)
    {
        if (size == 0) { Return(0); return; }
        var a = _m.Memory.Allocate(size, "MmAllocateContiguousMemory", alignment == 0 ? GuestMemory.PageSize : alignment, lowest, Math.Min(highest, OriginalXboxMachine.RamSize - 1));
        if (a is null) { Return(0); return; }
        _m.ZeroGuest(a.Value, GuestMemory.Align(size, GuestMemory.PageSize));
        Return(a.Value | OriginalXboxMachine.KernelRamMirror);
    }

    private void MmFreeContiguousMemory()
    {
        _m.Memory.Free(Arg(0) & ~OriginalXboxMachine.KernelRamMirror);
        Return(0);
    }

    private const uint MemCommit = 0x1000, MemReserve = 0x2000, MemDecommit = 0x4000, MemRelease = 0x8000;

    // NTSTATUS NtAllocateVirtualMemory(IN OUT PVOID *BaseAddress, IN ULONG_PTR ZeroBits, IN OUT PSIZE_T RegionSize,
    //     IN ULONG AllocationType, IN ULONG Protect). Reserve and commit both back the range with RAM (no paging).
    private void NtAllocateVirtualMemory()
    {
        uint basePtr = Arg(0), sizePtr = Arg(2), type = Arg(3);
        var requestedBase = Bus.Read32(basePtr);
        var size = Bus.Read32(sizePtr);
        if (size == 0 || (type & (MemCommit | MemReserve)) == 0) { Return(NtStatus.InvalidParameter); return; }
        if (requestedBase != 0)
        {
            if (_m.Memory.Containing(requestedBase) is { } existing && requestedBase + size <= existing.Start + existing.Size)
            {
                // commit inside an existing reservation: already backed
                var pageBase = requestedBase & ~(GuestMemory.PageSize - 1);
                Bus.Write32(basePtr, pageBase);
                Bus.Write32(sizePtr, GuestMemory.Align(requestedBase + size, GuestMemory.PageSize) - pageBase);
                Return(NtStatus.Success);
                return;
            }
            var exact = _m.Memory.Allocate(size, "NtAllocateVirtualMemory", 0x1_0000, requestedBase & ~0xFFFFu, (requestedBase & ~0xFFFFu) + GuestMemory.Align(size, 0x1_0000) - 1);
            if (exact is null) { Return(0xC000_0018); return; } // STATUS_CONFLICTING_ADDRESSES
            _m.ZeroGuest(exact.Value, GuestMemory.Align(size, GuestMemory.PageSize));
            Bus.Write32(basePtr, exact.Value);
            Bus.Write32(sizePtr, GuestMemory.Align(size, GuestMemory.PageSize));
            Return(NtStatus.Success);
            return;
        }
        var a = _m.Memory.Allocate(size, "NtAllocateVirtualMemory", 0x1_0000);
        if (a is null) { Return(NtStatus.NoMemory); return; }
        _m.ZeroGuest(a.Value, GuestMemory.Align(size, GuestMemory.PageSize));
        Bus.Write32(basePtr, a.Value);
        Bus.Write32(sizePtr, GuestMemory.Align(size, GuestMemory.PageSize));
        Return(NtStatus.Success);
    }

    // NTSTATUS NtFreeVirtualMemory(IN OUT PVOID *BaseAddress, IN OUT PSIZE_T RegionSize, IN ULONG FreeType)
    private void NtFreeVirtualMemory()
    {
        var baseAddress = Bus.Read32(Arg(0));
        var type = Arg(2);
        if (type == MemDecommit) { Return(NtStatus.Success); return; } // decommit keeps the reservation; memory stays backed
        if (type != MemRelease) { Return(NtStatus.InvalidParameter); return; }
        if (_m.Memory.SizeOf(baseAddress) is not { } size) { Return(0xC000_009F); return; } // STATUS_MEMORY_NOT_ALLOCATED
        _m.Memory.Free(baseAddress);
        Bus.Write32(Arg(1), size);
        Return(NtStatus.Success);
    }

    // ------------------------------------------------------------------ object namespace (symbolic links only)

    private readonly Dictionary<string, string> _symbolicLinks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Symbolic links created by the title (the emulated namespace starts empty).</summary>
    public IReadOnlyDictionary<string, string> SymbolicLinks => _symbolicLinks;

    /// <summary>Reads an ANSI_STRING { USHORT Length; USHORT MaximumLength; PCHAR Buffer; }.</summary>
    private string ReadAnsiString(uint p)
    {
        var length = Bus.Read16(p);
        var buffer = Bus.Read32(p + 4);
        return Encoding.ASCII.GetString(Bus.ReadBytes(buffer, length));
    }

    // VOID RtlInitAnsiString(OUT PANSI_STRING DestinationString, IN PCSZ SourceString)
    private void RtlInitAnsiString()
    {
        uint dest = Arg(0), source = Arg(1);
        if (source == 0)
        {
            Bus.Write16(dest, 0);
            Bus.Write16(dest + 2, 0);
        }
        else
        {
            var length = (ushort)Math.Min(Bus.ReadCString(source, 0xFFFE).Length, 0xFFFE);
            Bus.Write16(dest, length);
            Bus.Write16(dest + 2, (ushort)(length + 1));
        }
        Bus.Write32(dest + 4, source);
        Return(0);
    }

    // NTSTATUS IoCreateSymbolicLink(IN PANSI_STRING SymbolicLinkName, IN PANSI_STRING DeviceName)
    private void IoCreateSymbolicLink()
    {
        var name = ReadAnsiString(Arg(0));
        if (_symbolicLinks.ContainsKey(name)) { Return(NtStatus.ObjectNameCollision); return; }
        _symbolicLinks[name] = ReadAnsiString(Arg(1));
        Return(NtStatus.Success);
    }

    // NTSTATUS IoDeleteSymbolicLink(IN PANSI_STRING SymbolicLinkName)
    private void IoDeleteSymbolicLink() => Return(_symbolicLinks.Remove(ReadAnsiString(Arg(0))) ? NtStatus.Success : NtStatus.ObjectNameNotFound);

    // NTSTATUS NtOpenSymbolicLinkObject(OUT PHANDLE LinkHandle, IN POBJECT_ATTRIBUTES ObjectAttributes)
    private void NtOpenSymbolicLinkObject()
    {
        var oa = Arg(1);
        if (Bus.Read32(oa) != 0) throw Unsupported("NtOpenSymbolicLinkObject relative to a root directory handle");
        var name = ReadAnsiString(Bus.Read32(oa + 4));
        if (!_symbolicLinks.TryGetValue(name, out var target)) { Return(NtStatus.ObjectNameNotFound); return; }
        Bus.Write32(Arg(0), NewHandle(new SymbolicLink(name, target)));
        Return(NtStatus.Success);
    }

    // ------------------------------------------------------------------ misc

    /// <summary>
    /// VOID HalReadWritePCISpace(ULONG BusNumber, ULONG SlotNumber, ULONG RegisterNumber, PVOID Buffer, ULONG Length,
    /// BOOLEAN WritePCISpace). PCI configuration space is NOT emulated: reads return zeros and writes are ignored,
    /// and every access is recorded in <see cref="OriginalXboxMachine.UnmodelledAccesses"/> so the run report shows it.
    /// </summary>
    private void HalReadWritePCISpace()
    {
        uint bus = Arg(0), slot = Arg(1), reg = Arg(2), buffer = Arg(3), length = Arg(4);
        var write = (Arg(5) & 0xFF) != 0;
        if (length > 256) throw Unsupported($"HalReadWritePCISpace length {length}");
        _m.UnmodelledAccesses.Add($"PCI config {(write ? "write" : "read")} bus {bus} slot 0x{slot:X2} (device {slot & 0x1F}, function {slot >> 5}) register 0x{reg:X2}, {length} bytes: {(write ? "ignored" : "returned zeros")}");
        if (!write) Bus.WriteBytes(buffer, new byte[length]);
        Return(0);
    }

    /// <summary>
    /// NTSTATUS ExQueryNonVolatileSetting(IN ULONG ValueIndex, OUT PULONG Type, OUT PVOID Value, IN ULONG ValueLength,
    /// OUT PULONG ResultLength). Only index 0xFFFF (the whole 256-byte EEPROM image) is supported. The emulated EEPROM
    /// is blank (all zero): it contains no console serial numbers, MAC address, region data or keys. Individual
    /// setting indices stop the machine until a title needs one.
    /// </summary>
    private void ExQueryNonVolatileSetting()
    {
        uint index = Arg(0), typePtr = Arg(1), value = Arg(2), length = Arg(3), resultPtr = Arg(4);
        if (index != 0xFFFF) throw Unsupported($"ExQueryNonVolatileSetting index 0x{index:X} (individual EEPROM settings are not modelled)");
        if (length < 256) { Return(0xC000_0023); return; } // STATUS_BUFFER_TOO_SMALL
        Bus.WriteBytes(value, new byte[256]);
        if (typePtr != 0) Bus.Write32(typePtr, 3);   // REG_BINARY
        if (resultPtr != 0) Bus.Write32(resultPtr, 256);
        Return(NtStatus.Success);
    }

    private void DbgPrint()
    {
        var text = FormatPrintf(Bus.ReadCString(Arg(0)), i => Arg(1 + i), Bus);
        _debugOutput.Append(text);
        Return(NtStatus.Success);
    }

    private void HalReturnToFirmware() =>
        throw new MachineStopException(MachineStop.TitleExited, $"Title called HalReturnToFirmware({Arg(0)}).", 49);

    private void RtlRaiseException()
    {
        var record = Arg(0);
        var code = Bus.Read32(record);
        var address = Bus.Read32(record + 12);
        throw new MachineStopException(MachineStop.TitleRaisedException,
            $"Title raised exception 0x{code:X8} at 0x{address:X8} via RtlRaiseException; structured exception dispatch is not emulated yet.", 302);
    }

    /// <summary>Partial mapping (the codes this kernel model can produce); others map to ERROR_MR_MID_NOT_FOUND (317).</summary>
    public static uint StatusToDosError(uint status) => status switch
    {
        NtStatus.Success => 0,
        NtStatus.Timeout => 258,          // WAIT_TIMEOUT
        NtStatus.InvalidHandle => 6,
        NtStatus.NoMemory => 8,
        NtStatus.InvalidParameter => 87,
        NtStatus.NotImplemented => 120,
        NtStatus.MutantNotOwned => 288,
        NtStatus.SemaphoreLimitExceeded => 298,
        0xC000_0018 => 487,               // ERROR_INVALID_ADDRESS
        _ => 317,
    };

    /// <summary>printf subset used by DbgPrint: %s %c %d %i %u %x %X %p %%, with '-', '0', width and 'l'/'h' modifiers.</summary>
    public static string FormatPrintf(string format, Func<int, uint> arg, PhysicalBus bus)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(arg);
        ArgumentNullException.ThrowIfNull(bus);
        var sb = new StringBuilder();
        var next = 0;
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c != '%' || i + 1 >= format.Length) { sb.Append(c); continue; }
            i++;
            bool zero = false, left = false;
            while (i < format.Length && format[i] is '0' or '-')
            {
                if (format[i] == '0') zero = true; else left = true;
                i++;
            }
            var width = 0;
            while (i < format.Length && char.IsDigit(format[i])) width = (width * 10) + (format[i++] - '0');
            while (i < format.Length && format[i] is 'l' or 'h') i++;
            if (i >= format.Length) break;
            var spec = format[i];
            var piece = spec switch
            {
                '%' => "%",
                's' => bus.ReadCString(arg(next++), 1024),
                'c' => ((char)(arg(next++) & 0xFF)).ToString(),
                'd' or 'i' => ((int)arg(next++)).ToString(CultureInfo.InvariantCulture),
                'u' => arg(next++).ToString(CultureInfo.InvariantCulture),
                'x' => arg(next++).ToString("x", CultureInfo.InvariantCulture),
                'X' => arg(next++).ToString("X", CultureInfo.InvariantCulture),
                'p' => arg(next++).ToString("X8", CultureInfo.InvariantCulture),
                _ => "%" + spec,
            };
            if (width > piece.Length) piece = left ? piece.PadRight(width) : piece.PadLeft(width, zero ? '0' : ' ');
            sb.Append(piece);
        }
        return sb.ToString();
    }
}
