using XboxPc.Emulation.Core;

namespace XboxPc.Emulation.X86;

/// <summary>
/// MMX and SSE (Pentium III level: SSE1 + the MMX integer extensions). SSE2+ encodings (66/F2-prefixed forms) are
/// reported as unimplemented because the Pentium III does not have them.
/// Exactness: add/sub/mul/div/sqrt/min/max/compare/conversions are IEEE-exact with round-to-nearest. MXCSR rounding
/// control is honoured by CVTSS2SI/CVTPS2PI only. RCPPS/RSQRTPS return correctly rounded values truncated to 12 mantissa
/// bits, inside Intel's documented error bound but not bit-identical to Pentium III tables. Flags tracked in MXCSR:
/// IE, ZE, OE (with PE), UE when FTZ flushes. DE and PE in general are not tracked; DAZ is not a Pentium III feature.
/// Unmasked SIMD exceptions (#XM) are not delivered: the instruction stops as unimplemented.
/// </summary>
public sealed partial class X86Cpu
{
    private readonly uint[] _xmm = new uint[32];

    public uint Mxcsr { get; set; } = 0x1F80;

    /// <summary>Machine-specific register model supplied by the machine. Null result = MSR not modelled (stops).</summary>
    public Func<uint, ulong?>? ReadMsr { get; set; }

    public uint Xmm(int reg, int lane) => _xmm[(reg * 4) + lane];

    public void SetXmm(int reg, int lane, uint value) => _xmm[(reg * 4) + lane] = value;

    /// <summary>MMX register i aliases the significand of x87 physical register i.</summary>
    public ulong Mmx(int reg) => _fpMant[reg];

    public void SetMmx(int reg, ulong value)
    {
        _fpMant[reg] = value;
        _fpExp[reg] = 0xFFFF;
        _fpTop = 0;
        _fpTags = 0; // all valid
    }

    private const uint MxIE = 0x01, MxZE = 0x04, MxOE = 0x08, MxUE = 0x10, MxPE = 0x20, MxFtz = 0x8000;
    private const uint DefaultNaN = 0xFFC0_0000; // "real indefinite"

    private void MxRaise(uint flags)
    {
        Mxcsr |= flags;
        if ((flags & 0x3F & ~(Mxcsr >> 7)) != 0) throw Unimpl("unmasked SIMD floating-point exception (#XM delivery is not modelled)");
    }

    private static bool IsNaN(uint f) => (f & 0x7F80_0000) == 0x7F80_0000 && (f & 0x007F_FFFF) != 0;
    private static bool IsSNaN(uint f) => IsNaN(f) && (f & 0x0040_0000) == 0;
    private static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);
    private static uint B(float f) => BitConverter.SingleToUInt32Bits(f);

    private uint FtzResult(uint r)
    {
        if ((Mxcsr & MxFtz) != 0 && (r & 0x7F80_0000) == 0 && (r & 0x007F_FFFF) != 0)
        {
            Mxcsr |= MxUE | MxPE;
            return r & 0x8000_0000;
        }
        return r;
    }

    /// <summary>op: 0x58 add, 0x59 mul, 0x5C sub, 0x5D min, 0x5E div, 0x5F max, 0x51 sqrt (b ignored).</summary>
    private uint SseScalar(byte op, uint a, uint b)
    {
        if (op is 0x5D or 0x5F)
        {
            // MIN/MAX: if either operand is NaN, or both are zero, the second (source) operand is returned.
            if (IsNaN(a) || IsNaN(b)) { MxRaise(MxIE); return b; }
            var fa = F(a);
            var fb = F(b);
            return op == 0x5D ? (fa < fb ? a : b) : (fa > fb ? a : b);
        }
        if (op == 0x51)
        {
            if (IsNaN(b)) { if (IsSNaN(b)) MxRaise(MxIE); return b | 0x0040_0000; }
            if ((b & 0x8000_0000) != 0 && (b & 0x7FFF_FFFF) != 0) { MxRaise(MxIE); return DefaultNaN; }
            return FtzResult(B(MathF.Sqrt(F(b))));
        }
        if (IsNaN(a) || IsNaN(b))
        {
            if (IsSNaN(a) || IsSNaN(b)) MxRaise(MxIE);
            return (IsNaN(a) ? a : b) | 0x0040_0000;
        }
        float x = F(a), y = F(b);
        if (op == 0x5E && y == 0 && x != 0 && !float.IsInfinity(x)) MxRaise(MxZE);
        var r = op switch { 0x58 => x + y, 0x59 => x * y, 0x5C => x - y, _ => x / y };
        if (float.IsNaN(r)) { MxRaise(MxIE); return DefaultNaN; }
        if (float.IsInfinity(r) && !float.IsInfinity(x) && !float.IsInfinity(y) && !(op == 0x5E && y == 0)) MxRaise(MxOE | MxPE);
        return FtzResult(B(r));
    }

    private static uint Approx(byte op, uint b)
    {
        if (IsNaN(b)) return b | 0x0040_0000;
        var v = F(b);
        var r = op == 0x53 ? 1.0f / v : 1.0f / MathF.Sqrt(v);
        var bits = B(r);
        if (float.IsNaN(r)) return DefaultNaN;
        if (float.IsInfinity(r) || r == 0) return bits;
        return (bits + 0x400) & 0xFFFF_F800; // round to 12 fraction bits: |relative error| ≤ 2^-13 < Intel bound 1.5·2^-12
    }

    private bool ComparePredicate(int predicate, uint a, uint b)
    {
        var unordered = IsNaN(a) || IsNaN(b);
        if (unordered && (IsSNaN(a) || IsSNaN(b) || predicate is 1 or 2 or 5 or 6)) MxRaise(MxIE);
        float x = F(a), y = F(b);
        return (predicate & 7) switch
        {
            0 => !unordered && x == y,
            1 => !unordered && x < y,
            2 => !unordered && x <= y,
            3 => unordered,
            4 => unordered || x != y,
            5 => unordered || !(x < y),
            6 => unordered || !(x <= y),
            _ => !unordered,
        };
    }

    private int SseRounding => (int)((Mxcsr >> 13) & 3);

    private uint CvtToInt32(uint f, bool truncate)
    {
        if (IsNaN(f)) { MxRaise(MxIE); return 0x8000_0000; }
        double v = F(f);
        var r = truncate ? Math.Truncate(v) : SseRounding switch
        {
            0 => Math.Round(v, MidpointRounding.ToEven),
            1 => Math.Floor(v),
            2 => Math.Ceiling(v),
            _ => Math.Truncate(v),
        };
        if (r < int.MinValue || r > int.MaxValue) { MxRaise(MxIE); return 0x8000_0000; }
        return (uint)(int)r;
    }

    private uint[] ReadXmmOperand(in ModRm m, bool aligned)
    {
        if (m.IsReg) return [_xmm[m.Rm * 4], _xmm[(m.Rm * 4) + 1], _xmm[(m.Rm * 4) + 2], _xmm[(m.Rm * 4) + 3]];
        var a = Linear(m);
        if (aligned && (a & 15) != 0) throw new X86Exception(X86Exception.GeneralProtection, _start, $"#GP: misaligned 16-byte SSE access at 0x{a:X8}");
        return [_bus.Read32(a), _bus.Read32(a + 4), _bus.Read32(a + 8), _bus.Read32(a + 12)];
    }

    private void WriteXmmMemory(uint a, int reg, bool aligned)
    {
        if (aligned && (a & 15) != 0) throw new X86Exception(X86Exception.GeneralProtection, _start, $"#GP: misaligned 16-byte SSE access at 0x{a:X8}");
        for (var j = 0; j < 4; j++) _bus.Write32(a + (uint)(j * 4), _xmm[(reg * 4) + j]);
    }

    private uint ReadScalarOperand(in ModRm m) => m.IsReg ? _xmm[m.Rm * 4] : _bus.Read32(Linear(m));

    private ulong ReadMmxOperand(in ModRm m) => m.IsReg ? Mmx(m.Rm) : _bus.Read32(Linear(m)) | ((ulong)_bus.Read32(Linear(m) + 4) << 32);

    private bool ExecuteSimd(byte op)
    {
        // Pentium III: no SSE2 (66 / F2 prefixed SIMD forms).
        var isSimdRange = op is >= 0x10 and <= 0x17 or >= 0x28 and <= 0x2F or >= 0x50 and <= 0x7F or 0xC2 or >= 0xC4 and <= 0xC6 or >= 0xD1;
        if (isSimdRange && (_o16 || _rep == 0xF2)) throw Unimpl($"SSE2 encoding 0x{(_o16 ? "66" : "F2")} 0F {op:X2} (not present on Pentium III)");
        var ss = _rep == 0xF3;

        switch (op)
        {
            case 0x18: { var m = Decode(); if (m.IsReg || m.Reg > 3) throw Ud("PREFETCH form"); return true; }
            case 0x32: // RDMSR
            {
                var msr = Regs[ECX];
                if (ReadMsr is null || ReadMsr(msr) is not { } value) throw Unimpl($"RDMSR 0x{msr:X} (this MSR is not modelled)");
                Regs[EAX] = (uint)value;
                Regs[EDX] = (uint)(value >> 32);
                return true;
            }
            case 0xAE:
            {
                var m = Decode();
                switch (m.Reg)
                {
                    case 2 when !m.IsReg:
                    {
                        var v = _bus.Read32(Linear(m));
                        if ((v & 0xFFFF_0000) != 0) throw new X86Exception(X86Exception.GeneralProtection, _start, "#GP: reserved MXCSR bits");
                        Mxcsr = v & ~0x40u; // DAZ (bit 6) does not exist on Pentium III
                        return true;
                    }
                    case 3 when !m.IsReg: _bus.Write32(Linear(m), Mxcsr); return true;
                    case 7 when m.IsReg: return true; // SFENCE: single in-order core, nothing to order
                    case 0 or 1 when !m.IsReg: throw Unimpl("FXSAVE/FXRSTOR");
                    default: throw Ud("0F AE form");
                }
            }
        }

        if (op is >= 0x60 and <= 0x7F or >= 0xD1 or 0xC4 or 0xC5)
        {
            if (op is 0x77) { _fpTags = 0xFFFF; return true; } // EMMS
            if (ss && op is 0x6F or 0x7F) throw Unimpl("MOVDQU (SSE2)");
            return ExecuteMmx(op);
        }

        if (op is not (>= 0x10 and <= 0x17 or >= 0x28 and <= 0x2F or >= 0x50 and <= 0x5F or 0xC2 or 0xC6)) return false;

        var mr = Decode();
        var d = mr.Reg;
        switch (op)
        {
            case 0x10: // MOVUPS / MOVSS load
                if (ss)
                {
                    if (mr.IsReg) _xmm[d * 4] = _xmm[mr.Rm * 4];
                    else { _xmm[d * 4] = _bus.Read32(Linear(mr)); _xmm[(d * 4) + 1] = _xmm[(d * 4) + 2] = _xmm[(d * 4) + 3] = 0; }
                }
                else SetXmmAll(d, ReadXmmOperand(mr, aligned: false));
                return true;
            case 0x11:
                if (ss) { if (mr.IsReg) _xmm[mr.Rm * 4] = _xmm[d * 4]; else _bus.Write32(Linear(mr), _xmm[d * 4]); }
                else if (mr.IsReg) Array.Copy(_xmm, d * 4, _xmm, mr.Rm * 4, 4);
                else WriteXmmMemory(Linear(mr), d, aligned: false);
                return true;
            case 0x12: case 0x16: // MOVLPS/MOVHLPS, MOVHPS/MOVLHPS (load)
            {
                var dst = op == 0x12 ? 0 : 2;
                if (mr.IsReg)
                {
                    var src = op == 0x12 ? 2 : 0;
                    _xmm[(d * 4) + dst] = _xmm[(mr.Rm * 4) + src];
                    _xmm[(d * 4) + dst + 1] = _xmm[(mr.Rm * 4) + src + 1];
                }
                else
                {
                    var a = Linear(mr);
                    _xmm[(d * 4) + dst] = _bus.Read32(a);
                    _xmm[(d * 4) + dst + 1] = _bus.Read32(a + 4);
                }
                return true;
            }
            case 0x13: case 0x17:
            {
                if (mr.IsReg) throw Ud("MOVLPS/MOVHPS store with register operand");
                var src = op == 0x13 ? 0 : 2;
                var a = Linear(mr);
                _bus.Write32(a, _xmm[(d * 4) + src]);
                _bus.Write32(a + 4, _xmm[(d * 4) + src + 1]);
                return true;
            }
            case 0x14: case 0x15: // UNPCKLPS / UNPCKHPS
            {
                var s = ReadXmmOperand(mr, aligned: true);
                var baseLane = op == 0x14 ? 0 : 2;
                uint a0 = _xmm[(d * 4) + baseLane], a1 = _xmm[(d * 4) + baseLane + 1];
                SetXmmAll(d, [a0, s[baseLane], a1, s[baseLane + 1]]);
                return true;
            }
            case 0x28: SetXmmAll(d, ReadXmmOperand(mr, aligned: true)); return true; // MOVAPS
            case 0x29: case 0x2B: // MOVAPS store, MOVNTPS
                if (mr.IsReg) { if (op == 0x2B) throw Ud("MOVNTPS register form"); Array.Copy(_xmm, d * 4, _xmm, mr.Rm * 4, 4); }
                else WriteXmmMemory(Linear(mr), d, aligned: true);
                return true;
            case 0x2A:
                if (ss) { _xmm[d * 4] = B((float)(int)GetRm(32, mr)); return true; } // CVTSI2SS (round-to-nearest)
                {
                    var v = ReadMmxOperand(mr); // CVTPI2PS
                    _xmm[d * 4] = B((float)(int)(uint)v);
                    _xmm[(d * 4) + 1] = B((float)(int)(uint)(v >> 32));
                    return true;
                }
            case 0x2C: case 0x2D:
            {
                var truncate = op == 0x2C;
                if (ss) { SetReg(32, d, CvtToInt32(ReadScalarOperand(mr), truncate)); return true; } // CVT(T)SS2SI
                uint lo, hi;
                if (mr.IsReg) { lo = _xmm[mr.Rm * 4]; hi = _xmm[(mr.Rm * 4) + 1]; }
                else { lo = _bus.Read32(Linear(mr)); hi = _bus.Read32(Linear(mr) + 4); }
                SetMmx(d, CvtToInt32(lo, truncate) | ((ulong)CvtToInt32(hi, truncate) << 32)); // CVT(T)PS2PI
                return true;
            }
            case 0x2E: case 0x2F: // UCOMISS / COMISS
            {
                uint a = _xmm[d * 4], b = ReadScalarOperand(mr);
                Eflags &= ~(ZF | PF | CF | OF | SF | AF);
                if (IsNaN(a) || IsNaN(b))
                {
                    Eflags |= ZF | PF | CF;
                    if (op == 0x2F || IsSNaN(a) || IsSNaN(b)) MxRaise(MxIE);
                }
                else if (F(a) < F(b)) Eflags |= CF;
                else if (F(a) == F(b)) Eflags |= ZF;
                return true;
            }
            case 0x50: // MOVMSKPS
                if (!mr.IsReg) throw Ud("MOVMSKPS memory form");
                Regs[d] = (_xmm[mr.Rm * 4] >> 31) | ((_xmm[(mr.Rm * 4) + 1] >> 31) << 1) | ((_xmm[(mr.Rm * 4) + 2] >> 31) << 2) | ((_xmm[(mr.Rm * 4) + 3] >> 31) << 3);
                return true;
            case 0x54: case 0x55: case 0x56: case 0x57:
            {
                var s = ReadXmmOperand(mr, aligned: true);
                for (var j = 0; j < 4; j++)
                {
                    var a = _xmm[(d * 4) + j];
                    _xmm[(d * 4) + j] = op switch { 0x54 => a & s[j], 0x55 => ~a & s[j], 0x56 => a | s[j], _ => a ^ s[j] };
                }
                return true;
            }
            case 0x51: case 0x52: case 0x53: case 0x58: case 0x59: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
            {
                if (ss)
                {
                    var b = ReadScalarOperand(mr);
                    _xmm[d * 4] = op is 0x52 or 0x53 ? Approx(op == 0x52 ? (byte)0x52 : (byte)0x53, b) : SseScalar(op, _xmm[d * 4], b);
                    return true;
                }
                var s = ReadXmmOperand(mr, aligned: true);
                for (var j = 0; j < 4; j++)
                    _xmm[(d * 4) + j] = op is 0x52 or 0x53 ? Approx(op, s[j]) : SseScalar(op, _xmm[(d * 4) + j], s[j]);
                return true;
            }
            case 0x5A: case 0x5B: throw Unimpl("SSE2 conversion");
            case 0xC2: // CMPPS / CMPSS
            {
                if (ss)
                {
                    var b = ReadScalarOperand(mr);
                    var pred = F8();
                    _xmm[d * 4] = ComparePredicate(pred, _xmm[d * 4], b) ? 0xFFFF_FFFFu : 0;
                    return true;
                }
                var s = ReadXmmOperand(mr, aligned: true);
                var predicate = F8();
                for (var j = 0; j < 4; j++) _xmm[(d * 4) + j] = ComparePredicate(predicate, _xmm[(d * 4) + j], s[j]) ? 0xFFFF_FFFFu : 0;
                return true;
            }
            case 0xC6: // SHUFPS
            {
                var s = ReadXmmOperand(mr, aligned: true);
                var imm = F8();
                uint[] a = [_xmm[d * 4], _xmm[(d * 4) + 1], _xmm[(d * 4) + 2], _xmm[(d * 4) + 3]];
                SetXmmAll(d, [a[imm & 3], a[(imm >> 2) & 3], s[(imm >> 4) & 3], s[(imm >> 6) & 3]]);
                return true;
            }
            default: return false;
        }
    }

    private void SetXmmAll(int reg, uint[] v) => Array.Copy(v, 0, _xmm, reg * 4, 4);

    // ------------------------------------------------------------------ MMX

    private static ulong Lanes(ulong a, ulong b, int bits, Func<long, long, long> f, bool signed)
    {
        ulong r = 0;
        var mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        for (var i = 0; i < 64; i += bits)
        {
            long x = (long)((a >> i) & mask), y = (long)((b >> i) & mask);
            if (signed && bits < 64) { x = (x << (64 - bits)) >> (64 - bits); y = (y << (64 - bits)) >> (64 - bits); }
            r |= ((ulong)f(x, y) & mask) << i;
        }
        return r;
    }

    private static long SatS(long v, int bits) => Math.Clamp(v, -(1L << (bits - 1)), (1L << (bits - 1)) - 1);
    private static long SatU(long v, int bits) => Math.Clamp(v, 0, (1L << bits) - 1);

    private static ulong Interleave(ulong a, ulong b, int bits, bool high)
    {
        var count = 32 / bits;
        var mask = (1UL << bits) - 1;
        var start = high ? 32 : 0;
        ulong r = 0;
        for (var i = 0; i < count; i++)
        {
            r |= ((a >> (start + (i * bits))) & mask) << (2 * i * bits);
            r |= ((b >> (start + (i * bits))) & mask) << (((2 * i) + 1) * bits);
        }
        return r;
    }

    private static ulong Pack(ulong a, ulong b, int srcBits, bool unsignedSat)
    {
        var dstBits = srcBits / 2;
        var count = 64 / srcBits;
        ulong r = 0;
        var dmask = (1UL << dstBits) - 1;
        for (var i = 0; i < count * 2; i++)
        {
            var src = i < count ? a : b;
            var lane = (long)((src >> ((i % count) * srcBits)) & ((1UL << srcBits) - 1));
            lane = (lane << (64 - srcBits)) >> (64 - srcBits);
            var v = unsignedSat ? SatU(lane, dstBits) : SatS(lane, dstBits);
            r |= ((ulong)v & dmask) << (i * dstBits);
        }
        return r;
    }

    private static ulong ShiftLanes(ulong v, int bits, ulong count, int kind) // kind: 2 logical right, 4 arithmetic right, 6 left
    {
        if (bits == 64)
        {
            if (count > 63) return 0;
            return kind == 6 ? v << (int)count : v >> (int)count;
        }
        if (count >= (ulong)bits)
        {
            if (kind != 4) return 0;
            count = (ulong)bits - 1;
        }
        var c = (int)count;
        return Lanes(v, 0, bits, (x, _) => kind switch { 2 => (long)((ulong)x >> c), 4 => x >> c, _ => x << c }, signed: kind == 4);
    }

    private bool ExecuteMmx(byte op)
    {
        var m = Decode();
        var d = m.Reg;
        ulong a = Mmx(d);
        switch (op)
        {
            case 0x6E: SetMmx(d, GetRm(32, m)); return true;                    // MOVD mm, r/m32
            case 0x7E: SetRm(32, m, (uint)a); return true;                      // MOVD r/m32, mm
            case 0x6F: SetMmx(d, ReadMmxOperand(m)); return true;               // MOVQ mm, mm/m64
            case 0x7F: case 0xE7:                                               // MOVQ / MOVNTQ store
                if (m.IsReg) { if (op == 0xE7) throw Ud("MOVNTQ register form"); SetMmx(m.Rm, a); }
                else { _bus.Write32(Linear(m), (uint)a); _bus.Write32(Linear(m) + 4, (uint)(a >> 32)); }
                return true;
            case 0x70: // PSHUFW
            {
                var s = ReadMmxOperand(m);
                var imm = F8();
                ulong r = 0;
                for (var i = 0; i < 4; i++) r |= ((s >> (((imm >> (i * 2)) & 3) * 16)) & 0xFFFF) << (i * 16);
                SetMmx(d, r);
                return true;
            }
            case 0x71: case 0x72: case 0x73: // shift by immediate groups
            {
                if (!m.IsReg) throw Ud("MMX shift-immediate with memory operand");
                var imm = F8();
                var bits = op switch { 0x71 => 16, 0x72 => 32, _ => 64 };
                if (m.Reg is not (2 or 4 or 6) || (bits == 64 && m.Reg == 4)) throw Ud("MMX shift group form");
                SetMmx(m.Rm, ShiftLanes(Mmx(m.Rm), bits, imm, m.Reg));
                return true;
            }
            case 0xC4: // PINSRW mm, r32/m16, imm8
            {
                var v = m.IsReg ? Regs[m.Rm] & 0xFFFF : _bus.Read16(Linear(m));
                var sel = (F8() & 3) * 16;
                SetMmx(d, (a & ~(0xFFFFUL << sel)) | ((ulong)v << sel));
                return true;
            }
            case 0xC5: // PEXTRW r32, mm, imm8
                if (!m.IsReg) throw Ud("PEXTRW memory form");
                Regs[d] = (uint)((Mmx(m.Rm) >> ((F8() & 3) * 16)) & 0xFFFF);
                return true;
            case 0xD7: // PMOVMSKB
            {
                if (!m.IsReg) throw Ud("PMOVMSKB memory form");
                var s = Mmx(m.Rm);
                uint r = 0;
                for (var i = 0; i < 8; i++) r |= (uint)((s >> ((i * 8) + 7)) & 1) << i;
                Regs[d] = r;
                return true;
            }
            case 0xF7: // MASKMOVQ: byte-masked store to [EDI]
            {
                if (!m.IsReg) throw Ud("MASKMOVQ memory form");
                var mask = Mmx(m.Rm);
                for (var i = 0; i < 8; i++)
                    if (((mask >> ((i * 8) + 7)) & 1) != 0) _bus.Write8(Regs[EDI] + _seg + (uint)i, (byte)(a >> (i * 8)));
                return true;
            }
        }

        var b = ReadMmxOperand(m);
        ulong result = op switch
        {
            0x60 => Interleave(a, b, 8, false), 0x61 => Interleave(a, b, 16, false), 0x62 => Interleave(a, b, 32, false),
            0x68 => Interleave(a, b, 8, true), 0x69 => Interleave(a, b, 16, true), 0x6A => Interleave(a, b, 32, true),
            0x63 => Pack(a, b, 16, false), 0x67 => Pack(a, b, 16, true), 0x6B => Pack(a, b, 32, false),
            0x64 => Lanes(a, b, 8, (x, y) => x > y ? -1 : 0, true), 0x65 => Lanes(a, b, 16, (x, y) => x > y ? -1 : 0, true), 0x66 => Lanes(a, b, 32, (x, y) => x > y ? -1 : 0, true),
            0x74 => Lanes(a, b, 8, (x, y) => x == y ? -1 : 0, false), 0x75 => Lanes(a, b, 16, (x, y) => x == y ? -1 : 0, false), 0x76 => Lanes(a, b, 32, (x, y) => x == y ? -1 : 0, false),
            0xD1 => ShiftLanes(a, 16, b, 2), 0xD2 => ShiftLanes(a, 32, b, 2), 0xD3 => ShiftLanes(a, 64, b, 2),
            0xE1 => ShiftLanes(a, 16, b, 4), 0xE2 => ShiftLanes(a, 32, b, 4),
            0xF1 => ShiftLanes(a, 16, b, 6), 0xF2 => ShiftLanes(a, 32, b, 6), 0xF3 => ShiftLanes(a, 64, b, 6),
            0xD5 => Lanes(a, b, 16, (x, y) => x * y, true),
            0xE5 => Lanes(a, b, 16, (x, y) => (x * y) >> 16, true),
            0xE4 => Lanes(a, b, 16, (x, y) => (x * y) >> 16, false),
            0xF5 => PmaddWd(a, b),
            0xD8 => Lanes(a, b, 8, (x, y) => SatU(x - y, 8), false), 0xD9 => Lanes(a, b, 16, (x, y) => SatU(x - y, 16), false),
            0xDC => Lanes(a, b, 8, (x, y) => SatU(x + y, 8), false), 0xDD => Lanes(a, b, 16, (x, y) => SatU(x + y, 16), false),
            0xE8 => Lanes(a, b, 8, (x, y) => SatS(x - y, 8), true), 0xE9 => Lanes(a, b, 16, (x, y) => SatS(x - y, 16), true),
            0xEC => Lanes(a, b, 8, (x, y) => SatS(x + y, 8), true), 0xED => Lanes(a, b, 16, (x, y) => SatS(x + y, 16), true),
            0xF8 => Lanes(a, b, 8, (x, y) => x - y, false), 0xF9 => Lanes(a, b, 16, (x, y) => x - y, false), 0xFA => Lanes(a, b, 32, (x, y) => x - y, false),
            0xFC => Lanes(a, b, 8, (x, y) => x + y, false), 0xFD => Lanes(a, b, 16, (x, y) => x + y, false), 0xFE => Lanes(a, b, 32, (x, y) => x + y, false),
            0xDB => a & b, 0xDF => ~a & b, 0xEB => a | b, 0xEF => a ^ b,
            0xDA => Lanes(a, b, 8, Math.Min, false), 0xDE => Lanes(a, b, 8, Math.Max, false),
            0xEA => Lanes(a, b, 16, Math.Min, true), 0xEE => Lanes(a, b, 16, Math.Max, true),
            0xE0 => Lanes(a, b, 8, (x, y) => (x + y + 1) >> 1, false), 0xE3 => Lanes(a, b, 16, (x, y) => (x + y + 1) >> 1, false),
            0xF6 => Psadbw(a, b),
            _ => throw Unimpl($"MMX/SSE opcode 0x0F 0x{op:X2}" + (op is 0xD4 or 0xD6 or 0xE6 or 0xF4 or 0xFB or 0x7C or 0x7D or 0x6C or 0x6D ? " (SSE2/SSE3, not on Pentium III)" : "")),
        };
        SetMmx(d, result);
        return true;
    }

    private static ulong PmaddWd(ulong a, ulong b)
    {
        ulong r = 0;
        for (var i = 0; i < 2; i++)
        {
            long sum = 0;
            for (var k = 0; k < 2; k++)
            {
                var shift = ((i * 2) + k) * 16;
                sum += (long)(short)(a >> shift) * (short)(b >> shift);
            }
            r |= (ulong)(uint)(int)sum << (i * 32);
        }
        return r;
    }

    private static ulong Psadbw(ulong a, ulong b)
    {
        ulong sum = 0;
        for (var i = 0; i < 64; i += 8) sum += (ulong)Math.Abs((int)((a >> i) & 0xFF) - (int)((b >> i) & 0xFF));
        return sum;
    }
}
