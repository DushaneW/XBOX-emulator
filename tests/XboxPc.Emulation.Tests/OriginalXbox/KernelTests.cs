using System.Security.Cryptography;
using XboxPc.Emulation.OriginalXbox;
using ThreadState = XboxPc.Emulation.OriginalXbox.ThreadState;
using XboxPc.Emulation.X86;

namespace XboxPc.Emulation.Tests.OriginalXbox;

/// <summary>
/// Calls kernel exports through the real guest path: arguments are placed on the guest stack (ECX/EDX for fastcall),
/// the CPU jumps to the import trap, and the export returns to an INT3 stub. Every call also verifies that the stack
/// was cleaned up exactly as the export's calling convention requires.
/// </summary>
public sealed class KernelHarness
{
    public OriginalXboxMachine M { get; } = new();
    public uint Stub { get; }
    public uint Scratch { get; }

    public KernelHarness()
    {
        var b = new XbeBuilder();
        b.Emit(0xCC);
        var file = b.Build();
        M.Load(XbeImage.Parse(file), file);
        Stub = M.Allocate(0x1000, "test stub");
        M.Bus.Write8(Stub, 0xCC); // int3
        Scratch = M.Allocate(0x1_0000, "test scratch");
    }

    /// <summary>Places guest code and returns its address.</summary>
    public uint Code(params byte[] bytes)
    {
        var a = M.Allocate((uint)bytes.Length, "test code");
        M.Bus.WriteBytes(a, bytes);
        return a;
    }

    /// <summary>Calls an export from the machine's current thread; runs until it returns. Returns EAX.</summary>
    public uint Call(int ordinal, params uint[] args)
    {
        var info = KernelExports.ByOrdinal[ordinal];
        var cpu = M.Cpu;
        var stackArgs = args;
        if (info.Kind == KernelExportKind.Fastcall)
        {
            if (args.Length > 0) cpu[X86Cpu.ECX] = args[0];
            if (args.Length > 1) cpu[X86Cpu.EDX] = args[1];
            stackArgs = args.Skip(2).ToArray();
        }
        var espBefore = cpu[X86Cpu.ESP];
        for (var i = stackArgs.Length - 1; i >= 0; i--) { cpu[X86Cpu.ESP] -= 4; M.Bus.Write32(cpu[X86Cpu.ESP], stackArgs[i]); }
        cpu[X86Cpu.ESP] -= 4;
        M.Bus.Write32(cpu[X86Cpu.ESP], Stub);
        cpu.Eip = OriginalXboxMachine.TrapAddress(ordinal);
        var r = M.Run(10_000_000);
        Assert.True(r.Stop == MachineStop.CpuException && r.Detail.Contains("INT3", StringComparison.Ordinal), $"#{ordinal} {info.Name}: {r.Stop} {r.Detail}");
        cpu.Eip = Stub; // INT3 is a trap; rewind so the next call starts from a clean state
        var expectedEsp = info.Kind == KernelExportKind.Cdecl ? espBefore - (uint)(stackArgs.Length * 4) : espBefore;
        Assert.Equal(expectedEsp, cpu[X86Cpu.ESP]);
        if (info.Kind == KernelExportKind.Cdecl) cpu[X86Cpu.ESP] = espBefore; // caller cleans up
        return cpu[X86Cpu.EAX];
    }

    /// <summary>Calls and expects the machine to stop with the given reason instead of returning.</summary>
    public MachineRunResult CallExpectingStop(int ordinal, params uint[] args)
    {
        var cpu = M.Cpu;
        for (var i = args.Length - 1; i >= 0; i--) { cpu[X86Cpu.ESP] -= 4; M.Bus.Write32(cpu[X86Cpu.ESP], args[i]); }
        cpu[X86Cpu.ESP] -= 4;
        M.Bus.Write32(cpu[X86Cpu.ESP], Stub);
        cpu.Eip = OriginalXboxMachine.TrapAddress(ordinal);
        return M.Run(10_000_000);
    }

    public uint Handle(int ordinal, params uint[] restArgs)
    {
        var status = Call(ordinal, [Scratch, .. restArgs]);
        Assert.Equal(NtStatus.Success, status);
        return M.Bus.Read32(Scratch);
    }

    public uint U32(uint a) => M.Bus.Read32(a);
}

public class KernelTests
{
    private const int NtClose = 187, NtCreateEvent = 189, NtCreateMutant = 192, NtCreateSemaphore = 193, NtWait = 233, NtWaitEx = 234,
        NtWaitMultiple = 235, NtReleaseMutant = 221, NtReleaseSemaphore = 222, NtSetEvent = 225, NtClearEvent = 186, NtPulseEvent = 205,
        PsCreateSystemThreadEx = 255, KeDelay = 99, KeQuerySystemTime = 128;

    private static uint Timeout(KernelHarness h, long value100ns)
    {
        var p = h.Scratch + 0x100;
        h.M.Bus.Write32(p, (uint)value100ns);
        h.M.Bus.Write32(p + 4, (uint)((ulong)value100ns >> 32));
        return p;
    }

    [Fact]
    public void Mutant_ownership_recursion_and_not_owned()
    {
        var h = new KernelHarness();
        var m = h.Handle(NtCreateMutant, 0, 1); // initially owned
        Assert.Equal(NtStatus.Wait0, h.Call(NtWait, m, 0, 0)); // recursive acquire
        Assert.Equal(NtStatus.Success, h.Call(NtReleaseMutant, m, h.Scratch + 8));
        Assert.Equal(unchecked((uint)-1), h.U32(h.Scratch + 8)); // previous count: 1 - recursion(2)
        Assert.Equal(NtStatus.Success, h.Call(NtReleaseMutant, m, 0));
        Assert.Equal(NtStatus.MutantNotOwned, h.Call(NtReleaseMutant, m, 0));
        Assert.Equal(NtStatus.Success, h.Call(NtClose, m));
        Assert.Equal(NtStatus.InvalidHandle, h.Call(NtClose, m));
    }

    [Fact]
    public void Notification_and_synchronization_events()
    {
        var h = new KernelHarness();
        var notification = h.Handle(NtCreateEvent, 0, 0, 1);
        var sync = h.Handle(NtCreateEvent, 0, 1, 1);
        var zero = Timeout(h, 0);
        Assert.Equal(NtStatus.Wait0, h.Call(NtWait, notification, 0, zero));
        Assert.Equal(NtStatus.Wait0, h.Call(NtWait, notification, 0, zero)); // stays signaled
        Assert.Equal(NtStatus.Wait0, h.Call(NtWait, sync, 0, zero));
        Assert.Equal(NtStatus.Timeout, h.Call(NtWait, sync, 0, zero));      // auto-reset
        Assert.Equal(NtStatus.Success, h.Call(NtSetEvent, sync, h.Scratch + 8));
        Assert.Equal(0u, h.U32(h.Scratch + 8));                              // previous state
        Assert.Equal(NtStatus.Success, h.Call(NtClearEvent, notification));
        Assert.Equal(NtStatus.Timeout, h.Call(NtWaitEx, notification, 1, 0, zero));
        Assert.Equal(NtStatus.Success, h.Call(NtPulseEvent, notification, 0));
        Assert.Equal(NtStatus.Timeout, h.Call(NtWait, notification, 0, zero)); // pulse with no waiter leaves it reset
        Assert.Equal(NtStatus.InvalidParameter, h.Call(NtCreateEvent, h.Scratch, 0, 7, 0));
    }

    [Fact]
    public void Semaphore_counts_and_limit()
    {
        var h = new KernelHarness();
        var s = h.Handle(NtCreateSemaphore, 0, 1, 2);
        var zero = Timeout(h, 0);
        Assert.Equal(NtStatus.Wait0, h.Call(NtWait, s, 0, zero));
        Assert.Equal(NtStatus.Timeout, h.Call(NtWait, s, 0, zero));
        Assert.Equal(NtStatus.Success, h.Call(NtReleaseSemaphore, s, 2, h.Scratch + 8));
        Assert.Equal(0u, h.U32(h.Scratch + 8));
        Assert.Equal(NtStatus.SemaphoreLimitExceeded, h.Call(NtReleaseSemaphore, s, 1, 0));
        Assert.Equal(NtStatus.InvalidParameter, h.Call(NtCreateSemaphore, h.Scratch, 0, 3, 2));
    }

    [Fact]
    public void Wait_any_returns_index_and_wait_all_needs_every_object()
    {
        var h = new KernelHarness();
        var a = h.Handle(NtCreateEvent, 0, 0, 0);
        var b = h.Handle(NtCreateEvent, 0, 0, 1);
        h.M.Bus.Write32(h.Scratch + 0x20, a);
        h.M.Bus.Write32(h.Scratch + 0x24, b);
        var zero = Timeout(h, 0);
        Assert.Equal(1u, h.Call(NtWaitMultiple, 2, h.Scratch + 0x20, 1, 0, 0, zero)); // WaitAny -> index 1
        Assert.Equal(NtStatus.Timeout, h.Call(NtWaitMultiple, 2, h.Scratch + 0x20, 0, 0, 0, zero)); // WaitAll: a unsignaled
        h.Call(NtSetEvent, a, 0);
        Assert.Equal(NtStatus.Wait0, h.Call(NtWaitMultiple, 2, h.Scratch + 0x20, 0, 0, 0, zero));
    }

    [Fact]
    public void Thread_runs_its_routine_and_waiting_on_it_returns_after_exit()
    {
        var h = new KernelHarness();
        var counter = h.Scratch + 0x40;
        // routine(ctx): inc dword [ctx]; mov eax, 0x1234; ret 4
        var routine = h.Code(0x8B, 0x44, 0x24, 0x04, 0xFF, 0x00, 0xB8, 0x34, 0x12, 0, 0, 0xC2, 0x04, 0x00);
        var status = h.Call(PsCreateSystemThreadEx, h.Scratch, 0, 0x4000, 0, h.Scratch + 4, routine, counter, 0, 0, 0);
        Assert.Equal(NtStatus.Success, status);
        var thread = h.U32(h.Scratch);
        Assert.Equal(0u, h.U32(counter)); // not run yet: cooperative scheduling
        Assert.Equal(NtStatus.Wait0, h.Call(NtWait, thread, 0, 0)); // blocks, thread runs, wakes us
        Assert.Equal(1u, h.U32(counter));
        var t = h.M.Threads.Single(x => x.Id == h.U32(h.Scratch + 4));
        Assert.Equal(ThreadState.Terminated, t.State);
        Assert.Equal(0x1234u, t.ExitStatus);
        Assert.Equal(1, h.M.Bus.Read8(t.EThread + GuestLayout.KThreadHasTerminated));
        Assert.Equal(0x1234u, h.M.Bus.Read32(t.EThread + GuestLayout.EThreadExitStatus));
    }

    [Fact]
    public void System_routine_receives_start_routine_and_context_and_tls_is_aligned()
    {
        var h = new KernelHarness();
        var outp = h.Scratch + 0x80;
        // sysroutine(start, ctx): mov eax,[esp+4]; mov [out],eax; mov eax,[esp+8]; mov [out+4],eax; ret 8
        var sys = h.Code(0x8B, 0x44, 0x24, 0x04, 0xA3, (byte)outp, (byte)(outp >> 8), (byte)(outp >> 16), (byte)(outp >> 24),
            0x8B, 0x44, 0x24, 0x08, 0xA3, (byte)(outp + 4), (byte)((outp + 4) >> 8), (byte)((outp + 4) >> 16), (byte)((outp + 4) >> 24), 0xC2, 0x08, 0x00);
        h.Call(PsCreateSystemThreadEx, h.Scratch, 0, 0x4000, 0x24, 0, 0xAAAA_0000, 0xBBBB_0000, 0, 0, sys);
        h.Call(NtWait, h.U32(h.Scratch), 0, 0);
        Assert.Equal(0xAAAA_0000u, h.U32(outp));
        Assert.Equal(0xBBBB_0000u, h.U32(outp + 4));
        var t = h.M.Threads[^1];
        Assert.Equal(0u, (t.TlsData + 4) % 16);
        Assert.Equal(t.TlsData, h.M.Bus.Read32(t.EThread + GuestLayout.KThreadTlsData));
    }

    [Fact]
    public void Mutex_contention_blocks_until_owner_releases()
    {
        var h = new KernelHarness();
        var m = h.Handle(NtCreateMutant, 0, 1); // main owns
        var result = h.Scratch + 0x60;
        var trap = OriginalXboxMachine.TrapAddress(NtWait);
        // routine(ctx): push 0; push 0; push m; mov eax,trap; call eax; mov [result],eax; ret 4
        var routine = h.Code(0x6A, 0, 0x6A, 0, 0x68, (byte)m, (byte)(m >> 8), (byte)(m >> 16), (byte)(m >> 24),
            0xB8, (byte)trap, (byte)(trap >> 8), (byte)(trap >> 16), (byte)(trap >> 24), 0xFF, 0xD0,
            0xA3, (byte)result, (byte)(result >> 8), (byte)(result >> 16), (byte)(result >> 24), 0xC2, 0x04, 0x00);
        h.M.Bus.Write32(result, 0xDEAD);
        h.Call(PsCreateSystemThreadEx, h.Scratch, 0, 0x4000, 0, 0, routine, 0, 0, 0, 0);
        var thread = h.U32(h.Scratch);
        Assert.Equal(NtStatus.Success, h.Call(238)); // NtYieldExecution: other thread runs and blocks on the mutex
        Assert.Equal(0xDEADu, h.U32(result));
        Assert.Equal(NtStatus.Success, h.Call(NtReleaseMutant, m, 0));
        Assert.Equal(NtStatus.Wait0, h.Call(NtWait, thread, 0, 0));
        Assert.Equal(NtStatus.Wait0, h.U32(result));
    }

    [Fact]
    public void Delay_advances_virtual_time_without_burning_instructions()
    {
        var h = new KernelHarness();
        h.Call(KeQuerySystemTime, h.Scratch);
        var before = h.U32(h.Scratch) | ((ulong)h.U32(h.Scratch + 4) << 32);
        var executed = h.M.Cpu.Retired;
        Assert.Equal(NtStatus.Success, h.Call(KeDelay, 0, 0, Timeout(h, -10_000_000))); // 1 s relative
        h.Call(KeQuerySystemTime, h.Scratch);
        var after = h.U32(h.Scratch) | ((ulong)h.U32(h.Scratch + 4) << 32);
        Assert.InRange((long)(after - before), 10_000_000, 10_000_100);
        Assert.True(h.M.Cpu.Retired - executed < 100);
        Assert.True(h.M.IdleCyclesSkipped >= OriginalXboxMachine.CpuHz - 1000);
    }

    [Fact]
    public void Waiting_forever_with_no_other_thread_is_reported_as_blocked()
    {
        var h = new KernelHarness();
        var e = h.Handle(NtCreateEvent, 0, 0, 0);
        var r = h.CallExpectingStop(NtWait, e, 0, 0);
        Assert.Equal(MachineStop.AllThreadsBlocked, r.Stop);
        Assert.Contains("NotificationEvent", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Timed_wait_times_out_in_virtual_time()
    {
        var h = new KernelHarness();
        var e = h.Handle(NtCreateEvent, 0, 0, 0);
        Assert.Equal(NtStatus.Timeout, h.Call(NtWait, e, 0, Timeout(h, -5000)));
    }

    [Fact]
    public void Critical_section_recursion_and_try_enter()
    {
        var h = new KernelHarness();
        var cs = h.Scratch + 0x200;
        h.Call(291, cs);                                 // RtlInitializeCriticalSection
        Assert.Equal(0xFFFF_FFFFu, h.U32(cs + GuestLayout.CsLockCount));
        h.Call(277, cs);                                 // Enter
        h.Call(277, cs);                                 // Enter (recursive)
        Assert.Equal(2u, h.U32(cs + GuestLayout.CsRecursionCount));
        Assert.Equal(h.M.Current!.EThread, h.U32(cs + GuestLayout.CsOwningThread));
        Assert.Equal(1u, h.Call(306, cs));               // TryEnter by owner succeeds
        h.Call(294, cs); h.Call(294, cs); h.Call(294, cs);
        Assert.Equal(0u, h.U32(cs + GuestLayout.CsOwningThread));
        Assert.Equal(0xFFFF_FFFFu, h.U32(cs + GuestLayout.CsLockCount));
    }

    [Fact]
    public void Sha1_matches_dotnet_reference_with_incremental_updates()
    {
        var h = new KernelHarness();
        var rng = new Random(3);
        var ctx = h.Scratch + 0x400;
        var data = h.Scratch + 0x1000;
        foreach (var total in new[] { 0, 1, 55, 56, 63, 64, 65, 1000, 4096 })
        {
            var bytes = new byte[total];
            rng.NextBytes(bytes);
            h.M.Bus.WriteBytes(data, bytes);
            h.Call(335, ctx);
            var offset = 0;
            while (offset < total)
            {
                var chunk = Math.Min(total - offset, rng.Next(1, 100));
                h.Call(336, ctx, data + (uint)offset, (uint)chunk);
                offset += chunk;
            }
            h.Call(337, ctx, h.Scratch + 0x500);
#pragma warning disable CA5350 // the Xbox kernel API (XcSHA*) is SHA-1 by definition; .NET's SHA-1 is the reference
            Assert.Equal(SHA1.HashData(bytes), h.M.Bus.ReadBytes(h.Scratch + 0x500, 20));
#pragma warning restore CA5350
        }
    }

    [Fact]
    public void Fastcall_interlocked_operations_use_ecx_edx_and_clean_stack_arguments()
    {
        var h = new KernelHarness();
        var v = h.Scratch + 0x30;
        h.M.Bus.Write32(v, 5);
        Assert.Equal(5u, h.Call(54, v, 9));             // InterlockedExchange -> old
        Assert.Equal(9u, h.U32(v));
        Assert.Equal(9u, h.Call(51, v, 1, 9));          // InterlockedCompareExchange(dest, exch, comparand on stack)
        Assert.Equal(1u, h.U32(v));
        Assert.Equal(1u, h.Call(51, v, 7, 9));          // comparand mismatch: unchanged
        Assert.Equal(1u, h.U32(v));
        Assert.Equal(2u, h.Call(53, v));                // InterlockedIncrement -> new
        Assert.Equal(1u, h.Call(52, v));                // InterlockedDecrement -> new
        Assert.Equal(1u, h.Call(55, v, 10));            // InterlockedExchangeAdd -> old
        Assert.Equal(11u, h.U32(v));
    }

    [Fact]
    public void Memory_allocation_exports()
    {
        var h = new KernelHarness();
        var p = h.Call(166, 0x3000, 0, 0x03FF_FFFF, 0x1_0000, 4); // MmAllocateContiguousMemoryEx
        Assert.Equal(OriginalXboxMachine.KernelRamMirror, p & 0xF000_0000);
        Assert.Equal(0u, p & 0xFFFF);
        Assert.Equal(0x3000u, h.Call(180, p));                     // MmQueryAllocationSize
        Assert.Equal(0u, h.M.Bus.Read32(p + 0x2FFC));
        h.Call(171, p);                                            // MmFreeContiguousMemory
        Assert.Equal(0u, h.Call(180, p));

        h.M.Bus.Write32(h.Scratch, 0);
        h.M.Bus.Write32(h.Scratch + 4, 0x2_0001);
        Assert.Equal(NtStatus.Success, h.Call(184, h.Scratch, 0, h.Scratch + 4, 0x3000, 4)); // NtAllocateVirtualMemory
        var va = h.U32(h.Scratch);
        Assert.Equal(0u, va % 0x1_0000);
        Assert.Equal(0x2_1000u, h.U32(h.Scratch + 4));
        Assert.Equal(NtStatus.Success, h.Call(199, h.Scratch, h.Scratch + 4, 0x8000)); // MEM_RELEASE
        Assert.Equal(0xC000_009Fu, h.Call(199, h.Scratch, h.Scratch + 4, 0x8000));    // already freed
        Assert.Equal(0u, h.Call(166, 0x1000, 0x0400_0000, 0x0500_0000, 0, 4));         // range outside RAM -> NULL
    }

    [Fact]
    public void Ansi_strings_and_symbolic_links()
    {
        var h = new KernelHarness();
        var name = h.Scratch + 0x100;
        var target = h.Scratch + 0x140;
        h.M.Bus.WriteBytes(h.Scratch + 0x200, "\\??\\D:\0"u8);
        h.M.Bus.WriteBytes(h.Scratch + 0x220, "\\Device\\CdRom0\\\0"u8);
        h.Call(289, name, h.Scratch + 0x200);   // RtlInitAnsiString
        Assert.Equal(6, h.M.Bus.Read16(name));
        Assert.Equal(7, h.M.Bus.Read16(name + 2));
        h.Call(289, target, h.Scratch + 0x220);
        var oa = h.Scratch + 0x180;
        h.M.Bus.Write32(oa, 0);
        h.M.Bus.Write32(oa + 4, name);
        Assert.Equal(NtStatus.ObjectNameNotFound, h.Call(203, h.Scratch, oa));
        Assert.Equal(NtStatus.Success, h.Call(67, name, target));
        Assert.Equal(NtStatus.ObjectNameCollision, h.Call(67, name, target));
        Assert.Equal(NtStatus.Success, h.Call(203, h.Scratch, oa));
        Assert.Equal(NtStatus.Success, h.Call(69, name));
        Assert.Equal(NtStatus.ObjectNameNotFound, h.Call(69, name));
    }

    [Fact]
    public void File_io_on_invalid_handle_and_eeprom_and_pci()
    {
        var h = new KernelHarness();
        Assert.Equal(NtStatus.InvalidHandle, h.Call(236, 0xFFFF_FFFF, 0, 0, 0, h.Scratch, h.Scratch + 0x10, 4, 0));
        var e = h.Handle(NtCreateEvent, 0, 0, 0);
        Assert.Equal(NtStatus.ObjectTypeMismatch, h.Call(219, e, 0, 0, 0, h.Scratch, h.Scratch + 0x10, 4, 0));

        h.M.Bus.WriteBytes(h.Scratch + 0x100, Enumerable.Repeat((byte)0xAA, 256).ToArray());
        Assert.Equal(NtStatus.Success, h.Call(24, 0xFFFF, h.Scratch, h.Scratch + 0x100, 256, h.Scratch + 4));
        Assert.All(h.M.Bus.ReadBytes(h.Scratch + 0x100, 256), b => Assert.Equal(0, b)); // blank EEPROM: no console data
        Assert.Equal(256u, h.U32(h.Scratch + 4));
        Assert.Equal(MachineStop.KernelFeatureUnsupported, h.CallExpectingStop(24, 0x10, h.Scratch, h.Scratch + 0x100, 256, h.Scratch + 4).Stop);

        var h2 = new KernelHarness();
        h2.M.Bus.Write32(h2.Scratch, 0x1234);
        h2.Call(46, 0, 0x60, 0x6C, h2.Scratch, 4, 0);
        Assert.Equal(0u, h2.U32(h2.Scratch));
        Assert.Contains("register 0x6C", Assert.Single(h2.M.UnmodelledAccesses), StringComparison.Ordinal);
    }

    [Fact]
    public void Unimplemented_export_stops_with_ordinal_and_name()
    {
        var h = new KernelHarness();
        var r = h.CallExpectingStop(91); // IoDismountVolumeByName
        Assert.Equal(MachineStop.KernelExportUnimplemented, r.Stop);
        Assert.Equal(91, r.KernelOrdinal);
        Assert.Contains("IoDismountVolumeByName", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Named_objects_are_reported_not_silently_ignored()
    {
        var h = new KernelHarness();
        var oa = h.Scratch + 0x100;
        h.M.Bus.Write32(oa, 0);
        h.M.Bus.Write32(oa + 4, h.Scratch + 0x200); // non-null ObjectName
        var r = h.CallExpectingStop(NtCreateMutant, h.Scratch, oa, 0);
        Assert.Equal(MachineStop.KernelFeatureUnsupported, r.Stop);
        Assert.Contains("named object", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_export_table_has_known_entries_and_conventions()
    {
        Assert.Equal(371, KernelExports.Count);
        Assert.Equal(("DbgPrint", KernelExportKind.Cdecl), (KernelExports.ByOrdinal[8].Name, KernelExports.ByOrdinal[8].Kind));
        Assert.Equal((KernelExportKind.Fastcall, 12), (KernelExports.ByOrdinal[51].Kind, KernelExports.ByOrdinal[51].ArgumentBytes));
        Assert.Equal((KernelExportKind.Stdcall, 40), (KernelExports.ByOrdinal[255].Kind, KernelExports.ByOrdinal[255].ArgumentBytes));
        Assert.Equal(KernelExportKind.Data, KernelExports.ByOrdinal[156].Kind); // KeTickCount
    }

    [Fact]
    public void Msr_model_reports_only_justified_registers()
    {
        var b = new XbeBuilder();
        b.Emit(0xB9, 0x2A, 0, 0, 0, 0x0F, 0x32, 0xCC); // mov ecx,0x2A; rdmsr; int3
        var file = b.Build();
        var m = new OriginalXboxMachine();
        m.Load(XbeImage.Parse(file), file);
        m.Run(100);
        Assert.Equal(0b000100u, (m.Cpu[X86Cpu.EAX] >> 22) & 0b101111); // 5.5x bus ratio
        var b2 = new XbeBuilder();
        b2.Emit(0xB9, 0x1B, 0, 0, 0, 0x0F, 0x32); // IA32_APIC_BASE: not modelled
        var f2 = b2.Build();
        var m2 = new OriginalXboxMachine();
        m2.Load(XbeImage.Parse(f2), f2);
        Assert.Equal(MachineStop.UnimplementedInstruction, m2.Run(100).Stop);
    }
}
