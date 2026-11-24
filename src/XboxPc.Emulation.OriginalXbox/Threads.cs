using XboxPc.Emulation.X86;

namespace XboxPc.Emulation.OriginalXbox;

public static class NtStatus
{
    public const uint Success = 0x0000_0000, Wait0 = 0x0000_0000, Abandoned = 0x0000_0080, Timeout = 0x0000_0102;
    public const uint NotImplemented = 0xC000_0002, InvalidHandle = 0xC000_0008, InvalidParameter = 0xC000_000D;
    public const uint NoMemory = 0xC000_0017, MutantNotOwned = 0xC000_0046, SemaphoreLimitExceeded = 0xC000_0047;
    public const uint ObjectTypeMismatch = 0xC000_0024;
    public const uint ObjectNameNotFound = 0xC000_0034, ObjectNameCollision = 0xC000_0035;
}

/// <summary>
/// Guest structure offsets, derived from nxdk lib/xboxkrnl/xboxkrnl.h (x86, default 8-byte packing).
/// sizeof(KTHREAD) = 0x110; ETHREAD extends it.
/// </summary>
public static class GuestLayout
{
    public const uint KThreadStackBase = 0x1C, KThreadStackLimit = 0x20, KThreadTlsData = 0x28, KThreadHasTerminated = 0x77;
    public const uint KThreadSize = 0x110;
    public const uint EThreadExitStatus = 0x120, EThreadUniqueThread = 0x12C, EThreadStartAddress = 0x130, EThreadSize = 0x140;

    // KPCR: NT_TIB at 0 (ExceptionList, StackBase, StackLimit, ..., Self at 0x18), SelfPcr 0x1C, Prcb 0x20, Irql 0x24,
    // KPRCB at 0x28 with CurrentThread first.
    public const uint KpcrExceptionList = 0x00, KpcrStackBase = 0x04, KpcrStackLimit = 0x08, KpcrSelf = 0x18, KpcrSelfPcr = 0x1C;
    public const uint KpcrPrcb = 0x20, KpcrIrql = 0x24, KpcrCurrentThread = 0x28, KpcrSize = 0x300;

    // RTL_CRITICAL_SECTION: KEVENT-like header (16 bytes), LockCount, RecursionCount, OwningThread.
    public const uint CsLockCount = 0x10, CsRecursionCount = 0x14, CsOwningThread = 0x18;
}

public enum ThreadState
{
    Ready,
    Waiting,
    Suspended,
    Terminated,
}

public abstract class DispatcherObject
{
    public abstract bool IsSignaledFor(GuestThread thread);

    /// <summary>Applies the side effect of a satisfied wait (auto-reset, ownership, count).</summary>
    public abstract void Acquire(GuestThread thread);

    public abstract string Kind { get; }
}

public sealed class Mutant : DispatcherObject
{
    public GuestThread? Owner { get; set; }
    public int Recursion { get; set; }
    public bool Abandoned { get; set; }
    public override string Kind => "Mutant";
    public override bool IsSignaledFor(GuestThread thread) => Owner is null || Owner == thread;

    public override void Acquire(GuestThread thread)
    {
        Owner = thread;
        Recursion++;
    }
}

public sealed class GuestEvent(bool synchronization, bool signaled) : DispatcherObject
{
    public bool Synchronization { get; } = synchronization;
    public bool Signaled { get; set; } = signaled;
    public override string Kind => Synchronization ? "SynchronizationEvent" : "NotificationEvent";
    public override bool IsSignaledFor(GuestThread thread) => Signaled;

    public override void Acquire(GuestThread thread)
    {
        if (Synchronization) Signaled = false;
    }
}

public sealed class Semaphore(int count, int limit) : DispatcherObject
{
    public int Count { get; set; } = count;
    public int Limit { get; } = limit;
    public override string Kind => "Semaphore";
    public override bool IsSignaledFor(GuestThread thread) => Count > 0;
    public override void Acquire(GuestThread thread) => Count--;
}

/// <summary>Object-manager symbolic link (e.g. \??\D: -> \Device\CdRom0\). Not waitable.</summary>
public sealed class SymbolicLink(string name, string target) : DispatcherObject
{
    public string Name { get; } = name;
    public string Target { get; } = target;
    public override string Kind => "SymbolicLink";
    public override bool IsSignaledFor(GuestThread thread) => false;
    public override void Acquire(GuestThread thread) { }
}

public sealed class GuestThread : DispatcherObject
{
    public required int Id { get; init; }
    public required uint EThread { get; init; }
    public required uint StackBase { get; init; }
    public required uint StackLimit { get; init; }
    public uint TlsData { get; init; }
    public CpuContext Context { get; } = new();
    public ThreadState State { get; set; } = ThreadState.Ready;
    public uint ExitStatus { get; set; }
    public int SuspendCount { get; set; }

    // Wait state
    public IReadOnlyList<DispatcherObject>? WaitObjects { get; set; }
    public bool WaitAll { get; set; }
    public long? WaitDeadline { get; set; }

    public override string Kind => "Thread";
    public override bool IsSignaledFor(GuestThread thread) => State == ThreadState.Terminated;
    public override void Acquire(GuestThread thread) { }
}
