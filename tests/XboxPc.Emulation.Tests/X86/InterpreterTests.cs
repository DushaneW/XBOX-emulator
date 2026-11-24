using XboxPc.Emulation.Core;
using XboxPc.Emulation.X86;

namespace XboxPc.Emulation.Tests.X86;

public class InterpreterTests
{
    private const uint CodeBase = 0x1000;

    private static (X86Cpu Cpu, PhysicalBus Bus, IoPortBus Io) Machine(params byte[] code)
    {
        var bus = new PhysicalBus();
        bus.MapRam("ram", 0, new byte[0x10_0000]);
        bus.WriteBytes(CodeBase, code);
        var io = new IoPortBus();
        var cpu = new X86Cpu(bus, io) { Eip = CodeBase };
        cpu[X86Cpu.ESP] = 0x8_0000;
        return (cpu, bus, io);
    }

    [Fact]
    public void Loop_sums_1_to_100()
    {
        // xor eax,eax; mov ecx,100; L: add eax,ecx; dec ecx; jnz L; hlt
        var (cpu, _, _) = Machine(0x31, 0xC0, 0xB9, 100, 0, 0, 0, 0x01, 0xC8, 0x49, 0x75, 0xFB, 0xF4);
        var result = cpu.Run(10_000);
        Assert.Equal(StopReason.Halted, result.Reason);
        Assert.Equal(5050u, cpu[X86Cpu.EAX]);
        Assert.Equal(2 + 3 * 100 + 1, result.Executed); // xor, mov, 100x(add,dec,jnz), hlt
    }

    [Fact]
    public void Call_ret_and_stack_frame()
    {
        // call +5 ; hlt ; [func:] push ebp; mov ebp,esp; mov eax,[ebp+4] (return address); pop ebp; ret
        var (cpu, _, _) = Machine(0xE8, 0x01, 0, 0, 0, 0xF4, 0x55, 0x89, 0xE5, 0x8B, 0x45, 0x04, 0x5D, 0xC3);
        Assert.Equal(StopReason.Halted, cpu.Run(100).Reason);
        Assert.Equal(CodeBase + 5, cpu[X86Cpu.EAX]);
        Assert.Equal(0x8_0000u, cpu[X86Cpu.ESP]);
    }

    [Fact]
    public void Stdcall_ret_imm_pops_arguments()
    {
        // push 7; push 5; call f; hlt; f: mov eax,[esp+4]; add eax,[esp+8]; ret 8
        var (cpu, _, _) = Machine(0x6A, 7, 0x6A, 5, 0xE8, 0x01, 0, 0, 0, 0xF4, 0x8B, 0x44, 0x24, 0x04, 0x03, 0x44, 0x24, 0x08, 0xC2, 0x08, 0x00);
        Assert.Equal(StopReason.Halted, cpu.Run(100).Reason);
        Assert.Equal(12u, cpu[X86Cpu.EAX]);
        Assert.Equal(0x8_0000u, cpu[X86Cpu.ESP]);
    }

    [Fact]
    public void Div_and_idiv()
    {
        // mov edx,0; mov eax,100; mov ecx,7; div ecx; hlt
        var (cpu, _, _) = Machine(0xBA, 0, 0, 0, 0, 0xB8, 100, 0, 0, 0, 0xB9, 7, 0, 0, 0, 0xF7, 0xF1, 0xF4);
        cpu.Run(10);
        Assert.Equal(14u, cpu[X86Cpu.EAX]);
        Assert.Equal(2u, cpu[X86Cpu.EDX]);

        // mov eax,-100; cdq; mov ecx,7; idiv ecx
        (cpu, _, _) = Machine(0xB8, 0x9C, 0xFF, 0xFF, 0xFF, 0x99, 0xB9, 7, 0, 0, 0, 0xF7, 0xF9, 0xF4);
        cpu.Run(10);
        Assert.Equal(unchecked((uint)-14), cpu[X86Cpu.EAX]);
        Assert.Equal(unchecked((uint)-2), cpu[X86Cpu.EDX]);
    }

    [Theory]
    [InlineData(new byte[] { 0x31, 0xC9, 0xF7, 0xF1 })]                         // xor ecx,ecx; div ecx  -> divide by zero
    [InlineData(new byte[] { 0xBA, 1, 0, 0, 0, 0xB9, 1, 0, 0, 0, 0xF7, 0xF1 })]  // edx=1, div 1 -> quotient overflow
    public void Divide_error_is_a_fault_at_the_instruction(byte[] code)
    {
        var (cpu, _, _) = Machine(code);
        var result = cpu.Run(10);
        var ex = Assert.IsType<X86Exception>(result.Error);
        Assert.Equal(X86Exception.DivideError, ex.Vector);
        Assert.Equal(CodeBase + (uint)code.Length - 2, cpu.Eip);
    }

    [Fact]
    public void Rep_movsd_and_rep_stosb()
    {
        var (cpu, bus, _) = Machine(0xFC, 0xF3, 0xA5, 0xB0, 0xAB, 0xB9, 4, 0, 0, 0, 0xF3, 0xAA, 0xF4); // cld; rep movsd; mov al,0xAB; mov ecx,4; rep stosb
        bus.WriteBytes(0x2000, [1, 2, 3, 4, 5, 6, 7, 8]);
        cpu[X86Cpu.ESI] = 0x2000;
        cpu[X86Cpu.EDI] = 0x3000;
        cpu[X86Cpu.ECX] = 2;
        cpu.Run(10);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 0xAB, 0xAB, 0xAB, 0xAB], bus.ReadBytes(0x3000, 12));
        Assert.Equal(0u, cpu[X86Cpu.ECX]);
        Assert.Equal(0x300Cu, cpu[X86Cpu.EDI]);
    }

    [Fact]
    public void Repne_scasb_finds_string_terminator()
    {
        // strlen: xor eax,eax; mov ecx,-1; repne scasb; not ecx; dec ecx; hlt
        var (cpu, bus, _) = Machine(0x31, 0xC0, 0xB9, 0xFF, 0xFF, 0xFF, 0xFF, 0xF2, 0xAE, 0xF7, 0xD1, 0x49, 0xF4);
        bus.WriteBytes(0x4000, "Xbox\0"u8);
        cpu[X86Cpu.EDI] = 0x4000;
        cpu.Run(20);
        Assert.Equal(4u, cpu[X86Cpu.ECX]);
    }

    [Fact]
    public void Fs_segment_override_uses_fs_base()
    {
        // mov eax, fs:[0x28]
        var (cpu, bus, _) = Machine(0x64, 0xA1, 0x28, 0, 0, 0, 0xF4);
        bus.Write32(0x5028, 0xDEADBEEF);
        cpu.FsBase = 0x5000;
        cpu.Run(5);
        Assert.Equal(0xDEADBEEFu, cpu[X86Cpu.EAX]);
    }

    [Fact]
    public void Unmapped_access_is_a_page_fault_with_restored_eip_and_unchanged_esp()
    {
        // push dword [0x7FFF0000]  (unmapped)
        var (cpu, _, _) = Machine(0xFF, 0x35, 0x00, 0x00, 0xFF, 0x7F);
        var result = cpu.Run(5);
        var ex = Assert.IsType<X86Exception>(result.Error);
        Assert.Equal(X86Exception.PageFault, ex.Vector);
        Assert.Equal(0x7FFF_0000u, ex.BusFault!.Address);
        Assert.Equal(CodeBase, cpu.Eip);
        Assert.Equal(0x8_0000u, cpu[X86Cpu.ESP]);
    }

    [Fact]
    public void Ud2_is_invalid_opcode_and_x87_is_reported_as_unimplemented()
    {
        var (cpu, _, _) = Machine(0x0F, 0x0B);
        Assert.Equal(X86Exception.InvalidOpcode, Assert.IsType<X86Exception>(cpu.Run(1).Error).Vector);

        (cpu, _, _) = Machine(0xDF, 0x20); // fbld [eax], packed BCD is deliberately not implemented
        var r = cpu.Run(1);
        Assert.Equal(StopReason.Unimplemented, r.Reason);
        Assert.Contains("BCD", r.Error!.Message, StringComparison.Ordinal);

        (cpu, _, _) = Machine(0x66, 0x0F, 0x58, 0xC1); // addpd: SSE2, absent on Pentium III
        r = cpu.Run(1);
        Assert.Equal(StopReason.Unimplemented, r.Reason);
        Assert.Contains("SSE2", r.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Int3_is_a_trap_after_the_instruction()
    {
        var (cpu, _, _) = Machine(0x90, 0xCC, 0x90);
        var r = cpu.Run(10);
        var ex = Assert.IsType<X86Exception>(r.Error);
        Assert.Equal(X86Exception.Breakpoint, ex.Vector);
        Assert.True(ex.IsTrap);
        Assert.Equal(CodeBase + 2, ex.Eip);
    }

    [Fact]
    public void Port_io_reaches_the_io_bus()
    {
        uint written = 0;
        var (cpu, _, io) = Machine(0xBA, 0xF8, 0x03, 0, 0, 0xB0, 0x41, 0xEE, 0xEC, 0xF4); // mov edx,0x3F8; mov al,'A'; out dx,al; in al,dx
        io.Register(0x3F8, "test-uart", _ => 0x55, (_, v) => written = v);
        cpu.Run(10);
        Assert.Equal((uint)'A', written);
        Assert.Equal(0x55u, cpu[X86Cpu.EAX] & 0xFF);
    }

    [Fact]
    public void Cmpxchg8b_compares_and_swaps()
    {
        var (cpu, bus, _) = Machine(0x0F, 0xC7, 0x0D, 0x00, 0x60, 0, 0, 0xF4); // cmpxchg8b [0x6000]
        bus.Write32(0x6000, 1);
        bus.Write32(0x6004, 2);
        cpu[X86Cpu.EAX] = 1; cpu[X86Cpu.EDX] = 2; cpu[X86Cpu.EBX] = 3; cpu[X86Cpu.ECX] = 4;
        cpu.Run(5);
        Assert.True(cpu.GetFlag(X86Cpu.ZF));
        Assert.Equal(3u, bus.Read32(0x6000));
        Assert.Equal(4u, bus.Read32(0x6004));
    }

    [Fact]
    public void Cpuid_reports_pentium_iii_signature_and_only_implemented_features()
    {
        var (cpu, _, _) = Machine(0x31, 0xC0, 0x0F, 0xA2, 0xB8, 1, 0, 0, 0, 0x0F, 0xA2, 0xF4);
        cpu.Run(10);
        Assert.Equal(X86Cpu.CpuidSignature, cpu[X86Cpu.EAX]);
        Assert.Equal(0u, cpu[X86Cpu.EDX] & (1u << 25)); // SSE not advertised: not implemented yet
        Assert.Equal(0u, cpu[X86Cpu.EDX] & 1u);         // x87 not advertised: not implemented yet
    }

    [Fact]
    public void Bus_rejects_overlapping_and_misaligned_regions()
    {
        var bus = new PhysicalBus();
        bus.MapRam("a", 0, new byte[0x20000]);
        Assert.Throws<ArgumentException>(() => bus.MapRam("b", 0x10000, new byte[0x10000]));
        Assert.Throws<ArgumentException>(() => bus.MapRam("c", 0x30001, new byte[0x10000]));
    }

    [Fact]
    public void Mirrored_ram_shares_storage_and_cross_region_access_works()
    {
        var ram = new byte[0x20000];
        var bus = new PhysicalBus();
        bus.MapRam("ram", 0, ram);
        bus.MapRam("mirror", 0x8000_0000, ram);
        Assert.Throws<BusFaultException>(() => bus.Write32(0x1FFFE, 0xA1B2C3D4)); // straddles into unmapped space
        Assert.Equal(0, ram[0x1FFFE]); // nothing was written before the fault
        Assert.Throws<BusFaultException>(() => bus.Read32(0x20000));
        bus.Write32(0x100, 0x11223344);
        Assert.Equal(0x11223344u, bus.Read32(0x8000_0100));
    }
}
