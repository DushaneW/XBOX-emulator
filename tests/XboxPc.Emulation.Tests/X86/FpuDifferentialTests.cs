using System.Buffers.Binary;
using XboxPc.Emulation.X86;
using Xunit.Abstractions;

namespace XboxPc.Emulation.Tests.X86;

/// <summary>
/// x87 differential tests against the host FPU. Buffer: [0] a(f64) [8] b(f64) [0x10] result(≤10 bytes)
/// [0x20] FSW [0x22] control word under test [0x24] saved CW [0x28] EFLAGS [0x30] integer result.
/// FSW bits compared: C0 C2 C3 TOP SF ZE IE (+C1 where the instruction defines it). DE/UE/PE/OE are not tracked.
/// </summary>
public sealed class FpuDifferentialTests(ITestOutputHelper output) : IDisposable
{
    private readonly HostOracle _oracle = new();

    public void Dispose() => _oracle.Dispose();

    private static readonly byte[] Prologue = [0xD9, 0x79, 0x24, 0xDB, 0xE3, 0xD9, 0x69, 0x22];  // fnstcw [rcx+24]; fninit; fldcw [rcx+22]
    private static readonly byte[] Epilogue = [0xDD, 0x79, 0x20, 0xDB, 0xE3, 0xD9, 0x69, 0x24, 0xC3]; // fnstsw [rcx+20]; fninit; fldcw [rcx+24]; ret
    private static readonly byte[] LoadA = [0xDD, 0x01], LoadB = [0xDD, 0x41, 0x08], StoreResult = [0xDD, 0x59, 0x10], PopSt0 = [0xDD, 0xD8];

    private const ushort FswMask = 0x7D45;   // C3 TOP C2 C0 SF ZE IE
    private const ushort FswMaskC1 = 0x7F45; // + C1
    private const ushort Pc53 = 0x027F, Pc24 = 0x007F;

    public static IEnumerable<double> Values(int seed, int randomCount = 600)
    {
        double[] edges =
        [
            0.0, -0.0, 1.0, -1.0, 0.5, 1.5, 2.5, -2.5, 3.0, 1e-310, -4.9e-324, double.Epsilon, 2.2250738585072014e-308,
            double.MaxValue, -double.MaxValue, 1e308, 1e-300, double.PositiveInfinity, double.NegativeInfinity, double.NaN,
            BitConverter.UInt64BitsToDouble(0x7FF0_0000_0000_0001UL), // SNaN
            BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_1234UL), // QNaN with payload
            Math.PI, Math.E, 1.0 / 3.0, 4294967296.5, 9.2233720368547758e18, -9.2233720368547758e18, 2147483647.5, -2147483648.5,
            16777217.0, 0.1, 1e15 + 0.5,
        ];
        foreach (var e in edges) yield return e;
        var rng = new Random(seed);
        for (var i = 0; i < randomCount; i++)
        {
            yield return (i % 3) switch
            {
                0 => BitConverter.UInt64BitsToDouble((ulong)rng.NextInt64()),                   // any bit pattern
                1 => (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-30, 30)),               // moderate magnitude
                _ => Math.Round((rng.NextDouble() - 0.5) * 1e6, rng.Next(0, 3)),               // near integers
            };
        }
    }

    private static byte[] Buffer(double a, double b, ushort cw)
    {
        var buf = new byte[0x40];
        BinaryPrimitives.WriteDoubleLittleEndian(buf, a);
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(8), b);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x22), cw);
        return buf;
    }

    private sealed record Outcome(int Cases, int Mismatches, int KnownDoubleRounding);

    /// <summary>Compares result bytes [0x10, 0x10+resultBytes), integer bytes, FSW under mask, EFLAGS under mask.</summary>
    private Outcome Compare(string name, byte[] body, IEnumerable<(double A, double B)> inputs, ushort cw, int resultBytes, ushort fswMask,
        int intBytes = 0, uint eflagsMask = 0, bool intIsFsw = false)
    {
        var code = HostOracle.Concat(Prologue, body, Epilogue);
        int cases = 0, mismatches = 0, known = 0;
        foreach (var (a, b) in inputs)
        {
            cases++;
            var buf = Buffer(a, b, cw);
            var native = _oracle.Native(code, buf);
            var emu = HostOracle.Emulated(code, buf, out _);
            var resultOk = native.AsSpan(0x10, resultBytes).SequenceEqual(emu.AsSpan(0x10, resultBytes));
            var intOk = intIsFsw
                ? (BinaryPrimitives.ReadUInt16LittleEndian(native.AsSpan(0x30)) & FswMaskC1) == (BinaryPrimitives.ReadUInt16LittleEndian(emu.AsSpan(0x30)) & FswMaskC1)
                : native.AsSpan(0x30, intBytes).SequenceEqual(emu.AsSpan(0x30, intBytes));
            var fswN = BinaryPrimitives.ReadUInt16LittleEndian(native.AsSpan(0x20)) & fswMask;
            var fswE = BinaryPrimitives.ReadUInt16LittleEndian(emu.AsSpan(0x20)) & fswMask;
            var flN = BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(0x28)) & eflagsMask;
            var flE = BinaryPrimitives.ReadUInt32LittleEndian(emu.AsSpan(0x28)) & eflagsMask;
            if (resultOk && intOk && fswN == fswE && flN == flE) continue;

            // Documented limitation: the host keeps an extended exponent and rounds twice (to 53 bits, then to the
            // double subnormal grid on store); the interpreter rounds once. Only a ≤1-ulp subnormal difference qualifies.
            if (!resultOk && resultBytes == 8 && intOk && fswN == fswE && flN == flE)
            {
                var n = BinaryPrimitives.ReadInt64LittleEndian(native.AsSpan(0x10));
                var e = BinaryPrimitives.ReadInt64LittleEndian(emu.AsSpan(0x10));
                var subnormal = (n & 0x7FF0_0000_0000_0000L) == 0 || (e & 0x7FF0_0000_0000_0000L) == 0;
                if (subnormal && Math.Abs(n - e) <= 1) { known++; continue; }
            }
            mismatches++;
            if (mismatches <= 5)
                output.WriteLine($"MISMATCH {name} a={a:R} b={b:R} cw={cw:X4}: result native {Convert.ToHexString(native, 0x10, resultBytes)} emu {Convert.ToHexString(emu, 0x10, resultBytes)}; " +
                    $"int {Convert.ToHexString(native, 0x30, intBytes)}/{Convert.ToHexString(emu, 0x30, intBytes)}; fsw {fswN:X4}/{fswE:X4}; eflags {flN:X}/{flE:X}");
        }
        output.WriteLine($"{name} cw={cw:X4}: {cases} cases, {mismatches} mismatches, {known} documented subnormal double-rounding differences");
        return new Outcome(cases, mismatches, known);
    }

    private static IEnumerable<(double, double)> Pairs(int seed)
    {
        var values = Values(seed, 250).ToArray();
        var rng = new Random(seed + 1);
        foreach (var a in values.Take(40))
            foreach (var b in values.Take(40))
                yield return (a, b);
        for (var i = 0; i < 2000; i++) yield return (values[rng.Next(values.Length)], values[rng.Next(values.Length)]);
    }

    public static TheoryData<string, byte[]> Binary() => new()
    {
        { "faddp", [0xDE, 0xC1] }, { "fsubp", [0xDE, 0xE9] }, { "fsubrp", [0xDE, 0xE1] },
        { "fmulp", [0xDE, 0xC9] }, { "fdivp", [0xDE, 0xF9] }, { "fdivrp", [0xDE, 0xF1] },
        { "fadd st0,st1", [0xD8, 0xC1, 0xDD, 0xD9] }, // fadd st0,st1 ; fstp st1
        { "fsub st1,st0 (DC E9)", [0xDC, 0xE9, 0xDD, 0xD8] }, // DC E8+i = FSUB ST(i),ST(0) ; pop
    };

    [Theory]
    [MemberData(nameof(Binary))]
    public void Arithmetic_matches_host_at_53_and_24_bit_precision(string name, byte[] op)
    {
        if (!HostOracle.Available) return;
        var body = HostOracle.Concat(LoadA, LoadB, op, StoreResult);
        foreach (var cw in new[] { Pc53, Pc24 })
            Assert.Equal(0, Compare(name, body, Pairs(name.Length), cw, 8, FswMask).Mismatches);
    }

    [Fact]
    public void Memory_operand_forms_match_host()
    {
        if (!HostOracle.Available) return;
        // fld a ; fadd/fmul/fsub/fdiv qword [rcx+8] ; fstp result
        foreach (var (name, op) in new[] { ("fadd m64", (byte)0x41), ("fmul m64", (byte)0x49), ("fsub m64", (byte)0x61), ("fdivr m64", (byte)0x79) })
            Assert.Equal(0, Compare(name, HostOracle.Concat(LoadA, [0xDC, op, 0x08], StoreResult), Pairs(7), Pc53, 8, FswMask).Mismatches);
        // fld a ; fiadd dword [rcx+8] (integer operand from the low half of b)
        Assert.Equal(0, Compare("fiadd m32", HostOracle.Concat(LoadA, [0xDA, 0x41, 0x08], StoreResult), Pairs(8), Pc53, 8, FswMask).Mismatches);
    }

    [Fact]
    public void Sqrt_rndint_chs_abs_match_host()
    {
        if (!HostOracle.Available) return;
        var singles = Values(11).Select(v => (v, 0.0)).ToArray();
        Assert.Equal(0, Compare("fsqrt", HostOracle.Concat(LoadA, [0xD9, 0xFA], StoreResult), singles, Pc53, 8, FswMask).Mismatches);
        Assert.Equal(0, Compare("fchs", HostOracle.Concat(LoadA, [0xD9, 0xE0], StoreResult), singles, Pc53, 8, FswMask).Mismatches);
        Assert.Equal(0, Compare("fabs", HostOracle.Concat(LoadA, [0xD9, 0xE1], StoreResult), singles, Pc53, 8, FswMask).Mismatches);
        for (var rc = 0; rc < 4; rc++)
            Assert.Equal(0, Compare($"frndint rc{rc}", HostOracle.Concat(LoadA, [0xD9, 0xFC], StoreResult), singles, (ushort)(Pc53 | (rc << 10)), 8, FswMask).Mismatches);
    }

    [Fact]
    public void Integer_stores_match_host_in_all_rounding_modes()
    {
        if (!HostOracle.Available) return;
        var singles = Values(12).Select(v => (v, 0.0)).ToArray();
        for (var rc = 0; rc < 4; rc++)
        {
            var cw = (ushort)(Pc53 | (rc << 10));
            Assert.Equal(0, Compare($"fistp m64 rc{rc}", HostOracle.Concat(LoadA, [0xDF, 0x79, 0x30]), singles, cw, 0, FswMask, intBytes: 8).Mismatches);
            Assert.Equal(0, Compare($"fistp m32 rc{rc}", HostOracle.Concat(LoadA, [0xDB, 0x59, 0x30]), singles, cw, 0, FswMask, intBytes: 4).Mismatches);
            Assert.Equal(0, Compare($"fist m16 rc{rc}", HostOracle.Concat(LoadA, [0xDF, 0x51, 0x30], PopSt0), singles, cw, 0, FswMask, intBytes: 2).Mismatches);
        }
    }

    [Fact]
    public void Exact_80_bit_and_64_bit_integer_round_trips_match_host()
    {
        if (!HostOracle.Available) return;
        var rng = new Random(5);
        var raw = Enumerable.Range(0, 3000).Select(_ => (BitConverter.Int64BitsToDouble(rng.NextInt64(long.MinValue, long.MaxValue)), BitConverter.Int64BitsToDouble(rng.NextInt64()))).ToArray();
        // fild qword [rcx] ; fistp qword [rcx+30]: 64-bit integers survive exactly through the 80-bit register
        Assert.Equal(0, Compare("fild/fistp m64", [0xDF, 0x29, 0xDF, 0x79, 0x30], raw, Pc53, 0, FswMask, intBytes: 8).Mismatches);
        // fld tbyte [rcx] ; fstp tbyte [rcx+10]: arbitrary 80-bit patterns (incl. NaN/denormal encodings) are copied exactly
        Assert.Equal(0, Compare("fld/fstp m80", [0xDB, 0x29, 0xDB, 0x79, 0x10], raw, Pc53, 10, FswMask).Mismatches);
        // fld m64 ; fstp m32: double -> single rounding
        Assert.Equal(0, Compare("fld m64/fstp m32", HostOracle.Concat(LoadA, [0xD9, 0x59, 0x10]), Values(13).Select(v => (v, 0.0)), Pc53, 4, FswMask).Mismatches);
        // fld m32 ; fstp m64
        Assert.Equal(0, Compare("fld m32/fstp m64", HostOracle.Concat([0xD9, 0x01], StoreResult), raw, Pc53, 8, FswMask).Mismatches);
    }

    [Fact]
    public void Compares_and_classification_match_host()
    {
        if (!HostOracle.Available) return;
        var pairs = Pairs(21).ToArray();
        // fld a ; fld b ; fcompp  -> C0/C2/C3
        Assert.Equal(0, Compare("fcompp", HostOracle.Concat(LoadA, LoadB, [0xDE, 0xD9]), pairs, Pc53, 0, FswMaskC1).Mismatches);
        // fld a ; fld b ; fucompp
        Assert.Equal(0, Compare("fucompp", HostOracle.Concat(LoadA, LoadB, [0xDA, 0xE9]), pairs, Pc53, 0, FswMaskC1).Mismatches);
        // fld a ; fld b ; fcomip st0,st1 ; fstp st0 ; pushfq ; pop [rcx+28]
        const uint zpc = X86Cpu.ZF | X86Cpu.PF | X86Cpu.CF;
        Assert.Equal(0, Compare("fcomip", HostOracle.Concat(LoadA, LoadB, [0xDF, 0xF1], PopSt0, [0x9C, 0x8F, 0x41, 0x28]), pairs, Pc53, 0, FswMask, eflagsMask: zpc).Mismatches);
        Assert.Equal(0, Compare("fucomip", HostOracle.Concat(LoadA, LoadB, [0xDF, 0xE9], PopSt0, [0x9C, 0x8F, 0x41, 0x28]), pairs, Pc53, 0, FswMask, eflagsMask: zpc).Mismatches);
        // fld a ; ftst ; fxam ; fstp st0
        var singles = Values(22).Select(v => (v, 0.0)).ToArray();
        // capture FSW immediately (fnstsw [rcx+30]); the later pop would overwrite C1 (round-up indicator)
        Assert.Equal(0, Compare("ftst", HostOracle.Concat(LoadA, [0xD9, 0xE4], [0xDD, 0x79, 0x30], PopSt0), singles, Pc53, 0, FswMask, intBytes: 2, intIsFsw: true).Mismatches);
        Assert.Equal(0, Compare("fxam", HostOracle.Concat(LoadA, [0xD9, 0xE5], [0xDD, 0x79, 0x30], PopSt0), singles, Pc53, 0, FswMask, intBytes: 2, intIsFsw: true).Mismatches);
    }

    [Fact]
    public void Stack_underflow_and_constants_match_host()
    {
        if (!HostOracle.Available) return;
        var one = new[] { (0.0, 0.0) };
        // faddp on an empty stack: invalid + stack fault, result indefinite stored by the following fstp
        Assert.Equal(0, Compare("underflow faddp", [0xDE, 0xC1, 0xDD, 0x59, 0x10], one, Pc53, 8, FswMask).Mismatches);
        foreach (var (name, op) in new[] { ("fld1", (byte)0xE8), ("fldl2t", (byte)0xE9), ("fldl2e", (byte)0xEA), ("fldpi", (byte)0xEB), ("fldlg2", (byte)0xEC), ("fldln2", (byte)0xED), ("fldz", (byte)0xEE) })
            Assert.Equal(0, Compare(name, HostOracle.Concat([0xD9, op], StoreResult), one, Pc53, 8, FswMask).Mismatches);
    }

    [Fact]
    public void Fscale_and_fprem_match_host()
    {
        if (!HostOracle.Available) return;
        var rng = new Random(31);
        var scale = Enumerable.Range(0, 1500).Select(_ => ((rng.NextDouble() - 0.5) * 1000, (double)rng.Next(-60, 60))).ToArray();
        // fld b ; fld a ; fscale ; fstp result ; fstp st0
        Assert.Equal(0, Compare("fscale", HostOracle.Concat(LoadB, LoadA, [0xD9, 0xFD], StoreResult, PopSt0), scale, Pc53, 8, FswMask).Mismatches);
        var rem = Enumerable.Range(0, 1500).Select(_ => ((rng.NextDouble() - 0.5) * 1e6, (rng.NextDouble() + 0.01) * 37)).ToArray();
        // fld b ; fld a ; fprem ; fstp result ; fstp st0  (quotient bits in C0/C3/C1)
        Assert.Equal(0, Compare("fprem", HostOracle.Concat(LoadB, LoadA, [0xD9, 0xF8], [0xDD, 0x79, 0x30], StoreResult, PopSt0), rem, Pc53, 8, FswMask, intBytes: 2, intIsFsw: true).Mismatches);
        Assert.Equal(0, Compare("fprem1", HostOracle.Concat(LoadB, LoadA, [0xD9, 0xF5], [0xDD, 0x79, 0x30], StoreResult, PopSt0), rem, Pc53, 8, FswMask, intBytes: 2, intIsFsw: true).Mismatches);
    }

    [Fact]
    public void Transcendentals_are_within_two_ulp_of_host()
    {
        if (!HostOracle.Available) return;
        var rng = new Random(41);
        var maxUlp = 0L;
        foreach (var (name, op, gen) in new (string, byte[], Func<double>)[]
        {
            ("fsin", [0xD9, 0xFE], () => (rng.NextDouble() - 0.5) * 100),
            ("fcos", [0xD9, 0xFF], () => (rng.NextDouble() - 0.5) * 100),
            ("f2xm1", [0xD9, 0xF0], () => rng.NextDouble() * 2 - 1),
        })
        {
            var code = HostOracle.Concat(Prologue, LoadA, op, StoreResult, Epilogue);
            for (var i = 0; i < 2000; i++)
            {
                var buf = Buffer(gen(), 0, Pc53);
                var n = BinaryPrimitives.ReadInt64LittleEndian(_oracle.Native(code, buf).AsSpan(0x10));
                var e = BinaryPrimitives.ReadInt64LittleEndian(HostOracle.Emulated(code, buf, out _).AsSpan(0x10));
                var ulp = Math.Abs(n - e);
                // results near zero (e.g. sin near kπ) are compared in absolute terms
                if (Math.Abs(BitConverter.Int64BitsToDouble(n)) < 1e-12) ulp = Math.Abs(BitConverter.Int64BitsToDouble(n) - BitConverter.Int64BitsToDouble(e)) < 1e-15 ? 0 : ulp;
                maxUlp = Math.Max(maxUlp, ulp);
                Assert.True(ulp <= 2, $"{name}: {ulp} ulp apart");
            }
        }
        output.WriteLine($"transcendentals: max difference {maxUlp} ulp vs host x87");
    }
}
