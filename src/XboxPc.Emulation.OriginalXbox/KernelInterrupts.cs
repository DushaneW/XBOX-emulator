namespace XboxPc.Emulation.OriginalXbox;

/// <summary>
/// Interrupt objects and DPCs. State lives in the guest KINTERRUPT / KDPC structures (layouts from nxdk xboxkrnl.h):
/// KINTERRUPT { ServiceRoutine 0x0, ServiceContext 0x4, BusInterruptLevel 0x8, Irql 0xC, Connected 0x10, ShareVector 0x11,
/// Mode 0x12, ServiceCount 0x14 }, KDPC { Type 0x0, Inserted 0x2, DpcListEntry 0x4, DeferredRoutine 0xC,
/// DeferredContext 0x10, SystemArgument1 0x14, SystemArgument2 0x18 }.
/// Vector numbering follows the Xbox HAL convention used by public HLE kernels: vector = IRQ + 0x30,
/// IRQL = 26 − IRQ. Titles only pass these values back to KeInitializeInterrupt.
/// </summary>
public sealed partial class KernelHle
{
    public const uint VectorBase = 0x30;
    private const uint KiServiceRoutine = 0x0, KiServiceContext = 0x4, KiBusLevel = 0x8, KiIrql = 0xC, KiConnected = 0x10, KiShareVector = 0x11, KiMode = 0x12, KiServiceCount = 0x14;
    private const uint KdType = 0x0, KdInserted = 0x2, KdRoutine = 0xC, KdContext = 0x10, KdArg1 = 0x14, KdArg2 = 0x18;

    private readonly List<uint> _connectedInterrupts = [];
    private readonly Queue<uint> _dpcQueue = new();

    public sealed record QueuedDpc(uint Dpc, uint Routine, uint Context, uint Arg1, uint Arg2);

    internal bool HasQueuedDpcs => _dpcQueue.Count > 0;

    /// <summary>Interrupt objects currently connected (guest addresses).</summary>
    public IReadOnlyList<uint> ConnectedInterrupts => _connectedInterrupts;

    private void RegisterInterruptExports()
    {
        _handlers[44] = HalGetInterruptVector;
        _handlers[98] = KeConnectInterrupt;
        _handlers[100] = KeDisconnectInterrupt;
        _handlers[107] = KeInitializeDpc;
        _handlers[109] = KeInitializeInterrupt;
        _handlers[119] = KeInsertQueueDpc;
        _handlers[137] = KeRemoveQueueDpc;
    }

    // ULONG HalGetInterruptVector(IN ULONG BusInterruptLevel, OUT PKIRQL Irql)
    private void HalGetInterruptVector()
    {
        var level = Arg(0);
        if (level > 15) { Return(0); return; }
        if (Arg(1) != 0) Bus.Write8(Arg(1), (byte)(26 - level));
        Return(VectorBase + level);
    }

    // VOID KeInitializeInterrupt(PKINTERRUPT, PKSERVICE_ROUTINE, PVOID ServiceContext, ULONG Vector, KIRQL Irql,
    //                            KINTERRUPT_MODE InterruptMode, BOOLEAN ShareVector)
    private void KeInitializeInterrupt()
    {
        var k = Arg(0);
        Bus.Write32(k + KiServiceRoutine, Arg(1));
        Bus.Write32(k + KiServiceContext, Arg(2));
        Bus.Write32(k + KiBusLevel, Arg(3) - VectorBase);
        Bus.Write32(k + KiIrql, Arg(4) & 0xFF);
        Bus.Write8(k + KiConnected, 0);
        Bus.Write8(k + KiShareVector, (byte)Arg(6));
        Bus.Write8(k + KiMode, (byte)Arg(5));
        Bus.Write32(k + KiServiceCount, 0);
        Return(0);
    }

    // BOOLEAN KeConnectInterrupt(IN PKINTERRUPT)
    private void KeConnectInterrupt()
    {
        var k = Arg(0);
        if (Bus.Read8(k + KiConnected) != 0 || Bus.Read32(k + KiBusLevel) > 15) { Return(0); return; }
        var level = Bus.Read32(k + KiBusLevel);
        // A vector can only be shared if every interrupt on it allows sharing.
        if (_connectedInterrupts.Any(o => Bus.Read32(o + KiBusLevel) == level && (Bus.Read8(o + KiShareVector) == 0 || Bus.Read8(k + KiShareVector) == 0))) { Return(0); return; }
        Bus.Write8(k + KiConnected, 1);
        _connectedInterrupts.Add(k);
        Return(1);
    }

    // BOOLEAN KeDisconnectInterrupt(IN PKINTERRUPT)
    private void KeDisconnectInterrupt()
    {
        var k = Arg(0);
        if (!_connectedInterrupts.Remove(k)) { Return(0); return; }
        Bus.Write8(k + KiConnected, 0);
        Return(1);
    }

    // VOID KeInitializeDpc(PKDPC, PKDEFERRED_ROUTINE, PVOID DeferredContext)
    private void KeInitializeDpc()
    {
        var d = Arg(0);
        Bus.Write16(d + KdType, 19); // DpcObject (NT numbering)
        Bus.Write8(d + KdInserted, 0);
        Bus.Write32(d + KdRoutine, Arg(1));
        Bus.Write32(d + KdContext, Arg(2));
        Return(0);
    }

    // BOOLEAN KeInsertQueueDpc(PRKDPC, PVOID SystemArgument1, PVOID SystemArgument2)
    private void KeInsertQueueDpc()
    {
        var d = Arg(0);
        if (Bus.Read8(d + KdInserted) != 0) { Return(0); return; }
        Bus.Write8(d + KdInserted, 1);
        Bus.Write32(d + KdArg1, Arg(1));
        Bus.Write32(d + KdArg2, Arg(2));
        _dpcQueue.Enqueue(d);
        _m.RequestAttention(); // run at the next IRQL drop (= next instruction boundary outside interrupt context)
        Return(1);
    }

    // BOOLEAN KeRemoveQueueDpc(PRKDPC)
    private void KeRemoveQueueDpc()
    {
        var d = Arg(0);
        if (Bus.Read8(d + KdInserted) == 0) { Return(0); return; }
        Bus.Write8(d + KdInserted, 0);
        var remaining = _dpcQueue.Where(x => x != d).ToList();
        _dpcQueue.Clear();
        foreach (var x in remaining) _dpcQueue.Enqueue(x);
        Return(1);
    }

    internal QueuedDpc? DequeueDpc()
    {
        if (_dpcQueue.Count == 0) return null;
        var d = _dpcQueue.Dequeue();
        Bus.Write8(d + KdInserted, 0);
        return new QueuedDpc(d, Bus.Read32(d + KdRoutine), Bus.Read32(d + KdContext), Bus.Read32(d + KdArg1), Bus.Read32(d + KdArg2));
    }

    /// <summary>Calls every connected ISR for the IRQ line: BOOLEAN ServiceRoutine(PKINTERRUPT, PVOID ServiceContext).</summary>
    internal void DeliverInterrupt(int line)
    {
        foreach (var k in _connectedInterrupts.ToList())
        {
            if (Bus.Read32(k + KiBusLevel) != (uint)line) continue;
            Bus.Write32(k + KiServiceCount, Bus.Read32(k + KiServiceCount) + 1);
            var handled = _m.CallGuest(Bus.Read32(k + KiServiceRoutine), k, Bus.Read32(k + KiServiceContext)) & 0xFF;
            if (handled != 0) break;
        }
    }
}
