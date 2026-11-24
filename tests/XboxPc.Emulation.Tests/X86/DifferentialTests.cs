using System.Runtime.InteropServices;
using XboxPc.Emulation.Core;
using XboxPc.Emulation.X86;
using Xunit.Abstractions;

namespace XboxPc.Emulation.Tests.X86;

/// <summary>
/// Oracle: the host CPU. Each instruction under test is assembled into a tiny x64 thunk, executed natively with
/// random inputs, and compared against the interpreter executing the same instruction bytes.
/// 32/16/8-bit ALU encodings used here are identical in 64-bit mode, so the host is a faithful IA-32 reference.
/// Only architecturally *defined* flags are compared (per Intel SDM vol. 2).
/// </summary>
public sealed unsafe class DifferentialTests(ITestOutputHelper output) : IDisposable
{
    private const int Cases = 4000;
    private readonly byte* _code = (byte*)VirtualAlloc(IntPtr.Zero, 4096, 0x3000, 0x40);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr address, nuint size, uint type);

    public void Dispose() => VirtualFree((IntPtr)_code, 0, 0x8000);

    private static bool HostIsX64 => RuntimeInformation.ProcessArchitecture == Architecture.X64 && OperatingSystem.IsWindows();

    private const uint All = X86Cpu.ArithmeticFlags;
    private const uint NoAf = All & ~X86Cpu.AF;

    /// <summary>Runs <paramref name="instruction"/> natively with EAX=a, ECX=EDX=b, flags=inFlags. Returns (eax, edx, flags).</summary>
    private (uint Eax, uint Edx, uint Flags) Native(byte[] instruction, uint a, uint b, uint inFlags)
    {
        byte[] prologue =
        [
            0x89, 0xC8,             // mov eax, ecx
            0x89, 0xD1,             // mov ecx, edx
            0x41, 0x51,             // push r9
            0x9D,                   // popfq
        ];
        byte[] epilogue =
        [
            0x41, 0x89, 0x50, 0x08, // mov [r8+8], edx
            0x9C,                   // pushfq
            0x41, 0x8F, 0x00,       // pop qword [r8]
            0xC3,                   // ret
        ];
        var bytes = prologue.Concat(instruction).Concat(epilogue).ToArray();
        for (var i = 0; i < bytes.Length; i++) _code[i] = bytes[i];
        var fn = (delegate* unmanaged<uint, uint, ulong*, ulong, uint>)_code;
        var outBuf = stackalloc ulong[2];
        var eax = fn(a, b, outBuf, inFlags);
        return (eax, (uint)outBuf[1], (uint)outBuf[0]);
    }

    private static (uint Eax, uint Edx, uint Flags) Emulated(byte[] instruction, uint a, uint b, uint inFlags)
    {
        var bus = new PhysicalBus();
        bus.MapRam("ram", 0, new byte[PhysicalBus.PageSize]);
        bus.WriteBytes(0x100, instruction);
        var cpu = new X86Cpu(bus) { Eip = 0x100, Eflags = inFlags | 0x2 };
        cpu[X86Cpu.EAX] = a;
        cpu[X86Cpu.ECX] = b;
        cpu[X86Cpu.EDX] = b;
        cpu[X86Cpu.ESP] = 0x8000;
        cpu.Step();
        Assert.Equal(0x100u + (uint)instruction.Length, cpu.Eip);
        return (cpu[X86Cpu.EAX], cpu[X86Cpu.EDX], cpu.Eflags);
    }

    private static IEnumerable<(uint A, uint B, uint Flags)> Inputs(int seed)
    {
        uint[] edges = [0, 1, 2, 0x7F, 0x80, 0xFF, 0x100, 0x7FFF, 0x8000, 0xFFFF, 0x7FFF_FFFF, 0x8000_0000, 0x8000_0001, 0xFFFF_FFFE, 0xFFFF_FFFF, 31, 32, 33];
        foreach (var a in edges)
            foreach (var b in edges)
                foreach (var cf in new[] { 0u, 1u })
                    yield return (a, b, cf);
        var rng = new Random(seed);
        for (var i = 0; i < Cases; i++)
        {
            var a = (uint)rng.NextInt64(0, 1L << 32);
            var b = rng.Next(4) == 0 ? (uint)rng.Next(0, 40) : (uint)rng.NextInt64(0, 1L << 32);
            // random arithmetic flags in, so flag-preserving behaviour is checked too
            var flags = (uint)rng.Next(0, 0x1000) & All;
            yield return (a, b, flags);
        }
    }

    private void Check(string name, byte[] instruction, Func<uint, uint, uint> definedFlags, bool compareEdx = false)
    {
        if (!HostIsX64) { output.WriteLine("Host is not x64 Windows; differential oracle unavailable."); return; }
        var count = 0;
        foreach (var (a, b, flags) in Inputs(name.GetHashCode(StringComparison.Ordinal)))
        {
            var native = Native(instruction, a, b, flags);
            var emu = Emulated(instruction, a, b, flags);
            var mask = definedFlags(a, b);
            var context = $"{name} a=0x{a:X8} b=0x{b:X8} flagsIn=0x{flags:X3}";
            Assert.True(native.Eax == emu.Eax, $"{context}: EAX native 0x{native.Eax:X8} vs emu 0x{emu.Eax:X8}");
            if (compareEdx) Assert.True(native.Edx == emu.Edx, $"{context}: EDX native 0x{native.Edx:X8} vs emu 0x{emu.Edx:X8}");
            Assert.True((native.Flags & mask) == (emu.Flags & mask), $"{context}: flags native 0x{native.Flags & mask:X3} vs emu 0x{emu.Flags & mask:X3} (mask 0x{mask:X3})");
            count++;
        }
        output.WriteLine($"{name}: {count} cases match the host CPU.");
    }

    public static TheoryData<string, byte[]> AluOps() => new()
    {
        { "add eax,edx", [0x01, 0xD0] }, { "or eax,edx", [0x09, 0xD0] }, { "adc eax,edx", [0x11, 0xD0] }, { "sbb eax,edx", [0x19, 0xD0] },
        { "and eax,edx", [0x21, 0xD0] }, { "sub eax,edx", [0x29, 0xD0] }, { "xor eax,edx", [0x31, 0xD0] }, { "cmp eax,edx", [0x39, 0xD0] },
        { "test eax,edx", [0x85, 0xD0] }, { "add al,dl", [0x00, 0xD0] }, { "sbb al,dl", [0x18, 0xD0] }, { "adc ax,dx", [0x66, 0x11, 0xD0] },
        { "sub ax,dx", [0x66, 0x29, 0xD0] }, { "cmp dh,ah", [0x38, 0xE6] }, { "add eax,imm32", [0x05, 0x78, 0x56, 0x34, 0x92] },
        { "sub eax,simm8", [0x83, 0xE8, 0xF0] }, { "xor al,imm8", [0x34, 0x5A] },
    };

    [Theory]
    [MemberData(nameof(AluOps))]
    public void Alu_matches_host(string name, byte[] instruction)
    {
        var logic = name.StartsWith("and", StringComparison.Ordinal) || name.StartsWith("or", StringComparison.Ordinal)
            || name.StartsWith("xor", StringComparison.Ordinal) || name.StartsWith("test", StringComparison.Ordinal);
        Check(name, instruction, (_, _) => logic ? NoAf : All);
    }

    [Theory]
    [InlineData("inc eax", new byte[] { 0xFF, 0xC0 })]
    [InlineData("dec eax", new byte[] { 0xFF, 0xC8 })]
    [InlineData("neg eax", new byte[] { 0xF7, 0xD8 })]
    [InlineData("not eax", new byte[] { 0xF7, 0xD0 })]
    [InlineData("inc al", new byte[] { 0xFE, 0xC0 })]
    [InlineData("neg ax", new byte[] { 0x66, 0xF7, 0xD8 })]
    public void Unary_matches_host(string name, byte[] instruction) => Check(name, instruction, (_, _) => All);

    [Theory]
    [InlineData("mul edx", new byte[] { 0xF7, 0xE2 })]
    [InlineData("imul edx", new byte[] { 0xF7, 0xEA })]
    [InlineData("imul eax,edx", new byte[] { 0x0F, 0xAF, 0xC2 })]
    [InlineData("imul eax,edx,imm8", new byte[] { 0x6B, 0xC2, 0x9C })]
    [InlineData("mul dl", new byte[] { 0xF6, 0xE2 })]
    [InlineData("imul dx", new byte[] { 0x66, 0xF7, 0xEA })]
    public void Multiply_matches_host(string name, byte[] instruction) =>
        Check(name, instruction, (_, _) => X86Cpu.CF | X86Cpu.OF, compareEdx: true);

    [Theory]
    [InlineData("shl eax,cl", new byte[] { 0xD3, 0xE0 }, false)]
    [InlineData("shr eax,cl", new byte[] { 0xD3, 0xE8 }, false)]
    [InlineData("sar eax,cl", new byte[] { 0xD3, 0xF8 }, false)]
    [InlineData("rol eax,cl", new byte[] { 0xD3, 0xC0 }, true)]
    [InlineData("ror eax,cl", new byte[] { 0xD3, 0xC8 }, true)]
    [InlineData("rcl eax,cl", new byte[] { 0xD3, 0xD0 }, true)]
    [InlineData("rcr eax,cl", new byte[] { 0xD3, 0xD8 }, true)]
    public void Shift_and_rotate_matches_host(string name, byte[] instruction, bool rotate) =>
        Check(name, instruction, (_, b) =>
        {
            var count = b & 0x1F;
            if (count == 0) return All;                    // nothing changes
            if (rotate) return count == 1 ? All : All & ~X86Cpu.OF; // SZAP untouched; OF only defined for count 1
            return count == 1 ? NoAf : NoAf & ~X86Cpu.OF;  // AF undefined; OF only defined for count 1
        });

    [Theory]
    [InlineData("shld eax,edx,cl", new byte[] { 0x0F, 0xA5, 0xD0 })]
    [InlineData("shrd eax,edx,cl", new byte[] { 0x0F, 0xAD, 0xD0 })]
    public void Double_shift_matches_host(string name, byte[] instruction) =>
        Check(name, instruction, (_, b) => (b & 0x1F) switch { 0 => All, 1 => NoAf, _ => NoAf & ~X86Cpu.OF });

    [Theory]
    [InlineData("bswap eax", new byte[] { 0x0F, 0xC8 })]
    [InlineData("movzx eax,dl", new byte[] { 0x0F, 0xB6, 0xC2 })]
    [InlineData("movsx eax,dx", new byte[] { 0x0F, 0xBF, 0xC2 })]
    [InlineData("sete al", new byte[] { 0x0F, 0x94, 0xC0 })]
    [InlineData("setl al", new byte[] { 0x0F, 0x9C, 0xC0 })]
    [InlineData("setbe al", new byte[] { 0x0F, 0x96, 0xC0 })]
    [InlineData("cmovg eax,edx", new byte[] { 0x0F, 0x4F, 0xC2 })]
    [InlineData("cmovp eax,edx", new byte[] { 0x0F, 0x4A, 0xC2 })]
    [InlineData("cdq", new byte[] { 0x99 })]
    [InlineData("cwde", new byte[] { 0x98 })]
    [InlineData("xchg eax,edx", new byte[] { 0x92 })]
    [InlineData("lea eax,[eax+edx*4+0x10]", new byte[] { 0x8D, 0x44, 0x90, 0x10 })]
    public void Data_movement_matches_host(string name, byte[] instruction) => Check(name, instruction, (_, _) => All, compareEdx: true);

    [Theory]
    [InlineData("bsf eax,edx", new byte[] { 0x0F, 0xBC, 0xC2 })]
    [InlineData("bsr eax,edx", new byte[] { 0x0F, 0xBD, 0xC2 })]
    public void Bit_scan_matches_host(string name, byte[] instruction) => Check(name, instruction, (_, _) => X86Cpu.ZF);

    [Theory]
    [InlineData("bt eax,edx", new byte[] { 0x0F, 0xA3, 0xD0 })]
    [InlineData("bts eax,edx", new byte[] { 0x0F, 0xAB, 0xD0 })]
    [InlineData("btr eax,edx", new byte[] { 0x0F, 0xB3, 0xD0 })]
    [InlineData("btc eax,edx", new byte[] { 0x0F, 0xBB, 0xD0 })]
    [InlineData("bt eax,imm8", new byte[] { 0x0F, 0xBA, 0xE0, 0x1F })]
    public void Bit_test_matches_host(string name, byte[] instruction) => Check(name, instruction, (_, _) => X86Cpu.CF);

    [Theory]
    [InlineData("xadd eax,edx", new byte[] { 0x0F, 0xC1, 0xD0 })]
    [InlineData("cmpxchg ecx,edx (eax compare)", new byte[] { 0x0F, 0xB1, 0xD1 })]
    public void Atomic_ops_match_host(string name, byte[] instruction) => Check(name, instruction, (_, _) => All, compareEdx: true);
}
