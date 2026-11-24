using System.Buffers.Binary;
using XboxPc.Emulation.X86;
using Xunit.Abstractions;

namespace XboxPc.Emulation.Tests.X86;

/// <summary>
/// SSE1 and MMX differential tests against the host CPU.
/// SSE buffer: [0] xmm0 in (16) [0x10] xmm1 in (16) [0x20] result (16) [0x30] saved MXCSR [0x34] test MXCSR
/// [0x38] result MXCSR [0x3C] eflags / gpr result. MXCSR flags compared: IE and ZE.
/// MMX buffer: [0] mm0 in [8] mm1 in [0x10] result [0x18] gpr input [0x20] gpr result.
/// </summary>
public sealed class SimdDifferentialTests(ITestOutputHelper output) : IDisposable
{
    private readonly HostOracle _oracle = new();

    public void Dispose() => _oracle.Dispose();

    private static readonly byte[] SsePrologue =
    [
        0x0F, 0xAE, 0x59, 0x30, // stmxcsr [rcx+30]
        0x0F, 0xAE, 0x51, 0x34, // ldmxcsr [rcx+34]
        0x0F, 0x10, 0x01,       // movups xmm0,[rcx]
        0x0F, 0x10, 0x49, 0x10, // movups xmm1,[rcx+10]
    ];

    private static readonly byte[] SseEpilogue =
    [
        0x0F, 0x11, 0x41, 0x20, // movups [rcx+20],xmm0
        0x0F, 0xAE, 0x59, 0x38, // stmxcsr [rcx+38]
        0x0F, 0xAE, 0x51, 0x30, // ldmxcsr [rcx+30]
        0xC3,
    ];

    private static uint[] FloatEdges() =>
    [
        0x0000_0000, 0x8000_0000, 0x3F80_0000, 0xBF80_0000, 0x7F80_0000, 0xFF80_0000, 0x7FC0_0000, 0xFFC0_0000,
        0x7F80_0001, 0xFF80_0001, 0x0000_0001, 0x807F_FFFF, 0x0080_0000, 0x7F7F_FFFF, 0xFF7F_FFFF, 0x4F00_0000,
        0xCF00_0000, 0x4EFF_FFFF, 0x3F00_0000, 0x3FC0_0000, 0x4020_0000, 0xC020_0000, 0x4B80_0001,
    ];

    private static IEnumerable<uint[]> Vectors(int seed, int count = 3000)
    {
        var edges = FloatEdges();
        var rng = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var v = new uint[8];
            for (var j = 0; j < 8; j++)
            {
                v[j] = rng.Next(4) switch
                {
                    0 => edges[rng.Next(edges.Length)],
                    1 => (uint)rng.NextInt64(0, 1L << 32),
                    2 => BitConverter.SingleToUInt32Bits((float)((rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-8, 9)))),
                    _ => BitConverter.SingleToUInt32Bits(rng.Next(-1000, 1000) + (rng.Next(4) * 0.25f)),
                };
            }
            yield return v;
        }
    }

    private int RunSse(string name, byte[] op, uint mxcsr, int resultBytes = 16, bool compareGpr = false, uint eflagsMask = 0, int seed = 1)
    {
        var code = HostOracle.Concat(SsePrologue, op, SseEpilogue);
        var mismatches = 0;
        var cases = 0;
        foreach (var v in Vectors(seed + name.Length))
        {
            cases++;
            var buf = new byte[0x40];
            for (var j = 0; j < 8; j++) BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(j * 4), v[j]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x34), mxcsr);
            var n = _oracle.Native(code, buf);
            var e = HostOracle.Emulated(code, buf, out _);
            var ok = n.AsSpan(0x20, resultBytes).SequenceEqual(e.AsSpan(0x20, resultBytes))
                && (BinaryPrimitives.ReadUInt32LittleEndian(n.AsSpan(0x38)) & 0x5) == (BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(0x38)) & 0x5)
                && (!compareGpr || n.AsSpan(0x3C, 4).SequenceEqual(e.AsSpan(0x3C, 4)))
                && (BinaryPrimitives.ReadUInt32LittleEndian(n.AsSpan(0x3C)) & eflagsMask) == (BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(0x3C)) & eflagsMask);
            if (ok) continue;
            mismatches++;
            if (mismatches <= 4)
                output.WriteLine($"MISMATCH {name} mxcsr={mxcsr:X4} in {Convert.ToHexString(buf, 0, 32)}: native {Convert.ToHexString(n, 0x20, resultBytes)} mx {BinaryPrimitives.ReadUInt32LittleEndian(n.AsSpan(0x38)):X} gpr {Convert.ToHexString(n, 0x3C, 4)} / emu {Convert.ToHexString(e, 0x20, resultBytes)} mx {BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(0x38)):X} gpr {Convert.ToHexString(e, 0x3C, 4)}");
        }
        output.WriteLine($"{name} mxcsr={mxcsr:X4}: {cases} cases, {mismatches} mismatches");
        return mismatches;
    }

    public static TheoryData<string, byte[]> PackedAndScalar()
    {
        var data = new TheoryData<string, byte[]>();
        foreach (var (name, op) in new[] { ("add", 0x58), ("mul", 0x59), ("sub", 0x5C), ("min", 0x5D), ("div", 0x5E), ("max", 0x5F), ("sqrt", 0x51) })
        {
            data.Add(name + "ps", [0x0F, (byte)op, 0xC1]);
            data.Add(name + "ss", [0xF3, 0x0F, (byte)op, 0xC1]);
        }
        foreach (var (name, op) in new[] { ("andps", 0x54), ("andnps", 0x55), ("orps", 0x56), ("xorps", 0x57), ("unpcklps", 0x14), ("unpckhps", 0x15), ("movhlps", 0x12), ("movlhps", 0x16) })
            data.Add(name, [0x0F, (byte)op, 0xC1]);
        data.Add("movss reg", [0xF3, 0x0F, 0x10, 0xC1]);
        data.Add("shufps 0x1B", [0x0F, 0xC6, 0xC1, 0x1B]);
        data.Add("shufps 0xE4", [0x0F, 0xC6, 0xC1, 0xE4]);
        for (var p = 0; p < 8; p++)
        {
            data.Add($"cmpps {p}", [0x0F, 0xC2, 0xC1, (byte)p]);
            data.Add($"cmpss {p}", [0xF3, 0x0F, 0xC2, 0xC1, (byte)p]);
        }
        data.Add("addps m128", [0x0F, 0x58, 0x41, 0x10]);   // addps xmm0,[rcx+10] (aligned buffer)
        data.Add("mulss m32", [0xF3, 0x0F, 0x59, 0x41, 0x14]);
        data.Add("movss load m32", [0xF3, 0x0F, 0x10, 0x41, 0x18]);
        data.Add("movlps load", [0x0F, 0x12, 0x41, 0x18]);
        data.Add("movhps load", [0x0F, 0x16, 0x41, 0x10]);
        data.Add("cvtpi2ps from xmm-less mm? (cvtsi2ss r/m32)", [0xF3, 0x0F, 0x2A, 0x41, 0x1C]);
        return data;
    }

    [Theory]
    [MemberData(nameof(PackedAndScalar))]
    public void Sse_matches_host(string name, byte[] op)
    {
        if (!HostOracle.Available) return;
        Assert.Equal(0, RunSse(name, op, 0x1F80));
        if (name.Contains("ps", StringComparison.Ordinal) || name.Contains("ss", StringComparison.Ordinal))
            Assert.Equal(0, RunSse(name + " FTZ", op, 0x9F80)); // flush-to-zero
    }

    [Fact]
    public void Conversions_match_host_in_all_rounding_modes()
    {
        if (!HostOracle.Available) return;
        byte[] storeEax = [0x89, 0x41, 0x3C]; // mov [rcx+3c], eax
        for (var rc = 0u; rc < 4; rc++)
        {
            var mx = 0x1F80 | (rc << 13);
            Assert.Equal(0, RunSse($"cvtss2si rc{rc}", HostOracle.Concat([0xF3, 0x0F, 0x2D, 0xC0], storeEax), mx, resultBytes: 0, compareGpr: true));
            Assert.Equal(0, RunSse($"cvttss2si rc{rc}", HostOracle.Concat([0xF3, 0x0F, 0x2C, 0xC0], storeEax), mx, resultBytes: 0, compareGpr: true));
        }
        Assert.Equal(0, RunSse("cvtsi2ss", [0xF3, 0x0F, 0x2A, 0x01], 0x1F80));   // cvtsi2ss xmm0, dword [rcx]
        Assert.Equal(0, RunSse("movmskps", HostOracle.Concat([0x0F, 0x50, 0xC1], storeEax), 0x1F80, resultBytes: 0, compareGpr: true));
    }

    [Fact]
    public void Comiss_ucomiss_match_host()
    {
        if (!HostOracle.Available) return;
        byte[] flags = [0x9C, 0x8F, 0x41, 0x3C]; // pushfq ; pop [rcx+3c]
        const uint mask = X86Cpu.ZF | X86Cpu.PF | X86Cpu.CF | X86Cpu.OF | X86Cpu.SF | X86Cpu.AF;
        Assert.Equal(0, RunSse("comiss", HostOracle.Concat([0x0F, 0x2F, 0xC1], flags), 0x1F80, resultBytes: 0, eflagsMask: mask));
        Assert.Equal(0, RunSse("ucomiss", HostOracle.Concat([0x0F, 0x2E, 0xC1], flags), 0x1F80, resultBytes: 0, eflagsMask: mask));
    }

    [Fact]
    public void Rcp_and_rsqrt_are_within_intel_error_bound()
    {
        // Pentium III tables are not public; the architectural bound is |relative error| ≤ 1.5 x 2^-12.
        var oracle = HostOracle.Available;
        var bound = 1.5 * Math.Pow(2, -12);
        var rng = new Random(9);
        for (var i = 0; i < 5000; i++)
        {
            var x = (float)(rng.NextDouble() * Math.Pow(10, rng.Next(-20, 20)));
            if (x == 0) continue;
            var buf = new byte[0x40];
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(0x10), x);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x34), 0x1F80);
            foreach (var (op, exact) in new[] { ((byte)0x53, 1.0 / x), ((byte)0x52, 1.0 / Math.Sqrt(x)) })
            {
                var code = HostOracle.Concat(SsePrologue, [0x0F, op, 0xC1], SseEpilogue);
                var e = BinaryPrimitives.ReadSingleLittleEndian(HostOracle.Emulated(code, buf, out _).AsSpan(0x20));
                Assert.True(Math.Abs((e - exact) / exact) <= bound, $"op {op:X2} x={x}: {e} vs {exact}");
            }
        }
        output.WriteLine(oracle ? "rcpps/rsqrtps within bound (bit-exact match with Intel tables is not claimed)" : "bound check only");
    }

    [Fact]
    public void Movaps_faults_on_misaligned_address()
    {
        var bus = new XboxPc.Emulation.Core.PhysicalBus();
        bus.MapRam("ram", 0, new byte[0x10000]);
        bus.WriteBytes(0x100, [0x0F, 0x28, 0x01]); // movaps xmm0,[ecx]
        var cpu = new X86Cpu(bus) { Eip = 0x100 };
        cpu[X86Cpu.ECX] = 0x2008;
        var ex = Assert.IsType<X86Exception>(cpu.Run(1).Error);
        Assert.Equal(X86Exception.GeneralProtection, ex.Vector);
        Assert.Equal(0x100u, cpu.Eip);
    }

    // ------------------------------------------------------------------ MMX

    private int RunMmx(string name, byte[] op, int resultBytes = 8, bool gpr = false)
    {
        byte[] prologue = [0x0F, 0x6F, 0x01, 0x0F, 0x6F, 0x49, 0x08, 0x8B, 0x51, 0x18]; // movq mm0,[rcx]; movq mm1,[rcx+8]; mov edx,[rcx+18]
        byte[] epilogue = [0x0F, 0x7F, 0x41, 0x10, 0x0F, 0x77, 0xC3];                 // movq [rcx+10],mm0; emms; ret
        var code = HostOracle.Concat(prologue, op, epilogue);
        var rng = new Random(name.Length * 7919);
        var mismatches = 0;
        for (var i = 0; i < 3000; i++)
        {
            var buf = new byte[0x40];
            rng.NextBytes(buf.AsSpan(0, 0x1C));
            if (i % 4 == 0) BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(8), (ulong)rng.Next(0, 70)); // small shift counts
            if (i % 7 == 0) BinaryPrimitives.WriteUInt64LittleEndian(buf, 0x7FFF_8000_00FF_FF80UL);
            var n = _oracle.Native(code, buf);
            var e = HostOracle.Emulated(code, buf, out _);
            if (n.AsSpan(0x10, resultBytes).SequenceEqual(e.AsSpan(0x10, resultBytes)) && (!gpr || n.AsSpan(0x20, 4).SequenceEqual(e.AsSpan(0x20, 4)))) continue;
            if (++mismatches <= 3) output.WriteLine($"MISMATCH {name}: in {Convert.ToHexString(buf, 0, 0x1C)} native {Convert.ToHexString(n, 0x10, 8)}/{Convert.ToHexString(n, 0x20, 4)} emu {Convert.ToHexString(e, 0x10, 8)}/{Convert.ToHexString(e, 0x20, 4)}");
        }
        output.WriteLine($"{name}: 3000 cases, {mismatches} mismatches");
        return mismatches;
    }

    public static TheoryData<string, byte> MmxBinary()
    {
        var d = new TheoryData<string, byte>();
        foreach (var (n, o) in new (string, byte)[]
        {
            ("punpcklbw", 0x60), ("punpcklwd", 0x61), ("punpckldq", 0x62), ("packsswb", 0x63), ("pcmpgtb", 0x64), ("pcmpgtw", 0x65), ("pcmpgtd", 0x66),
            ("packuswb", 0x67), ("punpckhbw", 0x68), ("punpckhwd", 0x69), ("punpckhdq", 0x6A), ("packssdw", 0x6B), ("pcmpeqb", 0x74), ("pcmpeqw", 0x75),
            ("pcmpeqd", 0x76), ("psrlw", 0xD1), ("psrld", 0xD2), ("psrlq", 0xD3), ("pmullw", 0xD5), ("psubusb", 0xD8), ("psubusw", 0xD9), ("pminub", 0xDA),
            ("pand", 0xDB), ("paddusb", 0xDC), ("paddusw", 0xDD), ("pmaxub", 0xDE), ("pandn", 0xDF), ("pavgb", 0xE0), ("psraw", 0xE1), ("psrad", 0xE2),
            ("pavgw", 0xE3), ("pmulhuw", 0xE4), ("pmulhw", 0xE5), ("psubsb", 0xE8), ("psubsw", 0xE9), ("pminsw", 0xEA), ("por", 0xEB), ("paddsb", 0xEC),
            ("paddsw", 0xED), ("pmaxsw", 0xEE), ("pxor", 0xEF), ("psllw", 0xF1), ("pslld", 0xF2), ("psllq", 0xF3), ("pmaddwd", 0xF5), ("psadbw", 0xF6),
            ("psubb", 0xF8), ("psubw", 0xF9), ("psubd", 0xFA), ("paddb", 0xFC), ("paddw", 0xFD), ("paddd", 0xFE),
        })
            d.Add(n, o);
        return d;
    }

    [Theory]
    [MemberData(nameof(MmxBinary))]
    public void Mmx_matches_host(string name, byte op)
    {
        if (!HostOracle.Available) return;
        Assert.Equal(0, RunMmx(name, [0x0F, op, 0xC1]));
        Assert.Equal(0, RunMmx(name + " m64", [0x0F, op, 0x41, 0x08]));
    }

    [Fact]
    public void Mmx_immediate_shifts_shuffles_and_moves_match_host()
    {
        if (!HostOracle.Available) return;
        foreach (var count in new byte[] { 0, 1, 7, 15, 16, 31, 32, 63, 64, 200 })
        {
            Assert.Equal(0, RunMmx($"psrlw {count}", [0x0F, 0x71, 0xD0, count]));
            Assert.Equal(0, RunMmx($"psraw {count}", [0x0F, 0x71, 0xE0, count]));
            Assert.Equal(0, RunMmx($"psllw {count}", [0x0F, 0x71, 0xF0, count]));
            Assert.Equal(0, RunMmx($"psrad {count}", [0x0F, 0x72, 0xE0, count]));
            Assert.Equal(0, RunMmx($"psllq {count}", [0x0F, 0x73, 0xF0, count]));
            Assert.Equal(0, RunMmx($"psrlq {count}", [0x0F, 0x73, 0xD0, count]));
        }
        foreach (var imm in new byte[] { 0x00, 0x1B, 0xE4, 0x39, 0xFF })
            Assert.Equal(0, RunMmx($"pshufw {imm:X2}", [0x0F, 0x70, 0xC1, imm]));
        for (byte sel = 0; sel < 4; sel++)
        {
            Assert.Equal(0, RunMmx($"pinsrw {sel}", [0x0F, 0xC4, 0xC2, sel]));                            // pinsrw mm0, edx, sel
            Assert.Equal(0, RunMmx($"pextrw {sel}", [0x0F, 0xC5, 0xC1, sel, 0x89, 0x41, 0x20], 0, gpr: true)); // pextrw eax, mm1 ; mov [rcx+20],eax
        }
        Assert.Equal(0, RunMmx("pmovmskb", [0x0F, 0xD7, 0xC1, 0x89, 0x41, 0x20], 0, gpr: true));
        Assert.Equal(0, RunMmx("movd mm0,edx", [0x0F, 0x6E, 0xC2]));
        Assert.Equal(0, RunMmx("movd eax,mm1", [0x0F, 0x7E, 0xC8, 0x89, 0x41, 0x20], 0, gpr: true));
        Assert.Equal(0, RunMmx("movq mm0,mm1", [0x0F, 0x6F, 0xC1]));
    }

    [Fact]
    public void Mmx_write_aliases_x87_register_and_sets_tags()
    {
        var bus = new XboxPc.Emulation.Core.PhysicalBus();
        bus.MapRam("ram", 0, new byte[0x10000]);
        // movd mm3, eax ; emms
        bus.WriteBytes(0x100, [0x0F, 0x6E, 0xD8, 0x0F, 0x77]);
        var cpu = new X86Cpu(bus) { Eip = 0x100 };
        cpu[X86Cpu.EAX] = 0x1234_5678;
        cpu.Step();
        Assert.Equal(0x1234_5678UL, cpu.Mmx(3));
        Assert.Equal(0, (cpu.FpuStatus >> 11) & 7);  // TOP = 0
        Assert.Equal(0, cpu.FpuTagWord);             // all valid
        cpu.Step();
        Assert.Equal(0xFFFF, cpu.FpuTagWord);        // EMMS: all empty
    }
}
