using XboxPc.Emulation.Core;

namespace XboxPc.Emulation.X86;

/// <summary>Architectural exception raised by the guest (#DE, #UD, #PF ...). Faults leave EIP at the faulting instruction.</summary>
public sealed class X86Exception : Exception
{
    public X86Exception(int vector, uint eip, string message, bool isTrap = false, Exception? inner = null) : base(message, inner)
    {
        Vector = vector;
        Eip = eip;
        IsTrap = isTrap;
    }

    public X86Exception() : this(6, 0, "#UD") { }

    public X86Exception(string message) : this(6, 0, message) { }

    public X86Exception(string message, Exception inner) : this(6, 0, message, false, inner) { }

    public const int DivideError = 0, Breakpoint = 3, InvalidOpcode = 6, GeneralProtection = 13, PageFault = 14;

    public int Vector { get; }

    /// <summary>EIP of the faulting instruction (faults) or of the next instruction (traps: INT3, INT n).</summary>
    public uint Eip { get; }

    public bool IsTrap { get; }

    /// <summary>Set when the exception was caused by a bus fault (unmapped / unemulated memory).</summary>
    public BusFaultException? BusFault => InnerException as BusFaultException;
}

/// <summary>A valid IA-32 instruction this interpreter does not implement yet. Distinct from #UD so gaps are never hidden.</summary>
public sealed class UnimplementedInstructionException : Exception
{
    public UnimplementedInstructionException(uint eip, string what) : base($"Unimplemented instruction at 0x{eip:X8}: {what}")
    {
        Eip = eip;
        What = what;
    }

    public UnimplementedInstructionException() : this(0, "unknown") { }

    public UnimplementedInstructionException(string message) : base(message) { What = message; }

    public UnimplementedInstructionException(string message, Exception inner) : base(message, inner) { What = message; }

    public uint Eip { get; }
    public string What { get; } = "";
}

public enum StopReason
{
    BudgetExhausted,
    Halted,
    Exception,
    Unimplemented,
}

public sealed record RunResult(StopReason Reason, long Executed, Exception? Error);

/// <summary>
/// IA-32 integer interpreter modelling the Original Xbox CPU (Intel Pentium III) in flat 32-bit protected mode.
/// Implemented: integer ALU with exact flags, ModR/M+SIB addressing, operand-size prefix, FS/GS bases, string ops with
/// REP, stack ops, control flow, MUL/DIV, shifts/rotates, bit ops, SHLD/SHRD, CMPXCHG/XADD/CMPXCHG8B, CPUID, RDTSC, port I/O.
/// Not implemented (stops with <see cref="UnimplementedInstructionException"/>): x87, MMX, SSE, segment loads, far
/// control transfer, IDT delivery, paging. See docs/engine/04_M1_REPORT.md.
/// </summary>
public sealed partial class X86Cpu(PhysicalBus bus, IoPortBus? io = null)
{
    public const uint CF = 0x001, PF = 0x004, AF = 0x010, ZF = 0x040, SF = 0x080, TF = 0x100, IF = 0x200, DF = 0x400, OF = 0x800;
    public const uint ArithmeticFlags = CF | PF | AF | ZF | SF | OF;
    public const int EAX = 0, ECX = 1, EDX = 2, EBX = 3, ESP = 4, EBP = 5, ESI = 6, EDI = 7;

    private static readonly bool[] ParityEven = Enumerable.Range(0, 256).Select(i => System.Numerics.BitOperations.PopCount((uint)i) % 2 == 0).ToArray();

    private readonly PhysicalBus _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    private readonly IoPortBus _io = io ?? new IoPortBus();

    public uint[] Regs { get; } = new uint[8];
    public uint Eip { get; set; }
    public uint Eflags { get; set; } = 0x2;
    public uint FsBase { get; set; }
    public uint GsBase { get; set; }

    /// <summary>Retired instruction count. Also used as the time-stamp counter (1 tick per instruction, not cycle accurate).</summary>
    public long Retired { get; private set; }

    public bool Halted { get; set; }

    // Per-instruction decode state.
    private uint _start;
    private bool _o16;
    private uint _seg;
    private byte _rep;

    public uint this[int reg] { get => Regs[reg]; set => Regs[reg] = value; }

    public bool GetFlag(uint flag) => (Eflags & flag) != 0;

    private void SetFlag(uint flag, bool value)
    {
        if (value) Eflags |= flag;
        else Eflags &= ~flag;
    }

    public RunResult Run(long maxInstructions)
    {
        long executed = 0;
        while (executed < maxInstructions)
        {
            if (Halted) return new RunResult(StopReason.Halted, executed, null);
            try { Step(); }
            catch (X86Exception e) { return new RunResult(StopReason.Exception, executed, e); }
            catch (UnimplementedInstructionException e) { return new RunResult(StopReason.Unimplemented, executed, e); }
            executed++;
        }
        return new RunResult(Halted ? StopReason.Halted : StopReason.BudgetExhausted, executed, null);
    }

    /// <summary>Executes one instruction. On a fault, architectural EIP is restored to the faulting instruction.</summary>
    public void Step()
    {
        _start = Eip;
        _o16 = false;
        _seg = 0;
        _rep = 0;
        try
        {
            Execute();
            Retired++;
        }
        catch (BusFaultException e)
        {
            Eip = _start;
            throw new X86Exception(X86Exception.PageFault, _start, $"#PF at EIP 0x{_start:X8}: {e.Message}", false, e);
        }
        catch (X86Exception e) when (!e.IsTrap)
        {
            Eip = _start;
            throw;
        }
        catch (X86Exception)
        {
            Retired++; // traps retire the instruction
            throw;
        }
        catch (UnimplementedInstructionException)
        {
            Eip = _start;
            throw;
        }
    }

    // ---------------------------------------------------------------- fetch / operands

    private byte F8() { var b = _bus.Read8(Eip, AccessKind.Fetch); Eip++; return b; }
    private ushort F16() { var v = _bus.Read16(Eip, AccessKind.Fetch); Eip += 2; return v; }
    private uint F32() { var v = _bus.Read32(Eip, AccessKind.Fetch); Eip += 4; return v; }

    private int OS => _o16 ? 16 : 32;

    private static uint Mask(int size) => size == 32 ? 0xFFFF_FFFFu : (1u << size) - 1;
    private static uint Sign(int size) => 1u << (size - 1);

    private static int SignExtend(uint v, int size) => size switch { 8 => (sbyte)v, 16 => (short)v, _ => (int)v };

    private uint Imm(int size) => size switch { 8 => F8(), 16 => F16(), _ => F32() };

    private uint ImmSx8(int size) => (uint)(sbyte)F8() & Mask(size);

    private struct ModRm
    {
        public int Mod, Reg, Rm;
        public bool IsReg;
        public uint Offset;
    }

    private ModRm Decode()
    {
        var b = F8();
        var m = new ModRm { Mod = b >> 6, Reg = (b >> 3) & 7, Rm = b & 7 };
        if (m.Mod == 3) { m.IsReg = true; return m; }
        uint addr;
        if (m.Rm == 4)
        {
            var sib = F8();
            int scale = sib >> 6, index = (sib >> 3) & 7, baseReg = sib & 7;
            var baseVal = baseReg == 5 && m.Mod == 0 ? F32() : Regs[baseReg];
            var indexVal = index == 4 ? 0 : Regs[index] << scale;
            addr = baseVal + indexVal;
        }
        else if (m.Rm == 5 && m.Mod == 0) addr = F32();
        else addr = Regs[m.Rm];
        if (m.Mod == 1) addr += (uint)(sbyte)F8();
        else if (m.Mod == 2) addr += F32();
        m.Offset = addr;
        return m;
    }

    private uint Linear(in ModRm m) => m.Offset + _seg;

    public uint GetReg(int size, int r) => size switch
    {
        8 => r < 4 ? Regs[r] & 0xFF : (Regs[r - 4] >> 8) & 0xFF,
        16 => Regs[r] & 0xFFFF,
        _ => Regs[r],
    };

    public void SetReg(int size, int r, uint v)
    {
        switch (size)
        {
            case 8:
                if (r < 4) Regs[r] = (Regs[r] & ~0xFFu) | (v & 0xFF);
                else Regs[r - 4] = (Regs[r - 4] & ~0xFF00u) | ((v & 0xFF) << 8);
                break;
            case 16: Regs[r] = (Regs[r] & 0xFFFF_0000u) | (v & 0xFFFF); break;
            default: Regs[r] = v; break;
        }
    }

    private uint ReadMem(int size, uint a) => size switch { 8 => _bus.Read8(a), 16 => _bus.Read16(a), _ => _bus.Read32(a) };

    private void WriteMem(int size, uint a, uint v)
    {
        switch (size)
        {
            case 8: _bus.Write8(a, (byte)v); break;
            case 16: _bus.Write16(a, (ushort)v); break;
            default: _bus.Write32(a, v); break;
        }
    }

    private uint GetRm(int size, in ModRm m) => m.IsReg ? GetReg(size, m.Rm) : ReadMem(size, Linear(m));

    private void SetRm(int size, in ModRm m, uint v)
    {
        if (m.IsReg) SetReg(size, m.Rm, v);
        else WriteMem(size, Linear(m), v);
    }

    private void Push(int size, uint v)
    {
        var sp = Regs[ESP] - (uint)(size / 8);
        WriteMem(size, sp, v); // write first: a faulting push leaves ESP unchanged
        Regs[ESP] = sp;
    }

    private uint Pop(int size)
    {
        var v = ReadMem(size, Regs[ESP]);
        Regs[ESP] += (uint)(size / 8);
        return v;
    }

    // ---------------------------------------------------------------- flags / ALU

    private void SetSzp(int size, uint r)
    {
        r &= Mask(size);
        SetFlag(ZF, r == 0);
        SetFlag(SF, (r & Sign(size)) != 0);
        SetFlag(PF, ParityEven[r & 0xFF]);
    }

    /// <summary>op: 0 ADD, 1 OR, 2 ADC, 3 SBB, 4 AND, 5 SUB, 6 XOR, 7 CMP.</summary>
    private uint Alu(int op, int size, uint a, uint b)
    {
        uint mask = Mask(size), sign = Sign(size);
        a &= mask;
        b &= mask;
        uint r;
        switch (op)
        {
            case 0:
            case 2:
            {
                var c = op == 2 && GetFlag(CF) ? 1u : 0u;
                var full = (ulong)a + b + c;
                r = (uint)full & mask;
                SetFlag(CF, full > mask);
                SetFlag(OF, ((a ^ r) & (b ^ r) & sign) != 0);
                SetFlag(AF, ((a ^ b ^ r) & 0x10) != 0);
                break;
            }
            case 3:
            case 5:
            case 7:
            {
                var c = op == 3 && GetFlag(CF) ? 1u : 0u;
                r = (a - b - c) & mask;
                SetFlag(CF, (ulong)a < (ulong)b + c);
                SetFlag(OF, ((a ^ b) & (a ^ r) & sign) != 0);
                SetFlag(AF, ((a ^ b ^ r) & 0x10) != 0);
                break;
            }
            default:
                r = op switch { 1 => a | b, 4 => a & b, _ => a ^ b };
                SetFlag(CF, false);
                SetFlag(OF, false);
                SetFlag(AF, false); // architecturally undefined
                break;
        }
        SetSzp(size, r);
        return r;
    }

    private uint IncDec(int size, uint v, bool dec)
    {
        var cf = GetFlag(CF);
        var r = Alu(dec ? 5 : 0, size, v, 1);
        SetFlag(CF, cf);
        return r;
    }

    /// <summary>Group 2: 0 ROL, 1 ROR, 2 RCL, 3 RCR, 4 SHL, 5 SHR, 6 SAL, 7 SAR.</summary>
    private uint Shift(int op, int size, uint v, uint count)
    {
        uint mask = Mask(size), sign = Sign(size);
        v &= mask;
        count &= 0x1F;
        if (count == 0) return v;
        uint r;
        switch (op)
        {
            case 0:
            {
                var c = (int)(count % (uint)size);
                r = c == 0 ? v : ((v << c) | (v >> (size - c))) & mask;
                SetFlag(CF, (r & 1) != 0);
                SetFlag(OF, ((r & sign) != 0) ^ GetFlag(CF));
                return r;
            }
            case 1:
            {
                var c = (int)(count % (uint)size);
                r = c == 0 ? v : ((v >> c) | (v << (size - c))) & mask;
                SetFlag(CF, (r & sign) != 0);
                SetFlag(OF, ((r & sign) != 0) ^ ((r & (sign >> 1)) != 0));
                return r;
            }
            case 2:
            case 3:
            {
                var n = size switch { 8 => count % 9, 16 => count % 17, _ => count };
                var cf = GetFlag(CF);
                r = v;
                if (op == 3) SetFlag(OF, ((r & sign) != 0) ^ cf);
                for (var i = 0; i < n; i++)
                {
                    if (op == 2)
                    {
                        var msb = (r & sign) != 0;
                        r = ((r << 1) | (cf ? 1u : 0u)) & mask;
                        cf = msb;
                    }
                    else
                    {
                        var lsb = (r & 1) != 0;
                        r = (r >> 1) | (cf ? sign : 0);
                        cf = lsb;
                    }
                }
                SetFlag(CF, cf);
                if (op == 2) SetFlag(OF, ((r & sign) != 0) ^ cf);
                return r;
            }
            case 4:
            case 6:
                r = count < 32 ? (uint)(((ulong)v << (int)count) & mask) : 0;
                SetFlag(CF, count <= size && ((v >> (size - (int)count)) & 1) != 0);
                SetFlag(OF, ((r & sign) != 0) ^ GetFlag(CF));
                break;
            case 5:
                r = count < size ? v >> (int)count : 0;
                SetFlag(CF, count <= size && ((v >> ((int)count - 1)) & 1) != 0);
                SetFlag(OF, (v & sign) != 0);
                break;
            default:
            {
                long sv = SignExtend(v, size);
                r = (uint)(sv >> (int)Math.Min(count, 63u)) & mask;
                SetFlag(CF, ((sv >> ((int)Math.Min(count, 63u) - 1)) & 1) != 0);
                SetFlag(OF, false);
                break;
            }
        }
        SetFlag(AF, false); // architecturally undefined
        SetSzp(size, r);
        return r;
    }

    private bool Cond(int cc)
    {
        var r = (cc >> 1) switch
        {
            0 => GetFlag(OF),
            1 => GetFlag(CF),
            2 => GetFlag(ZF),
            3 => GetFlag(CF) || GetFlag(ZF),
            4 => GetFlag(SF),
            5 => GetFlag(PF),
            6 => GetFlag(SF) != GetFlag(OF),
            _ => GetFlag(ZF) || GetFlag(SF) != GetFlag(OF),
        };
        return (cc & 1) == 0 ? r : !r;
    }

    private X86Exception Ud(string what) => new(X86Exception.InvalidOpcode, _start, $"#UD at 0x{_start:X8}: {what}");

    private UnimplementedInstructionException Unimpl(string what) => new(_start, what);

    // ---------------------------------------------------------------- execution

    private void Execute()
    {
        byte op;
        for (var prefixes = 0; ; prefixes++)
        {
            if (prefixes > 14) throw Ud("instruction longer than 15 bytes");
            op = F8();
            switch (op)
            {
                case 0x66: _o16 = true; continue;
                case 0x67: throw Unimpl("address-size override (16-bit addressing)");
                case 0xF2: case 0xF3: _rep = op; continue;
                case 0xF0: continue; // LOCK: single-threaded interpreter, all accesses are atomic
                case 0x26: case 0x2E: case 0x36: case 0x3E: _seg = 0; continue; // flat ES/CS/SS/DS
                case 0x64: _seg = FsBase; continue;
                case 0x65: _seg = GsBase; continue;
            }
            break;
        }

        // 00-3F ALU block (excluding prefixes/BCD/segment ops in the x6/x7/xE/xF columns)
        if (op < 0x40 && (op & 7) < 6)
        {
            var aluOp = op >> 3;
            var size = (op & 1) == 0 ? 8 : OS;
            switch (op & 7)
            {
                case 0:
                case 1:
                {
                    var m = Decode();
                    var r = Alu(aluOp, size, GetRm(size, m), GetReg(size, m.Reg));
                    if (aluOp != 7) SetRm(size, m, r);
                    return;
                }
                case 2:
                case 3:
                {
                    var m = Decode();
                    var r = Alu(aluOp, size, GetReg(size, m.Reg), GetRm(size, m));
                    if (aluOp != 7) SetReg(size, m.Reg, r);
                    return;
                }
                default:
                {
                    var r = Alu(aluOp, size, GetReg(size, EAX), Imm(size));
                    if (aluOp != 7) SetReg(size, EAX, r);
                    return;
                }
            }
        }

        switch (op)
        {
            case >= 0x40 and <= 0x47: SetReg(OS, op - 0x40, IncDec(OS, GetReg(OS, op - 0x40), false)); return;
            case >= 0x48 and <= 0x4F: SetReg(OS, op - 0x48, IncDec(OS, GetReg(OS, op - 0x48), true)); return;
            case >= 0x50 and <= 0x57: Push(OS, GetReg(OS, op - 0x50)); return;
            case >= 0x58 and <= 0x5F: SetReg(OS, op - 0x58, Pop(OS)); return;
            case 0x60:
            {
                var sp = Regs[ESP];
                for (var r = 0; r < 8; r++) Push(OS, r == ESP ? sp : GetReg(OS, r));
                return;
            }
            case 0x61:
                for (var r = 7; r >= 0; r--)
                {
                    var v = Pop(OS);
                    if (r != ESP) SetReg(OS, r, v);
                }
                return;
            case 0x68: Push(OS, Imm(OS)); return;
            case 0x6A: Push(OS, ImmSx8(OS)); return;
            case 0x69:
            case 0x6B:
            {
                var m = Decode();
                var a = GetRm(OS, m);
                var b = op == 0x69 ? Imm(OS) : ImmSx8(OS);
                SetReg(OS, m.Reg, ImulTrunc(OS, a, b));
                return;
            }
            case >= 0x70 and <= 0x7F:
            {
                var rel = (sbyte)F8();
                if (Cond(op - 0x70)) Eip += (uint)rel;
                return;
            }
            case 0x80:
            case 0x81:
            case 0x83:
            {
                var size = op == 0x80 ? 8 : OS;
                var m = Decode();
                var a = GetRm(size, m);
                var b = op == 0x81 ? Imm(size) : op == 0x83 ? ImmSx8(size) : Imm(8);
                var r = Alu(m.Reg, size, a, b);
                if (m.Reg != 7) SetRm(size, m, r);
                return;
            }
            case 0x84:
            case 0x85:
            {
                var size = op == 0x84 ? 8 : OS;
                var m = Decode();
                Alu(4, size, GetRm(size, m), GetReg(size, m.Reg));
                return;
            }
            case 0x86:
            case 0x87:
            {
                var size = op == 0x86 ? 8 : OS;
                var m = Decode();
                var a = GetRm(size, m);
                SetRm(size, m, GetReg(size, m.Reg));
                SetReg(size, m.Reg, a);
                return;
            }
            case 0x88: { var m = Decode(); SetRm(8, m, GetReg(8, m.Reg)); return; }
            case 0x89: { var m = Decode(); SetRm(OS, m, GetReg(OS, m.Reg)); return; }
            case 0x8A: { var m = Decode(); SetReg(8, m.Reg, GetRm(8, m)); return; }
            case 0x8B: { var m = Decode(); SetReg(OS, m.Reg, GetRm(OS, m)); return; }
            case 0x8D:
            {
                var m = Decode();
                if (m.IsReg) throw Ud("LEA with register operand");
                SetReg(OS, m.Reg, m.Offset);
                return;
            }
            case 0x8F:
            {
                var m = Decode();
                if (m.Reg != 0) throw Ud("8F /r with r != 0");
                var v = Pop(OS);
                SetRm(OS, m, v);
                return;
            }
            case 0x90: return; // NOP / PAUSE
            case >= 0x91 and <= 0x97:
            {
                var r = op - 0x90;
                var t = GetReg(OS, EAX);
                SetReg(OS, EAX, GetReg(OS, r));
                SetReg(OS, r, t);
                return;
            }
            case 0x98:
                if (_o16) SetReg(16, EAX, (uint)(sbyte)(Regs[EAX] & 0xFF));
                else Regs[EAX] = (uint)(short)(Regs[EAX] & 0xFFFF);
                return;
            case 0x99:
                if (_o16) SetReg(16, EDX, (Regs[EAX] & 0x8000) != 0 ? 0xFFFFu : 0);
                else Regs[EDX] = (Regs[EAX] & 0x8000_0000) != 0 ? 0xFFFF_FFFFu : 0;
                return;
            case 0x9C: Push(OS, Eflags & Mask(OS)); return;
            case 0x9D:
            {
                var v = Pop(OS);
                const uint writable = ArithmeticFlags | TF | IF | DF;
                Eflags = (Eflags & ~writable & ~Mask(OS)) | (v & writable & Mask(OS)) | 0x2;
                return;
            }
            case 0x9E: Eflags = (Eflags & ~(SF | ZF | AF | PF | CF)) | ((Regs[EAX] >> 8) & (SF | ZF | AF | PF | CF)) | 0x2; return;
            case 0x9F: SetReg(8, 4, (Eflags & 0xFF) | 0x2); return; // AH
            case 0xA0: SetReg(8, EAX, ReadMem(8, F32() + _seg)); return;
            case 0xA1: SetReg(OS, EAX, ReadMem(OS, F32() + _seg)); return;
            case 0xA2: WriteMem(8, F32() + _seg, GetReg(8, EAX)); return;
            case 0xA3: WriteMem(OS, F32() + _seg, GetReg(OS, EAX)); return;
            case 0xA4: case 0xA5: case 0xA6: case 0xA7: case 0xAA: case 0xAB: case 0xAC: case 0xAD: case 0xAE: case 0xAF:
                StringOp(op);
                return;
            case 0xA8: Alu(4, 8, GetReg(8, EAX), Imm(8)); return;
            case 0xA9: Alu(4, OS, GetReg(OS, EAX), Imm(OS)); return;
            case >= 0xB0 and <= 0xB7: SetReg(8, op - 0xB0, Imm(8)); return;
            case >= 0xB8 and <= 0xBF: SetReg(OS, op - 0xB8, Imm(OS)); return;
            case 0xC0:
            case 0xC1:
            case 0xD0:
            case 0xD1:
            case 0xD2:
            case 0xD3:
            {
                var size = (op & 1) == 0 ? 8 : OS;
                var m = Decode();
                var v = GetRm(size, m);
                var count = op is 0xC0 or 0xC1 ? F8() : op is 0xD0 or 0xD1 ? 1u : Regs[ECX] & 0xFF;
                SetRm(size, m, Shift(m.Reg, size, v, count));
                return;
            }
            case 0xC2: { var n = F16(); Eip = Pop(32); Regs[ESP] += n; return; }
            case 0xC3: Eip = Pop(32); return;
            case 0xC6:
            case 0xC7:
            {
                var size = op == 0xC6 ? 8 : OS;
                var m = Decode();
                if (m.Reg != 0) throw Ud("C6/C7 with r != 0");
                SetRm(size, m, Imm(size));
                return;
            }
            case 0xC9: Regs[ESP] = Regs[EBP]; SetReg(OS, EBP, Pop(OS)); return;
            case 0xCC: throw new X86Exception(X86Exception.Breakpoint, Eip, $"INT3 at 0x{_start:X8}", isTrap: true);
            case 0xCD:
            {
                var vector = F8();
                throw new X86Exception(vector, Eip, $"INT 0x{vector:X2} at 0x{_start:X8} (no IDT delivery yet)", isTrap: true);
            }
            case 0xE4: SetReg(8, EAX, _io.In(F8(), 1)); return;
            case 0xE5: SetReg(OS, EAX, _io.In(F8(), OS / 8)); return;
            case 0xE6: _io.Out(F8(), 1, GetReg(8, EAX)); return;
            case 0xE7: _io.Out(F8(), OS / 8, GetReg(OS, EAX)); return;
            case 0xEC: SetReg(8, EAX, _io.In((ushort)Regs[EDX], 1)); return;
            case 0xED: SetReg(OS, EAX, _io.In((ushort)Regs[EDX], OS / 8)); return;
            case 0xEE: _io.Out((ushort)Regs[EDX], 1, GetReg(8, EAX)); return;
            case 0xEF: _io.Out((ushort)Regs[EDX], OS / 8, GetReg(OS, EAX)); return;
            case 0xE8: { var rel = Imm(OS); Push(32, Eip); Eip += _o16 ? (uint)(short)rel : rel; return; }
            case 0xE9: { var rel = Imm(OS); Eip += _o16 ? (uint)(short)rel : rel; return; }
            case 0xEB: { var rel = (sbyte)F8(); Eip += (uint)rel; return; }
            case 0xF4: Halted = true; return;
            case 0xF5: SetFlag(CF, !GetFlag(CF)); return;
            case 0xF6: case 0xF7: Group3(op == 0xF6 ? 8 : OS); return;
            case 0xF8: SetFlag(CF, false); return;
            case 0xF9: SetFlag(CF, true); return;
            case 0xFA: SetFlag(IF, false); return;
            case 0xFB: SetFlag(IF, true); return;
            case 0xFC: SetFlag(DF, false); return;
            case 0xFD: SetFlag(DF, true); return;
            case 0xFE:
            {
                var m = Decode();
                if (m.Reg > 1) throw Ud("FE /r with r > 1");
                SetRm(8, m, IncDec(8, GetRm(8, m), m.Reg == 1));
                return;
            }
            case 0xFF: Group5(); return;
            case 0x0F: Execute0F(); return;
            case >= 0xD8 and <= 0xDF: ExecuteFpu(op); return;
            case 0x9B: FpuWait(); return; // FWAIT
            default: throw Unimpl($"opcode 0x{op:X2}");
        }
    }

    private uint ImulTrunc(int size, uint a, uint b)
    {
        long full = (long)SignExtend(a, size) * SignExtend(b, size);
        var r = (uint)full & Mask(size);
        var overflow = full != SignExtend(r, size);
        SetFlag(CF, overflow);
        SetFlag(OF, overflow);
        SetSzp(size, r);
        return r;
    }

    private void Group3(int size)
    {
        var m = Decode();
        var v = GetRm(size, m);
        switch (m.Reg)
        {
            case 0:
            case 1: Alu(4, size, v, Imm(size)); return;
            case 2: SetRm(size, m, ~v & Mask(size)); return;
            case 3:
            {
                var r = Alu(5, size, 0, v);
                SetFlag(CF, (v & Mask(size)) != 0);
                SetRm(size, m, r);
                return;
            }
            case 4:
            {
                var full = (ulong)GetReg(size, EAX) * v;
                StoreWide(size, full);
                var high = full >> size;
                SetFlag(CF, high != 0);
                SetFlag(OF, high != 0);
                SetSzp(size, (uint)full);
                return;
            }
            case 5:
            {
                var full = (long)SignExtend(GetReg(size, EAX), size) * SignExtend(v, size);
                StoreWide(size, (ulong)full);
                var overflow = full != SignExtend((uint)full & Mask(size), size);
                SetFlag(CF, overflow);
                SetFlag(OF, overflow);
                SetSzp(size, (uint)full);
                return;
            }
            case 6:
            {
                if ((v & Mask(size)) == 0) throw new X86Exception(X86Exception.DivideError, _start, $"#DE (divide by zero) at 0x{_start:X8}");
                var dividend = LoadWide(size);
                var q = dividend / v;
                if (q > Mask(size)) throw new X86Exception(X86Exception.DivideError, _start, $"#DE (quotient overflow) at 0x{_start:X8}");
                StoreDiv(size, (uint)q, (uint)(dividend % v));
                return;
            }
            default:
            {
                var divisor = (long)SignExtend(v, size);
                if (divisor == 0) throw new X86Exception(X86Exception.DivideError, _start, $"#DE (divide by zero) at 0x{_start:X8}");
                var wide = LoadWide(size);
                var dividend = size == 32 ? (long)wide : size == 16 ? (int)wide : (short)wide;
                if (dividend == long.MinValue && divisor == -1) throw new X86Exception(X86Exception.DivideError, _start, "#DE (overflow)");
                var q = dividend / divisor;
                var min = -(1L << (size - 1));
                var max = (1L << (size - 1)) - 1;
                if (q < min || q > max) throw new X86Exception(X86Exception.DivideError, _start, $"#DE (quotient overflow) at 0x{_start:X8}");
                StoreDiv(size, (uint)q & Mask(size), (uint)(dividend % divisor) & Mask(size));
                return;
            }
        }
    }

    private ulong LoadWide(int size) => size switch
    {
        8 => Regs[EAX] & 0xFFFF,
        16 => ((Regs[EDX] & 0xFFFF) << 16) | (Regs[EAX] & 0xFFFF),
        _ => ((ulong)Regs[EDX] << 32) | Regs[EAX],
    };

    private void StoreWide(int size, ulong full)
    {
        switch (size)
        {
            case 8: SetReg(16, EAX, (uint)full); break;
            case 16: SetReg(16, EAX, (uint)full); SetReg(16, EDX, (uint)(full >> 16)); break;
            default: Regs[EAX] = (uint)full; Regs[EDX] = (uint)(full >> 32); break;
        }
    }

    private void StoreDiv(int size, uint q, uint r)
    {
        if (size == 8) { SetReg(8, EAX, q); SetReg(8, 4, r); }
        else { SetReg(size, EAX, q); SetReg(size, EDX, r); }
    }

    private void Group5()
    {
        var m = Decode();
        switch (m.Reg)
        {
            case 0: SetRm(OS, m, IncDec(OS, GetRm(OS, m), false)); return;
            case 1: SetRm(OS, m, IncDec(OS, GetRm(OS, m), true)); return;
            case 2: { var target = GetRm(OS, m); Push(32, Eip); Eip = target; return; }
            case 4: Eip = GetRm(OS, m); return;
            case 6: Push(OS, GetRm(OS, m)); return;
            case 3:
            case 5: throw Unimpl("far CALL/JMP (segment loads not modelled)");
            default: throw Ud("FF /7");
        }
    }

    private void StringOp(byte op)
    {
        var size = (op & 1) == 0 ? 8 : OS;
        var step = (uint)(size / 8);
        if (GetFlag(DF)) step = (uint)-(int)step;
        var rep = _rep != 0;
        var compare = op is 0xA6 or 0xA7 or 0xAE or 0xAF;
        if (rep && Regs[ECX] == 0) return;
        while (true)
        {
            switch (op)
            {
                case 0xA4: case 0xA5:
                    WriteMem(size, Regs[EDI], ReadMem(size, Regs[ESI] + _seg));
                    Regs[ESI] += step;
                    Regs[EDI] += step;
                    break;
                case 0xA6: case 0xA7:
                    Alu(7, size, ReadMem(size, Regs[ESI] + _seg), ReadMem(size, Regs[EDI]));
                    Regs[ESI] += step;
                    Regs[EDI] += step;
                    break;
                case 0xAA: case 0xAB:
                    WriteMem(size, Regs[EDI], GetReg(size, EAX));
                    Regs[EDI] += step;
                    break;
                case 0xAC: case 0xAD:
                    SetReg(size, EAX, ReadMem(size, Regs[ESI] + _seg));
                    Regs[ESI] += step;
                    break;
                default:
                    Alu(7, size, GetReg(size, EAX), ReadMem(size, Regs[EDI]));
                    Regs[EDI] += step;
                    break;
            }
            if (!rep) return;
            if (--Regs[ECX] == 0) return;
            if (compare && (_rep == 0xF3 ? !GetFlag(ZF) : GetFlag(ZF))) return;
        }
    }

    // ---------------------------------------------------------------- 0F xx

    /// <summary>Feature bits reported by CPUID leaf 1 EDX: only what this interpreter implements (TSC, CX8, CMOV).</summary>
    public const uint CpuidFeatureEdx = (1u << 4) | (1u << 8) | (1u << 15);

    /// <summary>Family 6, model 8, stepping 10: Pentium III "Coppermine", as used in the Original Xbox.</summary>
    public const uint CpuidSignature = 0x0000_068A;

    private void Execute0F()
    {
        var op = F8();
        switch (op)
        {
            case 0x0B: throw Ud("UD2");
            case 0x08 or 0x09: return; // INVD / WBINVD: caches are not modelled (memory is always coherent), so a no-op
            case 0x1F: Decode(); return; // multi-byte NOP
            case 0x31: Regs[EAX] = (uint)Retired; Regs[EDX] = (uint)((ulong)Retired >> 32); return;
            case 0xA2:
                switch (Regs[EAX])
                {
                    case 0: Regs[EAX] = 1; Regs[EBX] = 0x756E_6547; Regs[EDX] = 0x4965_6E69; Regs[ECX] = 0x6C65_746E; break; // "GenuineIntel"
                    case 1: Regs[EAX] = CpuidSignature; Regs[EBX] = 0; Regs[ECX] = 0; Regs[EDX] = CpuidFeatureEdx; break;
                    default: Regs[EAX] = Regs[EBX] = Regs[ECX] = Regs[EDX] = 0; break;
                }
                return;
            case >= 0x40 and <= 0x4F:
            {
                var m = Decode();
                var v = GetRm(OS, m);
                if (Cond(op - 0x40)) SetReg(OS, m.Reg, v);
                return;
            }
            case >= 0x80 and <= 0x8F:
            {
                var rel = Imm(OS);
                if (Cond(op - 0x80)) Eip += _o16 ? (uint)(short)rel : rel;
                return;
            }
            case >= 0x90 and <= 0x9F: { var m = Decode(); SetRm(8, m, Cond(op - 0x90) ? 1u : 0u); return; }
            case 0xA3: case 0xAB: case 0xB3: case 0xBB:
            {
                var m = Decode();
                BitOp(op switch { 0xA3 => 4, 0xAB => 5, 0xB3 => 6, _ => 7 }, m, GetReg(OS, m.Reg), fromRegister: true);
                return;
            }
            case 0xBA:
            {
                var m = Decode();
                if (m.Reg < 4) throw Ud("0F BA /r with r < 4");
                BitOp(m.Reg, m, F8(), fromRegister: false);
                return;
            }
            case 0xA4: case 0xA5: case 0xAC: case 0xAD:
            {
                var m = Decode();
                var count = (op & 1) == 0 ? F8() : Regs[ECX] & 0xFF;
                SetRm(OS, m, DoubleShift(op < 0xAC, OS, GetRm(OS, m), GetReg(OS, m.Reg), count & 0x1F));
                return;
            }
            case 0xAF: { var m = Decode(); SetReg(OS, m.Reg, ImulTrunc(OS, GetReg(OS, m.Reg), GetRm(OS, m))); return; }
            case 0xB0: case 0xB1:
            {
                var size = op == 0xB0 ? 8 : OS;
                var m = Decode();
                var dest = GetRm(size, m);
                Alu(7, size, GetReg(size, EAX), dest);
                if (GetFlag(ZF)) SetRm(size, m, GetReg(size, m.Reg));
                else { SetRm(size, m, dest); SetReg(size, EAX, dest); } // destination is always written, as on hardware
                return;
            }
            case 0xC0: case 0xC1:
            {
                var size = op == 0xC0 ? 8 : OS;
                var m = Decode();
                var dest = GetRm(size, m);
                var src = GetReg(size, m.Reg);
                var sum = Alu(0, size, dest, src);
                SetReg(size, m.Reg, dest);
                SetRm(size, m, sum);
                return;
            }
            case 0xC7:
            {
                var m = Decode();
                if (m.Reg != 1 || m.IsReg) throw Ud("0F C7 form");
                var a = Linear(m);
                var lo = _bus.Read32(a);
                var hi = _bus.Read32(a + 4);
                if (lo == Regs[EAX] && hi == Regs[EDX])
                {
                    _bus.Write32(a, Regs[EBX]);
                    _bus.Write32(a + 4, Regs[ECX]);
                    SetFlag(ZF, true);
                }
                else
                {
                    Regs[EAX] = lo;
                    Regs[EDX] = hi;
                    SetFlag(ZF, false);
                }
                return;
            }
            case 0xB6: { var m = Decode(); SetReg(OS, m.Reg, GetRm(8, m)); return; }
            case 0xB7: { var m = Decode(); SetReg(OS, m.Reg, GetRm(16, m)); return; }
            case 0xBE: { var m = Decode(); SetReg(OS, m.Reg, (uint)(sbyte)GetRm(8, m) & Mask(OS)); return; }
            case 0xBF: { var m = Decode(); SetReg(OS, m.Reg, (uint)(short)GetRm(16, m) & Mask(OS)); return; }
            case 0xBC: case 0xBD:
            {
                var m = Decode();
                var v = GetRm(OS, m) & Mask(OS);
                if (v == 0) { SetFlag(ZF, true); return; } // destination unchanged
                SetFlag(ZF, false);
                SetReg(OS, m.Reg, op == 0xBC ? (uint)System.Numerics.BitOperations.TrailingZeroCount(v) : (uint)(31 - System.Numerics.BitOperations.LeadingZeroCount(v)));
                return;
            }
            case >= 0xC8 and <= 0xCF: Regs[op - 0xC8] = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(Regs[op - 0xC8]); return;
            default:
                if (ExecuteSimd(op)) return;
                throw Unimpl($"opcode 0x0F 0x{op:X2}");
        }
    }

    /// <summary>kind: 4 BT, 5 BTS, 6 BTR, 7 BTC.</summary>
    private void BitOp(int kind, ModRm m, uint bitOffset, bool fromRegister)
    {
        var size = OS;
        uint value;
        int bit;
        uint address = 0;
        if (m.IsReg || !fromRegister)
        {
            bit = (int)(bitOffset & (uint)(size - 1));
            if (!m.IsReg) address = Linear(m);
            value = m.IsReg ? GetReg(size, m.Rm) : ReadMem(size, address);
        }
        else
        {
            // Register bit offset into memory addresses a bit string: the offset is signed and may leave the operand.
            var signed = (long)SignExtend(bitOffset, size);
            address = Linear(m) + (uint)((signed >> (size == 16 ? 4 : 5)) * (size / 8));
            bit = (int)(signed & (size - 1));
            value = ReadMem(size, address);
        }
        SetFlag(CF, ((value >> bit) & 1) != 0);
        if (kind == 4) return;
        value = kind switch { 5 => value | (1u << bit), 6 => value & ~(1u << bit), _ => value ^ (1u << bit) };
        if (m.IsReg) SetReg(size, m.Rm, value);
        else WriteMem(size, address, value);
    }

    private uint DoubleShift(bool left, int size, uint dest, uint src, uint count)
    {
        if (count == 0) return dest & Mask(size);
        uint mask = Mask(size), sign = Sign(size);
        dest &= mask;
        src &= mask;
        ulong combined;
        uint r;
        if (left)
        {
            combined = ((ulong)dest << size) | src;
            r = (uint)((combined << (int)count) >> size) & mask;
            SetFlag(CF, ((dest >> (size - (int)count)) & 1) != 0);
        }
        else
        {
            combined = ((ulong)src << size) | dest;
            r = (uint)(combined >> (int)count) & mask;
            SetFlag(CF, ((dest >> ((int)count - 1)) & 1) != 0);
        }
        SetFlag(OF, ((r ^ dest) & sign) != 0);
        SetFlag(AF, false);
        SetSzp(size, r);
        return r;
    }
}
