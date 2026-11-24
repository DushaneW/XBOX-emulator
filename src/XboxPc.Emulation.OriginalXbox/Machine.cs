using System.Diagnostics;
using XboxPc.Emulation.Audio;
using XboxPc.Emulation.Core;
using XboxPc.Emulation.Gpu.Nv2a;
using XboxPc.Emulation.Storage;
using XboxPc.Emulation.Input;
using XboxPc.Emulation.Usb;
using XboxPc.Emulation.X86;

namespace XboxPc.Emulation.OriginalXbox;

public enum MachineStop
{
    BudgetExhausted,
    TitleExited,
    AllThreadsExited,
    AllThreadsBlocked,
    KernelExportUnimplemented,
    KernelDataExportAccessed,
    KernelFeatureUnsupported,
    TitleRaisedException,
    CpuException,
    UnimplementedInstruction,
}

public sealed record MachineRunResult(MachineStop Stop, string Detail, long Instructions, TimeSpan Elapsed, int? KernelOrdinal = null)
{
    /// <summary>Measured interpreter throughput for this run (million instructions per second).</summary>
    public double Mips => Elapsed.TotalSeconds > 0 ? Instructions / Elapsed.TotalSeconds / 1e6 : 0;
}

/// <summary>Raised inside an HLE export to stop the machine with an explicit reason.</summary>
public sealed class MachineStopException(MachineStop stop, string detail, int? ordinal = null) : Exception(detail)
{
    public MachineStopException() : this(MachineStop.KernelFeatureUnsupported, "unsupported") { }

    public MachineStopException(string message) : this(MachineStop.KernelFeatureUnsupported, message) { }

    public MachineStopException(string message, Exception inner) : this(MachineStop.KernelFeatureUnsupported, message + " " + inner.Message) { }

    public MachineStop Stop { get; } = stop;
    public int? Ordinal { get; } = ordinal;
}

/// <summary>
/// Original Xbox machine model (retail, 64 MB) with an HLE kernel and a cooperative, deterministic scheduler.
/// Physical map: RAM at 0 and its kernel view at 0x80000000; NV2A at 0xFD000000 and flash at 0xFF000000 are mapped
/// as reporting regions (not emulated). No paging: title addresses are physical.
/// Time is virtual: 1 retired instruction = 1 cycle of a 733.33 MHz CPU; when every thread sleeps the clock jumps
/// to the next deadline.
/// </summary>
public sealed class OriginalXboxMachine : IInterruptController, IHardwareReport
{
    public const uint RamSize = 64 * 1024 * 1024;
    public const uint KernelRamMirror = 0x8000_0000;
    public const uint Nv2aBase = 0xFD00_0000, Nv2aSize = 0x0100_0000;
    public const uint KernelTrapBase = 0xFE00_0000, KernelTrapSize = 0x1_0000;
    public const uint ThreadExitTrap = KernelTrapBase + 0x8000;
    public const uint FlashBase = 0xFF00_0000, FlashSize = 0x0100_0000;
    public const long CpuHz = 733_333_333;
    public const int Quantum = 20_000;
    public const int MaxIdleSeconds = 30;

    /// <summary>Stack top used by the raw (non-XBE) runner.</summary>
    public const uint DefaultStackTop = 0x03FF_0000;

    private readonly byte[] _ram = new byte[RamSize];
    private readonly List<GuestThread> _threads = [];
    private int _nextThreadId = 1;

    public OriginalXboxMachine()
    {
        Bus.MapRam("RAM", 0, _ram);
        Bus.MapRam("RAM (kernel contiguous view)", KernelRamMirror, _ram);

        Bus.MapDevice(KernelTrapBase, KernelTrapSize, new FaultingDevice("xboxkrnl import trap", "kernel import"));
        Bus.MapDevice(FlashBase, FlashSize, new FaultingDevice("Flash ROM", "no BIOS is used; the kernel is high-level emulated"));
        Cpu = new X86Cpu(Bus, Io) { ReadMsr = ReadMsr };
        Kernel = new KernelHle(this);
        Gpu = new Nv2aDevice(Bus, this, this, CpuHz);
        Bus.MapDevice(Nv2aBase, Nv2aSize, Gpu);
        Gpu.CycleClock = () => Cycles;
        AddClockedDevice(Gpu);
        Usb = new OhciController(Bus, this, UsbIrq, CpuHz, this);
        Bus.MapDevice(UsbBase, 0x1_0000, Usb);
        AddClockedDevice(Usb);
        Audio = new Ac97Controller(Bus, this, AudioIrq, CpuHz, this);
        Bus.MapDevice(AudioBase, 0x1_0000, Audio); // bus granularity is 64 KB; registers occupy the first 0x180 bytes
        AddClockedDevice(Audio);
        Gamepads = Enumerable.Range(0, OhciController.PortCount)
            .Select(port => new XidGamepad(() => Input.Port(port), r => Input.SetRumble(port, r))).ToArray();
        Input.PortConnectionChanged += (_, e) =>
        {
            if (e.Connected) Usb.Attach(e.Port, Gamepads[e.Port]);
            else Usb.Detach(e.Port);
        };
    }

    public const uint AudioBase = 0xFEC0_0000;
    public const int AudioIrq = 6;

    /// <summary>MCPX AC97 controller (PCM and S/PDIF output DMA).</summary>
    public Ac97Controller Audio { get; }

    public const uint UsbBase = 0xFED0_0000;
    public const int UsbIrq = 1;

    /// <summary>USB0 host controller; virtual controller port N is root-hub port N (real consoles route each front port through a hub).</summary>
    public OhciController Usb { get; }

    /// <summary>The XID controllers presented on each port when the input engine reports that port connected.</summary>
    public IReadOnlyList<XidGamepad> Gamepads { get; }

    /// <summary>Host input -> virtual controllers. The host (UI or test) calls <see cref="InputEngine.Update"/>.</summary>
    public InputEngine Input { get; } = new();

    public Nv2aDevice Gpu { get; }

    /// <summary>Host directories behind the Xbox devices. Configure before running (see <see cref="MountDefaults"/>).</summary>
    public VirtualStorage Storage { get; } = new();

    /// <summary>File name reported in XeImageFileName (\Device\CdRom0\&lt;name&gt;).</summary>
    public string ImageFileName { get; set; } = "default.xbe";

    /// <summary>Human-readable log of file-system activity (for run reports).</summary>
    public List<string> FileActivity { get; } = [];

    /// <summary>
    /// Mounts the disc drive (\Device\CdRom0, read-only) at the XBE's directory and, optionally, the data partition
    /// (\Device\Harddisk0\Partition1, "E:") at a writable sandbox directory chosen by the user.
    /// </summary>
    public void MountDefaults(string discDirectory, string? harddiskDirectory)
    {
        Storage.MountDirectory(@"\Device\CdRom0", discDirectory, readOnly: true, "DVD drive", clusterSize: 2048);
        if (harddiskDirectory is not null)
            Storage.MountDirectory(@"\Device\Harddisk0\Partition1", harddiskDirectory, readOnly: false, "Harddisk partition 1 (E:)");
    }

    /// <summary>
    /// Emulated console AV configuration returned by AvSendTVEncoderOption(GET_SETTINGS): AV pack (low byte),
    /// region (second byte), allowed HD modes and refresh flags. Default: HDTV component pack, NTSC-M, 480p and
    /// 720p enabled, 60 Hz. This is a user-facing machine setting, not data read from any console.
    /// </summary>
    public uint VideoEncoderSettings { get; set; } = 0x04 | 0x100 | 0x80000 | 0x20000 | 0x400000;

    /// <summary>Accesses to hardware state that is not modelled and was answered with a documented default.</summary>
    public List<string> UnmodelledAccesses { get; } = [];

    void IHardwareReport.Unmodelled(string message)
    {
        if (UnmodelledAccesses.Count < 10_000) UnmodelledAccesses.Add(message);
    }

    /// <summary>
    /// MSRs of the Xbox's Pentium III that this model can justify:
    /// 0x10 TSC = virtual cycle count; 0x2A EBL_CR_POWERON = only the bus-ratio field (bits 22-27), set to the 5.5x
    /// encoding from Intel's Pentium III datasheet, because the Xbox CPU runs at 733.33 MHz on a 133.33 MHz bus.
    /// The remaining EBL_CR_POWERON bits are not known and read as 0.
    /// </summary>
    private ulong? ReadMsr(uint msr) => msr switch
    {
        0x10 => (ulong)Cycles,
        0x2A => 0b000100UL << 22,
        _ => null,
    };

    public PhysicalBus Bus { get; } = new();
    public IoPortBus Io { get; } = new();
    public X86Cpu Cpu { get; }
    public KernelHle Kernel { get; }
    public GuestMemory Memory { get; private set; } = new(0x0010_0000, RamSize);
    public XbeImage? Image { get; private set; }
    public uint Kpcr { get; private set; }
    public IReadOnlyList<GuestThread> Threads => _threads;
    public GuestThread? Current { get; private set; }

    /// <summary>Virtual CPU cycles since boot (instructions + idle time skipped while all threads slept).</summary>
    public long Cycles { get; private set; }

    public long IdleCyclesSkipped { get; private set; }

    /// <summary>Kernel ordinals imported by the loaded title, in thunk-table order.</summary>
    public IReadOnlyList<int> ImportedOrdinals { get; private set; } = [];

    public static uint TrapAddress(int ordinal) => KernelTrapBase + ((uint)ordinal * 4);

    /// <summary>System time in 100 ns units: fixed epoch (2001-11-15 00:00 UTC, for reproducibility) + virtual time.</summary>
    public long SystemTime100ns => new DateTime(2001, 11, 15, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc() + (Cycles * 10_000_000 / CpuHz);

    public static long CyclesFrom100ns(long units) => units * CpuHz / 10_000_000;

    // ------------------------------------------------------------------ loading

    /// <summary>Maps the image, resolves kernel imports, builds the KPCR and the title's initial thread.</summary>
    public void Load(XbeImage image, ReadOnlySpan<byte> file)
    {
        ArgumentNullException.ThrowIfNull(image);
        if ((ulong)image.BaseAddress + image.SizeOfImage > RamSize) throw new XbeFormatException("Image does not fit in 64 MB of RAM.");
        Bus.WriteBytes(image.BaseAddress, file[..(int)image.SizeOfHeaders]);
        foreach (var s in image.Sections)
        {
            Array.Clear(_ram, (int)s.VirtualAddress, (int)s.VirtualSize);
            Bus.WriteBytes(s.VirtualAddress, file.Slice((int)s.RawAddress, (int)s.RawSize));
        }

        Memory = new GuestMemory(0x0001_0000, RamSize);
        Memory.Reserve(image.BaseAddress, image.SizeOfImage, "XBE image");
        Memory.Reserve(RamSize - KernelHle.GpuInstanceMemorySize, KernelHle.GpuInstanceMemorySize, "GPU instance memory");
        Kernel.InitializeDataExports();

        var ordinals = new List<int>();
        for (var slot = image.KernelThunkAddress; ; slot += 4)
        {
            if (ordinals.Count > 1024) throw new XbeFormatException("Kernel thunk table is not terminated.");
            var entry = Bus.Read32(slot);
            if (entry == 0) break;
            if ((entry & 0x8000_0000) == 0) throw new XbeFormatException($"Kernel thunk entry 0x{entry:X8} is not an ordinal import.");
            var ordinal = (int)(entry & 0x7FFF_FFFF);
            if (ordinal is <= 0 or >= 0x2000) throw new XbeFormatException($"Kernel ordinal {ordinal} is out of range.");
            ordinals.Add(ordinal);
            Bus.Write32(slot, Kernel.ResolveImport(ordinal));
        }
        ImportedOrdinals = ordinals;
        Image = image;
        AddClockedDevice(new KernelClock(this));

        Kpcr = Allocate(GuestLayout.KpcrSize, "KPCR");
        _interruptStackTop = Allocate(0x1_0000, "interrupt/DPC stack") + 0x1_0000 - 16;
        Bus.Write32(Kpcr + GuestLayout.KpcrExceptionList, 0xFFFF_FFFF);
        Bus.Write32(Kpcr + GuestLayout.KpcrSelf, Kpcr);
        Bus.Write32(Kpcr + GuestLayout.KpcrSelfPcr, Kpcr);
        Bus.Write32(Kpcr + GuestLayout.KpcrPrcb, Kpcr + GuestLayout.KpcrCurrentThread);

        var (tlsSize, tlsTemplate) = ReadTlsDirectory(image);
        var stackSize = Math.Max(image.StackCommit, 0x1_0000u);
        var main = CreateThread(stackSize, tlsSize, image.EntryPoint, [], tlsTemplate);
        Current = main;
        Cpu.RestoreContext(main.Context);
        WriteKpcrFor(main);
    }

    /// <summary>Reads IMAGE_TLS_DIRECTORY_32 (Start, End, AddressOfIndex, Callbacks, SizeOfZeroFill).</summary>
    private (uint Size, (uint Start, uint Length)? Template) ReadTlsDirectory(XbeImage image)
    {
        if (image.TlsAddress == 0) return (0, null);
        var start = Bus.Read32(image.TlsAddress);
        var end = Bus.Read32(image.TlsAddress + 4);
        var zeroFill = Bus.Read32(image.TlsAddress + 16);
        var size = GuestMemory.Align(end - start + zeroFill, 16) + 4;
        return (size, (start, end - start));
    }

    public uint Allocate(uint size, string tag, uint alignment = GuestMemory.PageSize)
    {
        var a = Memory.Allocate(size, tag, alignment) ?? throw new MachineStopException(MachineStop.KernelFeatureUnsupported, $"Out of emulated RAM allocating {size} bytes for {tag}.");
        Array.Clear(_ram, (int)a, (int)GuestMemory.Align(size, GuestMemory.PageSize));
        return a;
    }

    public void ZeroGuest(uint address, uint size) => Array.Clear(_ram, (int)(address & ~KernelRamMirror), (int)size);

    /// <summary>
    /// Creates a thread: stack, ETHREAD/KTHREAD block, optional TLS block (TlsData+4 16-byte aligned, first dword
    /// self-reference initialised by the title's own startup code), and an initial context that pushes
    /// <paramref name="args"/> (stdcall) and returns into the thread-exit trap.
    /// </summary>
    public GuestThread CreateThread(uint stackSize, uint tlsSize, uint startEip, uint[] args, (uint Start, uint Length)? tlsTemplate = null)
    {
        stackSize = GuestMemory.Align(Math.Max(stackSize, 0x4000u), GuestMemory.PageSize);
        var stackLimit = Allocate(stackSize, "thread stack");
        var stackBase = stackLimit + stackSize;
        var ethread = Allocate(GuestLayout.EThreadSize, "ETHREAD");
        uint tls = 0;
        if (tlsSize > 0)
        {
            var block = Allocate(tlsSize + 32, "TLS");
            tls = block + 12; // (tls + 4) % 16 == 0, as asserted by nxdk's WinapiThreadStartup
            if (tlsTemplate is { } t)
            {
                // Initial thread: the kernel-side copy of the TLS template (new threads copy it themselves in nxdk).
                Bus.Write32(tls, tls + 4);
                Bus.WriteBytes(tls + 4, Bus.ReadBytes(t.Start, (int)t.Length));
            }
        }

        var thread = new GuestThread { Id = _nextThreadId++, EThread = ethread, StackBase = stackBase, StackLimit = stackLimit, TlsData = tls };
        Bus.Write32(ethread + GuestLayout.KThreadStackBase, stackBase);
        Bus.Write32(ethread + GuestLayout.KThreadStackLimit, stackLimit);
        Bus.Write32(ethread + GuestLayout.KThreadTlsData, tls);
        Bus.Write32(ethread + GuestLayout.EThreadUniqueThread, (uint)thread.Id);
        Bus.Write32(ethread + GuestLayout.EThreadStartAddress, startEip);

        var esp = stackBase - 16;
        for (var i = args.Length - 1; i >= 0; i--) { esp -= 4; Bus.Write32(esp, args[i]); }
        esp -= 4;
        Bus.Write32(esp, ThreadExitTrap);
        thread.Context.Regs[X86Cpu.ESP] = esp;
        thread.Context.Eip = startEip;
        thread.Context.FsBase = Kpcr;
        _threads.Add(thread);
        return thread;
    }

    private void WriteKpcrFor(GuestThread t)
    {
        Bus.Write32(Kpcr + GuestLayout.KpcrStackBase, t.StackBase);
        Bus.Write32(Kpcr + GuestLayout.KpcrStackLimit, t.StackLimit);
        Bus.Write32(Kpcr + GuestLayout.KpcrCurrentThread, t.EThread);
    }

    // ------------------------------------------------------------------ threads and waits

    public void TerminateCurrent(uint exitStatus)
    {
        var t = Current!;
        t.State = ThreadState.Terminated;
        t.ExitStatus = exitStatus;
        Bus.Write8(t.EThread + GuestLayout.KThreadHasTerminated, 1);
        Bus.Write32(t.EThread + GuestLayout.EThreadExitStatus, exitStatus);
        Kernel.OnThreadTerminated(t);
        SwitchRequested = true;
    }

    internal bool SwitchRequested { get; set; }

    /// <summary>
    /// Starts a wait for the current thread. Returns the completion status if it can be satisfied now
    /// (or times out immediately); otherwise blocks the thread and returns null (EAX is written on wake-up).
    /// </summary>
    public uint? BeginWait(IReadOnlyList<DispatcherObject> objects, bool waitAll, long? timeout100ns)
    {
        var t = Current!;
        if (TrySatisfy(t, objects, waitAll) is { } status) return status;
        long? deadline = null;
        if (timeout100ns is { } to)
        {
            if (to == 0) return NtStatus.Timeout;
            deadline = to < 0 ? Cycles + CyclesFrom100ns(-to) : Cycles + Math.Max(0, CyclesFrom100ns(to - SystemTime100ns));
        }
        t.WaitObjects = objects;
        t.WaitAll = waitAll;
        t.WaitDeadline = deadline;
        t.State = ThreadState.Waiting;
        SwitchRequested = true;
        return null;
    }

    private static uint? TrySatisfy(GuestThread t, IReadOnlyList<DispatcherObject> objects, bool waitAll)
    {
        if (objects.Count == 0) return null; // pure delay
        if (waitAll)
        {
            if (!objects.All(o => o.IsSignaledFor(t))) return null;
            var abandoned = objects.OfType<Mutant>().Any(m => m.Abandoned && m.Owner is null);
            foreach (var o in objects) AcquireOne(o, t);
            return abandoned ? NtStatus.Abandoned : NtStatus.Wait0;
        }
        for (var i = 0; i < objects.Count; i++)
        {
            if (!objects[i].IsSignaledFor(t)) continue;
            var abandoned = objects[i] is Mutant { Abandoned: true, Owner: null };
            AcquireOne(objects[i], t);
            return (uint)i + (abandoned ? NtStatus.Abandoned : NtStatus.Wait0);
        }
        return null;
    }

    private static void AcquireOne(DispatcherObject o, GuestThread t)
    {
        if (o is Mutant m) m.Abandoned = false;
        o.Acquire(t);
    }

    /// <summary>Completes waits that can now be satisfied or have timed out.</summary>
    private void ProcessWaits()
    {
        foreach (var t in _threads)
        {
            if (t.State != ThreadState.Waiting) continue;
            uint? status = t.WaitObjects is { Count: > 0 } objs ? TrySatisfy(t, objs, t.WaitAll) : null;
            if (status is null && t.WaitDeadline is { } d && Cycles >= d) status = t.WaitObjects is { Count: > 0 } ? NtStatus.Timeout : NtStatus.Success;
            if (status is null) continue;
            t.Context.Regs[X86Cpu.EAX] = status.Value;
            t.WaitObjects = null;
            t.WaitDeadline = null;
            t.State = ThreadState.Ready;
            Kernel.CompletePendingCriticalSection(t);
        }
    }

    // ------------------------------------------------------------------ devices, interrupts, DPCs

    private readonly List<IClockedDevice> _clockedDevices = [];
    private uint _pendingIrqs;
    private long _attention = long.MaxValue;
    private int _nesting;
    private uint _interruptStackTop;

    public const uint InterruptReturnTrap = KernelTrapBase + 0x8004;

    public void AddClockedDevice(IClockedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _clockedDevices.Add(device);
        RecomputeAttention();
    }

    /// <summary>Asserts an interrupt line (Xbox PIC IRQ 0-15). Delivered at the next instruction boundary.</summary>
    public void RaiseIrq(int line)
    {
        _pendingIrqs |= 1u << line;
        _attention = 0;
    }

    internal void RequestAttention() => _attention = 0;

    public long InterruptsDelivered { get; private set; }
    public long DpcsExecuted { get; private set; }

    private void RecomputeAttention()
    {
        var next = long.MaxValue;
        foreach (var d in _clockedDevices) next = Math.Min(next, d.NextEventCycle);
        if (_pendingIrqs != 0 || Kernel.HasQueuedDpcs) next = 0;
        _attention = next;
    }

    /// <summary>Advances device timers, delivers pending interrupts and runs queued DPCs.</summary>
    private void ServiceHardware()
    {
        foreach (var d in _clockedDevices)
            if (Cycles >= d.NextEventCycle) d.OnEvent(Cycles);
        if (_nesting == 0)
        {
            var irqs = _pendingIrqs;
            _pendingIrqs = 0;
            for (var line = 0; irqs != 0; line++, irqs >>= 1)
            {
                if ((irqs & 1) == 0) continue;
                InterruptsDelivered++;
                Kernel.DeliverInterrupt(line);
            }
            while (Kernel.DequeueDpc() is { } dpc)
            {
                DpcsExecuted++;
                CallGuest(dpc.Routine, dpc.Dpc, dpc.Context, dpc.Arg1, dpc.Arg2);
            }
        }
        RecomputeAttention();
    }

    /// <summary>
    /// Runs a guest function (ISR or DPC routine, stdcall) to completion on the dedicated interrupt stack, without
    /// thread switches, and restores the interrupted CPU state afterwards. A blocking wait inside is an error, as on
    /// real hardware at DISPATCH_LEVEL or above.
    /// </summary>
    internal uint CallGuest(uint function, params uint[] args)
    {
        var saved = new CpuContext();
        Cpu.SaveContext(saved);
        var savedSwitch = SwitchRequested;
        var esp = _interruptStackTop;
        for (var i = args.Length - 1; i >= 0; i--) { esp -= 4; Bus.Write32(esp, args[i]); }
        esp -= 4;
        Bus.Write32(esp, InterruptReturnTrap);
        Cpu[X86Cpu.ESP] = esp;
        Cpu.Eip = function;
        _nesting++;
        uint result = 0;
        try
        {
            for (long n = 0; ; n++)
            {
                if (n > 50_000_000) throw new MachineStopException(MachineStop.KernelFeatureUnsupported, $"Interrupt/DPC routine 0x{function:X8} did not return within 50M instructions.");
                try
                {
                    Cpu.Step();
                    Cycles++;
                }
                catch (X86Exception e) when (e.BusFault is { } bf && bf.Address >= KernelTrapBase && bf.Address < KernelTrapBase + KernelTrapSize)
                {
                    if (bf.Address == InterruptReturnTrap && bf.Kind == AccessKind.Fetch) { result = Cpu[X86Cpu.EAX]; break; }
                    var ordinal = (int)((bf.Address - KernelTrapBase) / 4);
                    if (bf.Kind != AccessKind.Fetch || bf.Address == ThreadExitTrap)
                        throw new MachineStopException(MachineStop.KernelFeatureUnsupported, $"Interrupt/DPC routine 0x{function:X8} touched trap 0x{bf.Address:X8} ({KernelHle.NameOf(ordinal)}) in an unsupported way.", ordinal);
                    var stateBefore = Current?.State;
                    Kernel.Dispatch(ordinal);
                    if (Current is { } c && stateBefore == ThreadState.Ready && c.State != ThreadState.Ready)
                        throw new MachineStopException(MachineStop.KernelFeatureUnsupported, $"{KernelHle.NameOf(ordinal)} would block inside an interrupt/DPC routine.", ordinal);
                }
            }
        }
        finally
        {
            _nesting--;
            Cpu.RestoreContext(saved);
            SwitchRequested = savedSwitch;
        }
        return result;
    }

    /// <summary>Picks the next ready thread (round robin). Returns a stop result when nothing can run.</summary>
    private MachineRunResult? Schedule(Func<MachineStop, string, MachineRunResult> done)
    {
        if (Current is { } cur) Cpu.SaveContext(cur.Context);
        var idleStart = Cycles;

        while (true)
        {
            ProcessWaits();
            var start = Current is null ? 0 : _threads.IndexOf(Current) + 1;
            for (var k = 0; k < _threads.Count; k++)
            {
                var t = _threads[(start + k) % _threads.Count];
                if (t.State != ThreadState.Ready) continue;
                Current = t;
                Cpu.RestoreContext(t.Context);
                WriteKpcrFor(t);
                return null;
            }
            if (_threads.All(t => t.State == ThreadState.Terminated))
                return done(MachineStop.AllThreadsExited, "Every thread of the title has terminated without calling HalReturnToFirmware.");
            var next = long.MaxValue;
            foreach (var t in _threads)
                if (t.State == ThreadState.Waiting && t.WaitDeadline is { } d) next = Math.Min(next, d);
            foreach (var dev in _clockedDevices) next = Math.Min(next, dev.NextEventCycle);
            if (next == long.MaxValue)
            {
                var waits = string.Join("; ", _threads.Where(t => t.State == ThreadState.Waiting).Select(t => $"thread {t.Id} on {string.Join(",", t.WaitObjects?.Select(o => o.Kind) ?? [])}"));
                return done(MachineStop.AllThreadsBlocked, $"All threads are blocked with no timeout ({waits}) and no device will raise an interrupt. Either a deadlock, or the title waits for hardware that is not emulated.");
            }
            // Idle: jump virtual time to the next deadline or device event, then let devices raise interrupts.
            // Device events only help if a guest interrupt handler is connected; bound the idle time either way.
            var threadDeadline = _threads.Where(t => t.State == ThreadState.Waiting && t.WaitDeadline is not null).Select(t => t.WaitDeadline!.Value).DefaultIfEmpty(long.MaxValue).Min();
            if (threadDeadline == long.MaxValue && (Kernel.ConnectedInterrupts.Count == 0 || Cycles - idleStart > CpuHz * MaxIdleSeconds))
            {
                var waits = string.Join("; ", _threads.Where(t => t.State == ThreadState.Waiting).Select(t => $"thread {t.Id} on {string.Join(",", t.WaitObjects?.Select(o => o.Kind) ?? [])}"));
                return done(MachineStop.AllThreadsBlocked, Kernel.ConnectedInterrupts.Count == 0
                    ? $"All threads are blocked with no timeout ({waits}) and no interrupt handler is connected. Either a deadlock, or the title waits for hardware that is not emulated."
                    : $"All threads stayed blocked ({waits}) for {MaxIdleSeconds} virtual seconds although interrupts were delivered; the awaited event never happened.");
            }
            // Time must always advance while idle, otherwise a device event at the current cycle would spin forever.
            if (next > Cycles) { IdleCyclesSkipped += next - Cycles; Cycles = next; }
            else { IdleCyclesSkipped++; Cycles++; }
            ServiceHardware();
            if (StopRequested) return done(MachineStop.BudgetExhausted, "Stop requested by the host.");
            if (Cycles >= CycleLimit) return done(MachineStop.BudgetExhausted, "Virtual time limit reached while idle.");
        }
    }

    /// <summary>Set by the host (UI) to make <see cref="Run"/> return at the next opportunity.</summary>
    private volatile bool _stopRequested;

    public bool StopRequested { get => _stopRequested; set => _stopRequested = value; }

    /// <summary>
    /// Virtual-time bound for <see cref="Run"/>: it returns <see cref="MachineStop.BudgetExhausted"/> once
    /// <see cref="Cycles"/> reaches this value (also while idle). Hosts use it to pace emulation to real time.
    /// </summary>
    public long CycleLimit { get; set; } = long.MaxValue;

    // ------------------------------------------------------------------ execution

    public MachineRunResult Run(long maxInstructions)
    {
        var clock = Stopwatch.StartNew();
        var startRetired = Cpu.Retired;
        MachineRunResult Done(MachineStop stop, string detail, int? ordinal = null) => new(stop, detail, Cpu.Retired - startRetired, clock.Elapsed, ordinal);
        MachineRunResult Fin(MachineStop s, string d) => Done(s, d);

        if (Current is null) return Done(MachineStop.AllThreadsExited, "No title loaded.");
        var sliceLeft = Quantum;
        while (Cpu.Retired - startRetired < maxInstructions)
        {
            try
            {
                if (Cycles >= _attention)
                {
                    if (Current.State == ThreadState.Ready) Cpu.SaveContext(Current.Context);
                    ServiceHardware();
                    if (StopRequested) return Done(MachineStop.BudgetExhausted, "Stop requested by the host.");
                    if (Cycles >= CycleLimit) return Done(MachineStop.BudgetExhausted, "Virtual time limit reached.");
                }
                if (SwitchRequested || sliceLeft <= 0 || Current.State != ThreadState.Ready)
                {
                    SwitchRequested = false;
                    sliceLeft = Quantum;
                    if (Schedule(Fin) is { } stopped) return stopped;
                }
                Cpu.Step();
                Cycles++;
                sliceLeft--;
                if (Cpu.Halted)
                {
                    if (!Cpu.GetFlag(X86Cpu.IF) || _clockedDevices.Count == 0)
                        return Done(MachineStop.CpuException, $"HLT executed by thread {Current.Id} at 0x{Cpu.Eip:X8} with interrupts {(Cpu.GetFlag(X86Cpu.IF) ? "enabled but no interrupt source" : "disabled")}.");
                    Cpu.Halted = false; // HLT with IF=1: idle until the next device event
                    var next = _clockedDevices.Min(d => d.NextEventCycle);
                    if (next != long.MaxValue && next > Cycles) { IdleCyclesSkipped += next - Cycles; Cycles = next; }
                }
            }
            catch (X86Exception e) when (e.BusFault is { } bf && bf.Address >= KernelTrapBase && bf.Address < KernelTrapBase + KernelTrapSize)
            {
                if (bf.Address == ThreadExitTrap && bf.Kind == AccessKind.Fetch)
                {
                    // A thread routine returned: its return value (EAX) is the exit status.
                    Kernel.Record(0, "<thread routine returned>", [Cpu[X86Cpu.EAX]]);
                    TerminateCurrent(Cpu[X86Cpu.EAX]);
                    continue;
                }
                var ordinal = (int)((bf.Address - KernelTrapBase) / 4);
                if (bf.Kind != AccessKind.Fetch)
                    return Done(MachineStop.KernelDataExportAccessed, $"Thread {Current.Id} accessed kernel data export #{ordinal} {KernelHle.NameOf(ordinal)} at EIP 0x{Cpu.Eip:X8}; this data export is not emulated.", ordinal);
                try
                {
                    Kernel.Dispatch(ordinal);
                }
                catch (MachineStopException stop)
                {
                    return Done(stop.Stop, stop.Message, stop.Ordinal ?? ordinal);
                }
                catch (X86Exception inner)
                {
                    return Done(MachineStop.CpuException, $"While executing kernel export #{ordinal} {KernelHle.NameOf(ordinal)}: {inner.Message}", ordinal);
                }
            }
            catch (X86Exception e)
            {
                return Done(MachineStop.CpuException, $"Thread {Current.Id}: {e.Message}");
            }
            catch (UnimplementedInstructionException e)
            {
                return Done(MachineStop.UnimplementedInstruction, $"Thread {Current.Id}: {e.Message}");
            }
            catch (MachineStopException stop)
            {
                return Done(stop.Stop, stop.Message, stop.Ordinal);
            }
        }
        return Done(MachineStop.BudgetExhausted, $"Instruction budget reached at EIP 0x{Cpu.Eip:X8} (thread {Current.Id}).");
    }
}

/// <summary>
/// The 1 ms system clock interrupt's visible effect under HLE: keeps KeTickCount, KeInterruptTime and KeSystemTime in
/// guest memory current with virtual time (titles read them directly, e.g. nxdk's GetTickCount).
/// </summary>
internal sealed class KernelClock(OriginalXboxMachine m) : IClockedDevice
{
    private long _next;

    public long NextEventCycle => _next;

    public void OnEvent(long nowCycles)
    {
        var bus = m.Bus;
        var ms = nowCycles * 1000 / OriginalXboxMachine.CpuHz;
        bus.Write32(m.Kernel.TickCountAddress, (uint)ms);
        WriteSystemTime(m.Kernel.InterruptTimeAddress, nowCycles * 10_000_000 / OriginalXboxMachine.CpuHz);
        WriteSystemTime(m.Kernel.SystemTimeAddress, m.SystemTime100ns);
        // first cycle of the next millisecond, rounded up so it is always strictly in the future
        _next = Math.Max(nowCycles + 1, (((ms + 1) * OriginalXboxMachine.CpuHz) + 999) / 1000);

        void WriteSystemTime(uint a, long v)
        {
            bus.Write32(a + 4, (uint)((ulong)v >> 32)); // High1Time, then LowPart, then High2Time (reader consistency order)
            bus.Write32(a, (uint)v);
            bus.Write32(a + 8, (uint)((ulong)v >> 32));
        }
    }
}
