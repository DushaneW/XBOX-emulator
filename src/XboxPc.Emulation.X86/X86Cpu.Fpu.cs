using System.Numerics;
using XboxPc.Emulation.Core;

namespace XboxPc.Emulation.X86;

/// <summary>
/// x87 FPU.
/// Registers are stored in the native 80-bit extended format (64-bit significand + sign/15-bit exponent), so
/// FLD/FSTP m80, FILD/FISTP m64, register moves and MMX aliasing are exact.
/// Arithmetic is evaluated in IEEE double and then rounded to the precision-control setting:
///   * exact for PC = 24 or 53 bits (round-to-nearest) as long as results stay inside the double exponent range;
///   * PC = 64 bits is approximated with 53 bits;
///   * rounding-control modes other than nearest are honoured by FIST/FISTP/FRNDINT but not by + − x ÷ √;
///   * transcendental instructions use .NET Math and may differ from Pentium III microcode in the last bits;
///   * flags tracked: IE, ZE, OE, SF, C0-C3, TOP. DE, UE, PE are not tracked;
///   * unmasked exceptions are recorded (ES/B) and the next waiting x87 instruction stops with
///     <see cref="UnimplementedInstructionException"/> (#MF delivery is not modelled).
/// </summary>
public sealed partial class X86Cpu
{
    public const ushort FpIE = 0x0001, FpDE = 0x0002, FpZE = 0x0004, FpOE = 0x0008, FpUE = 0x0010, FpPE = 0x0020;
    public const ushort FpSF = 0x0040, FpES = 0x0080, FpC0 = 0x0100, FpC1 = 0x0200, FpC2 = 0x0400, FpC3 = 0x4000, FpBusy = 0x8000;

    private const ulong IndefiniteMantissa = 0xC000_0000_0000_0000UL;
    private const ushort IndefiniteExponent = 0xFFFF;

    private readonly ulong[] _fpMant = new ulong[8];
    private readonly ushort[] _fpExp = new ushort[8];
    private int _fpTop;
    private ushort _fpTags = 0xFFFF;
    private ushort _fpStatus;

    public ushort FpuControl { get; set; } = 0x037F;

    public ushort FpuStatus
    {
        get => (ushort)((_fpStatus & ~0x3800) | (_fpTop << 11));
        set { _fpStatus = (ushort)(value & ~0x3800); _fpTop = (value >> 11) & 7; }
    }

    public ushort FpuTagWord => _fpTags;

    /// <summary>ST(i) as a double (for diagnostics/tests).</summary>
    public double FpuSt(int i) => ExtToDouble(_fpMant[Phys(i)], _fpExp[Phys(i)]);

    // ------------------------------------------------------------------ 80-bit format conversions

    public static (ulong Mant, ushort Exp) DoubleToExt(double d)
    {
        var bits = BitConverter.DoubleToUInt64Bits(d);
        var sign = (ushort)((bits >> 48) & 0x8000);
        var e = (int)((bits >> 52) & 0x7FF);
        var f = bits & 0x000F_FFFF_FFFF_FFFFUL;
        if (e == 0x7FF) return ((1UL << 63) | (f << 11), (ushort)(sign | 0x7FFF));
        if (e == 0)
        {
            if (f == 0) return (0, sign);
            var shift = BitOperations.LeadingZeroCount(f);
            return (f << shift, (ushort)(sign | (-1074 - shift + 63 + 16383)));
        }
        return ((1UL << 63) | (f << 11), (ushort)(sign | (e - 1023 + 16383)));
    }

    public static double ExtToDouble(ulong mant, ushort sexp)
    {
        var negative = (sexp & 0x8000) != 0;
        var e = sexp & 0x7FFF;
        if (e == 0x7FFF)
        {
            if ((mant << 1) == 0) return negative ? double.NegativeInfinity : double.PositiveInfinity;
            var payload = (mant << 1) >> 12;
            var bits = (negative ? 1UL << 63 : 0) | 0x7FF0_0000_0000_0000UL | payload;
            if (payload == 0) bits |= 1UL << 51;
            return BitConverter.UInt64BitsToDouble(bits);
        }
        if (mant == 0) return negative ? -0.0 : 0.0;
        var v = Math.ScaleB((double)mant, e - 16383 - 63);
        return negative ? -v : v;
    }

    private static (ulong Mant, ushort Exp) Int64ToExt(long value)
    {
        if (value == 0) return (0, 0);
        var sign = value < 0 ? (ushort)0x8000 : (ushort)0;
        var magnitude = value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;
        var lz = BitOperations.LeadingZeroCount(magnitude);
        return (magnitude << lz, (ushort)(sign | (16383 + 63 - lz)));
    }

    // ------------------------------------------------------------------ stack

    private int Phys(int i) => (_fpTop + i) & 7;
    private bool IsEmpty(int phys) => ((_fpTags >> (phys * 2)) & 3) == 3;

    private void SetTag(int phys, int tag) => _fpTags = (ushort)((_fpTags & ~(3 << (phys * 2))) | (tag << (phys * 2)));

    private static int TagFor(ulong mant, ushort exp)
    {
        var e = exp & 0x7FFF;
        if (e == 0x7FFF || (e == 0 && mant != 0) || (e != 0 && (mant >> 63) == 0)) return 2; // special
        return mant == 0 ? 1 : 0;
    }

    private void WritePhys(int phys, ulong mant, ushort exp)
    {
        _fpMant[phys] = mant;
        _fpExp[phys] = exp;
        SetTag(phys, TagFor(mant, exp));
    }

    private void FpRaise(ushort flags)
    {
        _fpStatus |= flags;
        if ((flags & 0x3F & ~FpuControl) != 0) _fpStatus |= FpES | FpBusy;
    }

    private bool StackUnderflow(params int[] indices)
    {
        foreach (var i in indices)
        {
            if (IsEmpty(Phys(i)))
            {
                _fpStatus &= unchecked((ushort)~FpC1);
                FpRaise(FpIE | FpSF);
                return true;
            }
        }
        return false;
    }

    private double St(int i) => ExtToDouble(_fpMant[Phys(i)], _fpExp[Phys(i)]);

    private void SetSt(int i, double v)
    {
        var (m, e) = DoubleToExt(v);
        WritePhys(Phys(i), m, e);
    }

    private void SetStIndefinite(int i) => WritePhys(Phys(i), IndefiniteMantissa, IndefiniteExponent);

    private void FpPushExt(ulong mant, ushort exp)
    {
        var p = (_fpTop - 1) & 7;
        if (!IsEmpty(p))
        {
            FpRaise(FpIE | FpSF);
            _fpStatus |= FpC1; // overflow
            mant = IndefiniteMantissa;
            exp = IndefiniteExponent;
        }
        _fpTop = p;
        WritePhys(p, mant, exp);
    }

    private void FpPush(double v)
    {
        var (m, e) = DoubleToExt(v);
        FpPushExt(m, e);
    }

    private void FpPop()
    {
        SetTag(Phys(0), 3);
        _fpTop = (_fpTop + 1) & 7;
    }

    // ------------------------------------------------------------------ arithmetic helpers

    /// <summary>
    /// Precision control 24 bits rounds the significand only; the exponent range stays extended (it must NOT be a cast
    /// to float, which would overflow values beyond float range). Round-to-nearest-even on the dropped 29 bits.
    /// </summary>
    private double RoundToPrecision(double v) => RoundToPrecision(v, 0);

    /// <param name="residual">Exact error of the double result (true value = v + residual, |residual| ≤ ½ ulp).
    /// Breaks ties that double rounding would otherwise get wrong.</param>
    private double RoundToPrecision(double v, double residual)
    {
        if (((FpuControl >> 8) & 3) != 0) return v;
        var bits = BitConverter.DoubleToUInt64Bits(v);
        var e = (int)((bits >> 52) & 0x7FF);
        if (e == 0x7FF || (bits & 0x7FFF_FFFF_FFFF_FFFFUL) == 0) return v;
        // Normal doubles: 53 significant bits -> drop 29. Double subnormals are still normal in the 80-bit register,
        // so they also keep 24 *significant* bits counted from their leading one.
        var drop = 29;
        if (e == 0)
        {
            var significant = 64 - BitOperations.LeadingZeroCount(bits & 0x000F_FFFF_FFFF_FFFFUL);
            drop = significant - 24;
            if (drop <= 0) return v;
        }
        var mask = (1UL << drop) - 1;
        var half = 1UL << (drop - 1);
        var low = bits & mask;
        bits &= ~mask;
        var awayFromZero = residual != 0 && (residual > 0) == (v > 0);
        var towardZero = residual != 0 && !awayFromZero;
        if (low > half || (low == half && (awayFromZero || (!towardZero && ((bits >> drop) & 1) != 0)))) bits += 1UL << drop;
        return BitConverter.UInt64BitsToDouble(bits);
    }

    private static bool IsSignalingNaN(double v) => double.IsNaN(v) && (BitConverter.DoubleToUInt64Bits(v) & (1UL << 51)) == 0;

    private static double Quiet(double v) => BitConverter.UInt64BitsToDouble(BitConverter.DoubleToUInt64Bits(v) | (1UL << 51));

    private static readonly double Indefinite = BitConverter.UInt64BitsToDouble(0xFFF8_0000_0000_0000UL);

    /// <summary>x87 NaN propagation: SNaN -> IE; two NaNs -> larger significand wins.</summary>
    private double? PropagateNaN(double a, double b)
    {
        bool na = double.IsNaN(a), nb = double.IsNaN(b);
        if (!na && !nb) return null;
        bool sa0 = IsSignalingNaN(a), sb0 = IsSignalingNaN(b);
        if (sa0 || sb0) FpRaise(FpIE);
        if (na && nb && sa0 != sb0) return Quiet(sa0 ? b : a); // SNaN + QNaN -> the QNaN (Intel SDM vol.1 table 4-7)
        if (na && nb)
        {
            var sa = BitConverter.DoubleToUInt64Bits(a) & 0x000F_FFFF_FFFF_FFFFUL | (1UL << 51);
            var sb = BitConverter.DoubleToUInt64Bits(b) & 0x000F_FFFF_FFFF_FFFFUL | (1UL << 51);
            return Quiet(sa >= sb ? a : b);
        }
        return Quiet(na ? a : b);
    }

    /// <summary>op: 0 add, 1 mul, 4 sub (a−b), 5 subr (b−a), 6 div (a/b), 7 divr (b/a).</summary>
    private double FpArith(int op, double a, double b)
    {
        if (PropagateNaN(a, b) is { } nan) return nan;
        if (op is 5 or 7) (a, b) = (b, a);
        double r, residual = 0;
        switch (op)
        {
            case 0: r = a + b; residual = TwoSumError(a, b, r); break;
            case 1: r = a * b; residual = double.IsFinite(r) ? Math.FusedMultiplyAdd(a, b, -r) : 0; break;
            case 4: case 5: r = a - b; residual = TwoSumError(a, -b, r); break;
            default:
                if (b == 0 && !double.IsNaN(a) && a != 0 && !double.IsInfinity(a)) FpRaise(FpZE);
                r = a / b;
                // sign of (a − r·b) / b gives the direction of the true quotient relative to r
                if (double.IsFinite(r) && r != 0 && double.IsFinite(b) && b != 0) residual = Math.FusedMultiplyAdd(-r, b, a) / b;
                break;
        }
        if (double.IsNaN(r)) { FpRaise(FpIE); return Indefinite; }
        r = RoundToPrecision(r, double.IsFinite(residual) ? residual : 0);
        if (double.IsInfinity(r) && !double.IsInfinity(a) && !double.IsInfinity(b) && !(op is 6 or 7 && b == 0)) FpRaise(FpOE | FpPE);
        return r;
    }

    /// <summary>Knuth TwoSum: exact error of the floating-point sum s = fl(a + b).</summary>
    private static double TwoSumError(double a, double b, double s)
    {
        if (!double.IsFinite(s)) return 0;
        var bb = s - a;
        return (a - (s - bb)) + (b - bb);
    }

    private void SetCompareFlags(double a, double b, bool unordered)
    {
        _fpStatus &= unchecked((ushort)~(FpC0 | FpC2 | FpC3 | FpC1));
        if (double.IsNaN(a) || double.IsNaN(b))
        {
            _fpStatus |= FpC0 | FpC2 | FpC3;
            if (!unordered || IsSignalingNaN(a) || IsSignalingNaN(b)) FpRaise(FpIE);
        }
        else if (a < b) _fpStatus |= FpC0;
        else if (a == b) _fpStatus |= FpC3;
    }

    private void SetEflagsCompare(double a, double b, bool unordered)
    {
        Eflags &= ~(ZF | PF | CF | OF | SF | AF);
        if (double.IsNaN(a) || double.IsNaN(b))
        {
            Eflags |= ZF | PF | CF;
            if (!unordered || IsSignalingNaN(a) || IsSignalingNaN(b)) FpRaise(FpIE);
        }
        else if (a < b) Eflags |= CF;
        else if (a == b) Eflags |= ZF;
    }

    private int RoundingControl => (FpuControl >> 10) & 3;

    /// <summary>Exact conversion of an 80-bit value to an integer using the current rounding control. Null = out of range/NaN.</summary>
    private long? ExtToInteger(ulong mant, ushort sexp, int bits)
    {
        var negative = (sexp & 0x8000) != 0;
        var e = sexp & 0x7FFF;
        if (e == 0x7FFF) return null;
        if (mant == 0) return 0;
        var shift = e - 16383 - 63;
        UInt128 magnitude;
        bool roundUp;
        if (shift > 0) return null;
        if (shift == 0) { magnitude = mant; roundUp = false; }
        else
        {
            var rs = -shift;
            UInt128 m = mant;
            if (rs >= 128) { magnitude = 0; roundUp = Decide(fracNonZero: true, half: false, aboveHalf: false, odd: false); }
            else
            {
                magnitude = m >> rs;
                var frac = m & ((UInt128.One << rs) - 1);
                var halfPoint = UInt128.One << (rs - 1);
                roundUp = Decide(frac != 0, frac == halfPoint, frac > halfPoint, (magnitude & 1) != 0);
            }
        }
        if (roundUp) magnitude++;
        var limit = (UInt128)1 << (bits - 1);
        if (negative ? magnitude > limit : magnitude >= limit) return null;
        var value = (long)(ulong)magnitude;
        return negative ? -value : value;

        bool Decide(bool fracNonZero, bool half, bool aboveHalf, bool odd)
        {
            if (!fracNonZero) return false;
            return RoundingControl switch
            {
                0 => aboveHalf || (half && odd),
                1 => negative,   // toward −∞: magnitude grows for negatives
                2 => !negative,  // toward +∞
                _ => false,      // truncate
            };
        }
    }

    private double RoundToIntegralByControl(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return v;
        var r = RoundingControl switch
        {
            0 => Math.Round(v, MidpointRounding.ToEven),
            1 => Math.Floor(v),
            2 => Math.Ceiling(v),
            _ => Math.Truncate(v),
        };
        return r == 0 ? Math.CopySign(0.0, v) : r;
    }

    // ------------------------------------------------------------------ memory operand access

    private double ReadFpOperand(int format, uint a) => format switch
    {
        0 => SingleBitsToDouble(_bus.Read32(a)),                  // m32fp (SNaN-preserving)
        1 => (int)_bus.Read32(a),                                   // m32int
        2 => BitConverter.UInt64BitsToDouble(_bus.Read32(a) | ((ulong)_bus.Read32(a + 4) << 32)), // m64fp
        _ => (short)_bus.Read16(a),                                 // m16int
    };

    /// <summary>float -> double widening that keeps a signaling NaN signaling (the .NET/SSE conversion quiets it).</summary>
    private static double SingleBitsToDouble(uint f)
    {
        if ((f & 0x7F80_0000) == 0x7F80_0000 && (f & 0x007F_FFFF) != 0)
            return BitConverter.UInt64BitsToDouble(((ulong)(f & 0x8000_0000) << 32) | 0x7FF0_0000_0000_0000UL | ((ulong)(f & 0x007F_FFFF) << 29));
        return BitConverter.Int32BitsToSingle((int)f);
    }

    /// <summary>FLD m32/m64 of an SNaN raises IE and loads the quieted NaN.</summary>
    private double LoadSignaling(double v)
    {
        if (!IsSignalingNaN(v)) return v;
        FpRaise(FpIE);
        return Quiet(v);
    }

    private void StoreExt(uint a, ulong mant, ushort exp)
    {
        _bus.Write32(a, (uint)mant);
        _bus.Write32(a + 4, (uint)(mant >> 32));
        _bus.Write16(a + 8, exp);
    }

    private (ulong Mant, ushort Exp) LoadExt(uint a) => (_bus.Read32(a) | ((ulong)_bus.Read32(a + 4) << 32), _bus.Read16(a + 8));

    private void StoreSingle(uint a, double v)
    {
        var f = (float)v;
        if (double.IsNaN(v)) f = BitConverter.Int32BitsToSingle((int)(0x7FC0_0000u | ((uint)(BitConverter.DoubleToUInt64Bits(v) >> 32) & 0x8000_0000u) | (uint)((BitConverter.DoubleToUInt64Bits(v) >> 29) & 0x003F_FFFF)));
        else if (float.IsInfinity(f) && !double.IsInfinity(v)) FpRaise(FpOE | FpPE);
        _bus.Write32(a, (uint)BitConverter.SingleToInt32Bits(f));
    }

    private void StoreDouble(uint a, double v)
    {
        var bits = BitConverter.DoubleToUInt64Bits(v);
        _bus.Write32(a, (uint)bits);
        _bus.Write32(a + 4, (uint)(bits >> 32));
    }

    private void StoreInteger(uint a, int bits, long? value)
    {
        long v;
        if (value is null) { FpRaise(FpIE); v = bits == 64 ? long.MinValue : bits == 32 ? int.MinValue : short.MinValue; }
        else v = value.Value;
        switch (bits)
        {
            case 16: _bus.Write16(a, (ushort)v); break;
            case 32: _bus.Write32(a, (uint)v); break;
            default: _bus.Write32(a, (uint)v); _bus.Write32(a + 4, (uint)((ulong)v >> 32)); break;
        }
    }

    private int ComputeFullTag(int phys) => IsEmpty(phys) ? 3 : TagFor(_fpMant[phys], _fpExp[phys]);

    private void StoreEnvironment(uint a)
    {
        ushort tags = 0;
        for (var p = 0; p < 8; p++) tags |= (ushort)(ComputeFullTag(p) << (p * 2));
        _bus.Write32(a, FpuControl | 0xFFFF_0000u);
        _bus.Write32(a + 4, FpuStatus | 0xFFFF_0000u);
        _bus.Write32(a + 8, tags | 0xFFFF_0000u);
        // FPU instruction/operand pointers are not tracked: stored as zero (documented limitation).
        for (uint o = 12; o < 28; o += 4) _bus.Write32(a + o, 0);
    }

    private void LoadEnvironment(uint a)
    {
        FpuControl = (ushort)_bus.Read32(a);
        FpuStatus = (ushort)_bus.Read32(a + 4);
        var tags = (ushort)_bus.Read32(a + 8);
        _fpTags = 0;
        for (var p = 0; p < 8; p++) SetTag(p, ((tags >> (p * 2)) & 3) == 3 ? 3 : TagFor(_fpMant[p], _fpExp[p]));
    }

    private void FpInit()
    {
        FpuControl = 0x037F;
        _fpStatus = 0;
        _fpTop = 0;
        _fpTags = 0xFFFF;
    }

    private void FpuWait()
    {
        if ((_fpStatus & 0x3F & ~FpuControl) != 0)
            throw Unimpl("pending unmasked x87 exception (#MF delivery is not modelled)");
    }

    // ------------------------------------------------------------------ decoder / executor

    private void ExecuteFpu(byte op)
    {
        var m = Decode();
        var r = m.Reg;
        var i = m.Rm;

        // Non-waiting control forms skip the pending-exception check.
        var noWait = (op == 0xDB && m.IsReg && i is 2 or 3) || (op == 0xDF && m.IsReg && r == 4) ||
                     (!m.IsReg && ((op == 0xD9 && r is 6 or 7) || (op == 0xDD && r is 6 or 7)));
        if (!noWait) FpuWait();

        if (!m.IsReg)
        {
            var a = Linear(m);
            switch (op)
            {
                case 0xD8: case 0xDA: case 0xDC: case 0xDE:
                {
                    var format = op switch { 0xD8 => 0, 0xDA => 1, 0xDC => 2, _ => 3 };
                    var src = ReadFpOperand(format, a);
                    if (StackUnderflow(0)) { if (r is not (2 or 3)) SetStIndefinite(0); else _fpStatus |= FpC0 | FpC2 | FpC3; if (r == 3) FpPop(); return; }
                    if (r is 2 or 3) { SetCompareFlags(St(0), src, unordered: false); if (r == 3) FpPop(); return; }
                    SetSt(0, FpArith(r, St(0), src));
                    return;
                }
                case 0xD9:
                    switch (r)
                    {
                        case 0: FpPush(LoadSignaling(ReadFpOperand(0, a))); return;
                        case 2: case 3:
                            if (StackUnderflow(0)) { StoreSingle(a, Indefinite); }
                            else StoreSingle(a, St(0));
                            if (r == 3) FpPop();
                            return;
                        case 4: LoadEnvironment(a); return;
                        case 5: FpuControl = _bus.Read16(a); return;
                        case 6: StoreEnvironment(a); FpuControl |= 0x3F; return;
                        case 7: _bus.Write16(a, FpuControl); return;
                        default: throw Ud("D9 /1 memory form");
                    }
                case 0xDB:
                    switch (r)
                    {
                        case 0: { var (mm, ee) = Int64ToExt((int)_bus.Read32(a)); FpPushExt(mm, ee); return; }
                        case 2: case 3:
                            StoreInteger(a, 32, StackUnderflow(0) ? null : ExtToInteger(_fpMant[Phys(0)], _fpExp[Phys(0)], 32));
                            if (r == 3) FpPop();
                            return;
                        case 5: { var (mm, ee) = LoadExt(a); FpPushExt(mm, ee); return; }
                        case 7:
                            if (StackUnderflow(0)) StoreExt(a, IndefiniteMantissa, IndefiniteExponent);
                            else StoreExt(a, _fpMant[Phys(0)], _fpExp[Phys(0)]);
                            FpPop();
                            return;
                        case 1: throw Unimpl("FISTTP (SSE3, not present on Pentium III)");
                        default: throw Ud("DB /4 or /6 memory form");
                    }
                case 0xDD:
                    switch (r)
                    {
                        case 0: FpPush(LoadSignaling(ReadFpOperand(2, a))); return;
                        case 2: case 3:
                            StoreDouble(a, StackUnderflow(0) ? Indefinite : St(0));
                            if (r == 3) FpPop();
                            return;
                        case 4:
                            LoadEnvironment(a);
                            for (var k = 0; k < 8; k++)
                            {
                                var (mm, ee) = LoadExt(a + 28 + (uint)(k * 10));
                                _fpMant[Phys(k)] = mm;
                                _fpExp[Phys(k)] = ee;
                                if (!IsEmpty(Phys(k))) SetTag(Phys(k), TagFor(mm, ee));
                            }
                            return;
                        case 6:
                            StoreEnvironment(a);
                            for (var k = 0; k < 8; k++) StoreExt(a + 28 + (uint)(k * 10), _fpMant[Phys(k)], _fpExp[Phys(k)]);
                            FpInit();
                            return;
                        case 7: _bus.Write16(a, FpuStatus); return;
                        case 1: throw Unimpl("FISTTP (SSE3, not present on Pentium III)");
                        default: throw Ud("DD /5 memory form");
                    }
                default: // 0xDF
                    switch (r)
                    {
                        case 0: { var (mm, ee) = Int64ToExt((short)_bus.Read16(a)); FpPushExt(mm, ee); return; }
                        case 2: case 3:
                            StoreInteger(a, 16, StackUnderflow(0) ? null : ExtToInteger(_fpMant[Phys(0)], _fpExp[Phys(0)], 16));
                            if (r == 3) FpPop();
                            return;
                        case 5:
                        {
                            var v = (long)(_bus.Read32(a) | ((ulong)_bus.Read32(a + 4) << 32));
                            var (mm, ee) = Int64ToExt(v);
                            FpPushExt(mm, ee);
                            return;
                        }
                        case 7:
                            StoreInteger(a, 64, StackUnderflow(0) ? null : ExtToInteger(_fpMant[Phys(0)], _fpExp[Phys(0)], 64));
                            FpPop();
                            return;
                        case 4: case 6: throw Unimpl("FBLD/FBSTP (packed BCD)");
                        default: throw Unimpl("FISTTP (SSE3, not present on Pentium III)");
                    }
            }
        }

        switch (op)
        {
            case 0xD8:
                if (StackUnderflow(0, i)) { if (r is 2 or 3) { _fpStatus |= FpC0 | FpC2 | FpC3; if (r == 3) FpPop(); } else SetStIndefinite(0); return; }
                if (r is 2 or 3) { SetCompareFlags(St(0), St(i), unordered: false); if (r == 3) FpPop(); return; }
                SetSt(0, FpArith(r, St(0), St(i)));
                return;
            case 0xDC:
            case 0xDE:
            {
                if (op == 0xDE && r == 3)
                {
                    if (i != 1) throw Ud("DE D8-DF other than FCOMPP");
                    if (StackUnderflow(0, 1)) _fpStatus |= FpC0 | FpC2 | FpC3;
                    else SetCompareFlags(St(0), St(1), unordered: false);
                    FpPop();
                    FpPop();
                    return;
                }
                if (r is 2 or 3) throw Ud("DC/DE register compare forms are reserved");
                // Register forms write ST(i) and swap the sense of SUB/SUBR and DIV/DIVR.
                var mapped = r switch { 4 => 5, 5 => 4, 6 => 7, 7 => 6, _ => r };
                if (StackUnderflow(0, i)) SetStIndefinite(i);
                else SetSt(i, FpArith(mapped, St(i), St(0)) );
                if (op == 0xDE) FpPop();
                return;
            }
            case 0xD9: ExecuteD9Register(r, i); return;
            case 0xDA:
                if (r == 5 && i == 1)
                {
                    if (StackUnderflow(0, 1)) _fpStatus |= FpC0 | FpC2 | FpC3;
                    else SetCompareFlags(St(0), St(1), unordered: true);
                    FpPop();
                    FpPop();
                    return;
                }
                if (r <= 3) { FpCmov(r switch { 0 => GetFlag(CF), 1 => GetFlag(ZF), 2 => GetFlag(CF) || GetFlag(ZF), _ => GetFlag(PF) }, i); return; }
                throw Ud("DA register form");
            case 0xDB:
                if (r <= 3) { FpCmov(!(r switch { 0 => GetFlag(CF), 1 => GetFlag(ZF), 2 => GetFlag(CF) || GetFlag(ZF), _ => GetFlag(PF) }), i); return; }
                if (r == 4)
                {
                    switch (i)
                    {
                        case 2: _fpStatus &= unchecked((ushort)~(0x3F | FpSF | FpES | FpBusy)); return; // FNCLEX
                        case 3: FpInit(); return;                                                     // FNINIT
                        case 0: case 1: case 4: return;                                               // FENI/FDISI/FSETPM: no-ops on 387+
                        default: throw Ud("DB E5-E7");
                    }
                }
                if (r is 5 or 6)
                {
                    if (StackUnderflow(0, i)) { Eflags |= ZF | PF | CF; return; }
                    SetEflagsCompare(St(0), St(i), unordered: r == 5);
                    return;
                }
                throw Ud("DB F8-FF");
            case 0xDD:
                switch (r)
                {
                    case 0: SetTag(Phys(i), 3); return; // FFREE
                    case 2: case 3:
                        if (StackUnderflow(0)) SetStIndefinite(i);
                        else WritePhys(Phys(i), _fpMant[Phys(0)], _fpExp[Phys(0)]);
                        if (r == 3) FpPop();
                        return;
                    case 4: case 5:
                        if (StackUnderflow(0, i)) _fpStatus |= FpC0 | FpC2 | FpC3;
                        else SetCompareFlags(St(0), St(i), unordered: true);
                        if (r == 5) FpPop();
                        return;
                    default: throw Ud("DD register form");
                }
            default: // 0xDF
                if (r == 4 && i == 0) { SetReg(16, EAX, FpuStatus); return; } // FNSTSW AX
                if (r is 5 or 6)
                {
                    if (StackUnderflow(0, i)) Eflags |= ZF | PF | CF;
                    else SetEflagsCompare(St(0), St(i), unordered: r == 5);
                    FpPop();
                    return;
                }
                throw Ud("DF register form");
        }
    }

    private void FpCmov(bool condition, int i)
    {
        if (StackUnderflow(0, i)) return;
        if (condition) WritePhys(Phys(0), _fpMant[Phys(i)], _fpExp[Phys(i)]);
    }

    private static readonly double[] Constants = [1.0, Math.Log2(10.0), Math.Log2(Math.E), Math.PI, Math.Log10(2.0), Math.Log(2.0), 0.0];

    private void ExecuteD9Register(int r, int i)
    {
        switch (r)
        {
            case 0: // FLD ST(i)
                if (StackUnderflow(i)) { FpPushExt(IndefiniteMantissa, IndefiniteExponent); return; }
                FpPushExt(_fpMant[Phys(i)], _fpExp[Phys(i)]);
                return;
            case 1: // FXCH
            {
                if (StackUnderflow(0, i)) return;
                int p0 = Phys(0), pi = Phys(i);
                (_fpMant[p0], _fpMant[pi]) = (_fpMant[pi], _fpMant[p0]);
                (_fpExp[p0], _fpExp[pi]) = (_fpExp[pi], _fpExp[p0]);
                SetTag(p0, TagFor(_fpMant[p0], _fpExp[p0]));
                SetTag(pi, TagFor(_fpMant[pi], _fpExp[pi]));
                _fpStatus &= unchecked((ushort)~FpC1);
                return;
            }
            case 2: if (i == 0) return; throw Ud("D9 D1-D7"); // FNOP
            case 3: throw Ud("D9 D8-DF");
            case 5: // constants
                if (i == 7) throw Ud("D9 EF");
                FpPush(Constants[i]);
                return;
        }

        if (r == 4)
        {
            if (StackUnderflow(0)) { if (i is 0 or 1) SetStIndefinite(0); else _fpStatus |= FpC0 | FpC2 | FpC3; return; }
            var v = St(0);
            switch (i)
            {
                case 0: _fpExp[Phys(0)] ^= 0x8000; return; // FCHS (exact, sign only)
                case 1: _fpExp[Phys(0)] &= 0x7FFF; return; // FABS
                case 4: SetCompareFlags(v, 0.0, unordered: false); return; // FTST
                case 5: Fxam(); return;
                default: throw Ud("D9 E2/E3/E6/E7");
            }
        }

        // r == 6 or 7: transcendental / misc
        var code = (r << 3) | i;
        switch (code)
        {
            case 0x36: _fpTop = (_fpTop - 1) & 7; _fpStatus &= unchecked((ushort)~FpC1); return; // FDECSTP
            case 0x37: _fpTop = (_fpTop + 1) & 7; _fpStatus &= unchecked((ushort)~FpC1); return; // FINCSTP
        }
        if (StackUnderflow(0)) { SetStIndefinite(0); return; }
        var x = St(0);
        switch (code)
        {
            case 0x30: SetSt(0, Unary(x, Expm1Base2(x))); return;                     // F2XM1
            case 0x31: Binary(1, (st0, st1) => st1 * Math.Log2(st0)); return;           // FYL2X
            case 0x32: SetSt(0, Unary(x, Math.Tan(x))); FpPush(1.0); _fpStatus &= unchecked((ushort)~FpC2); return; // FPTAN
            case 0x33: Binary(1, (st0, st1) => Math.Atan2(st1, st0)); return;           // FPATAN
            case 0x34: // FXTRACT
            {
                if (double.IsNaN(x) || double.IsInfinity(x) || x == 0) { SetSt(0, x == 0 ? double.NegativeInfinity : x); if (x == 0) FpRaise(FpZE); FpPush(x); return; }
                var exp = Math.ILogB(x);
                SetSt(0, exp);
                FpPush(Math.ScaleB(x, -exp));
                return;
            }
            case 0x35: Remainder(ieee: true); return;                                    // FPREM1
            case 0x38: Remainder(ieee: false); return;                                   // FPREM
            case 0x39: Binary(1, (st0, st1) => st1 * Math.Log2(st0 + 1)); return;       // FYL2XP1
            case 0x3A: // FSQRT
                if (x < 0 && x != 0) { FpRaise(FpIE); SetSt(0, Indefinite); return; }
                SetSt(0, RoundToPrecision(Math.Sqrt(x)));
                return;
            case 0x3B: { var s = Math.Sin(x); var c = Math.Cos(x); SetSt(0, Unary(x, s)); FpPush(Unary(x, c)); _fpStatus &= unchecked((ushort)~FpC2); return; } // FSINCOS
            case 0x3C: SetSt(0, RoundToIntegralByControl(x)); return;                    // FRNDINT
            case 0x3D: // FSCALE
            {
                if (StackUnderflow(1)) { SetStIndefinite(0); return; }
                var n = Math.Truncate(St(1));
                SetSt(0, double.IsNaN(n) ? n : Math.ScaleB(x, (int)Math.Clamp(n, -100_000, 100_000)));
                return;
            }
            case 0x3E: SetSt(0, Unary(x, Math.Sin(x))); _fpStatus &= unchecked((ushort)~FpC2); return; // FSIN
            case 0x3F: SetSt(0, Unary(x, Math.Cos(x))); _fpStatus &= unchecked((ushort)~FpC2); return; // FCOS
            default: throw Ud($"D9 /{r} {i}");
        }

        double Unary(double input, double result)
        {
            if (double.IsNaN(input)) return Quiet(input);
            if (double.IsNaN(result)) { FpRaise(FpIE); return Indefinite; }
            return result;
        }

        void Binary(int other, Func<double, double, double> f)
        {
            if (StackUnderflow(other)) { SetStIndefinite(other); FpPop(); return; }
            var a = St(0);
            var b = St(other);
            var res = PropagateNaN(a, b) ?? f(a, b);
            if (double.IsNaN(res) && !double.IsNaN(a) && !double.IsNaN(b)) { FpRaise(FpIE); res = Indefinite; }
            SetSt(other, res);
            FpPop();
        }
    }

    /// <summary>
    /// 2^x − 1 for |x| ≤ 1, evaluated in double-double arithmetic (y = x·ln2 split hi/lo, Taylor series of e^y − 1),
    /// then rounded once to double. Accurate to well under 1 ulp.
    /// </summary>
    private static double Expm1Base2(double x)
    {
        if (x == 0 || double.IsNaN(x)) return x;
        if (Math.Abs(x) > 1) return Math.Pow(2, x) - 1; // outside the architectural input range
        const double ln2Hi = 0.6931471805599453, ln2Lo = 2.3190468138462996e-17;
        var (yh, yl) = DdMul(x, 0, ln2Hi, ln2Lo);
        double sh = yh, sl = yl;          // running sum = y
        double th = yh, tl = yl;          // running term = y^n / n!
        for (var n = 2; n < 30; n++)
        {
            (th, tl) = DdMul(th, tl, yh, yl);
            (th, tl) = DdDiv(th, tl, n);
            (sh, sl) = DdAdd(sh, sl, th, tl);
            if (Math.Abs(th) < 1e-40 * Math.Abs(sh)) break;
        }
        return sh + sl;
    }

    private static (double Hi, double Lo) DdAdd(double ah, double al, double bh, double bl)
    {
        var s = ah + bh;
        var e = TwoSumError(ah, bh, s) + al + bl;
        var hi = s + e;
        return (hi, e - (hi - s));
    }

    private static (double Hi, double Lo) DdMul(double ah, double al, double bh, double bl)
    {
        var p = ah * bh;
        var e = Math.FusedMultiplyAdd(ah, bh, -p) + (ah * bl) + (al * bh);
        var hi = p + e;
        return (hi, e - (hi - p));
    }

    private static (double Hi, double Lo) DdDiv(double ah, double al, double b)
    {
        var q = ah / b;
        var r = Math.FusedMultiplyAdd(-q, b, ah) + al;
        var lo = r / b;
        var hi = q + lo;
        return (hi, lo - (hi - q));
    }

    private void Remainder(bool ieee)
    {
        if (StackUnderflow(0, 1)) { SetStIndefinite(0); return; }
        var a = St(0);
        var b = St(1);
        _fpStatus &= unchecked((ushort)~(FpC0 | FpC1 | FpC2 | FpC3));
        if (PropagateNaN(a, b) is { } nan) { SetSt(0, nan); return; }
        if (double.IsInfinity(a) || b == 0) { FpRaise(FpIE); SetSt(0, Indefinite); return; }
        var rem = ieee ? Math.IEEERemainder(a, b) : a % b;
        var q = (long)Math.Abs(Math.Round((a - rem) / b));
        if ((q & 1) != 0) _fpStatus |= FpC1;
        if ((q & 2) != 0) _fpStatus |= FpC3;
        if ((q & 4) != 0) _fpStatus |= FpC0;
        SetSt(0, rem == 0 ? Math.CopySign(0.0, a) : rem);
    }

    private void Fxam()
    {
        _fpStatus &= unchecked((ushort)~(FpC0 | FpC1 | FpC2 | FpC3));
        var p = Phys(0);
        if ((_fpExp[p] & 0x8000) != 0) _fpStatus |= FpC1;
        if (IsEmpty(p)) { _fpStatus |= FpC3 | FpC0; return; }
        var e = _fpExp[p] & 0x7FFF;
        var mant = _fpMant[p];
        if (e == 0x7FFF) _fpStatus |= (mant << 1) == 0 ? (ushort)(FpC2 | FpC0) : FpC0; // infinity : NaN
        else if (e == 0) _fpStatus |= mant == 0 ? FpC3 : (ushort)(FpC3 | FpC2);         // zero : denormal
        else if ((mant >> 63) == 0) { }                                                    // unnormal -> unsupported (000)
        else _fpStatus |= FpC2;                                                            // normal
    }
}
