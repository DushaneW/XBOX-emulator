namespace XboxPc.Emulation.X86;

/// <summary>Complete architectural state of one thread (integer, x87/MMX, SSE). Used by the HLE scheduler.</summary>
public sealed class CpuContext
{
    public uint[] Regs { get; } = new uint[8];
    public uint Eip { get; set; }
    public uint Eflags { get; set; } = 0x202;
    public uint FsBase { get; set; }
    public uint GsBase { get; set; }
    internal ulong[] FpMant { get; } = new ulong[8];
    internal ushort[] FpExp { get; } = new ushort[8];
    internal int FpTop { get; set; }
    internal ushort FpTags { get; set; } = 0xFFFF;
    internal ushort FpStatus { get; set; }
    public ushort FpControl { get; set; } = 0x037F;
    internal uint[] Xmm { get; } = new uint[32];
    public uint Mxcsr { get; set; } = 0x1F80;
}

public sealed partial class X86Cpu
{
    public void SaveContext(CpuContext c)
    {
        ArgumentNullException.ThrowIfNull(c);
        Regs.CopyTo(c.Regs, 0);
        c.Eip = Eip;
        c.Eflags = Eflags;
        c.FsBase = FsBase;
        c.GsBase = GsBase;
        _fpMant.CopyTo(c.FpMant, 0);
        _fpExp.CopyTo(c.FpExp, 0);
        c.FpTop = _fpTop;
        c.FpTags = _fpTags;
        c.FpStatus = _fpStatus;
        c.FpControl = FpuControl;
        _xmm.CopyTo(c.Xmm, 0);
        c.Mxcsr = Mxcsr;
    }

    public void RestoreContext(CpuContext c)
    {
        ArgumentNullException.ThrowIfNull(c);
        c.Regs.CopyTo(Regs, 0);
        Eip = c.Eip;
        Eflags = c.Eflags | 0x2;
        FsBase = c.FsBase;
        GsBase = c.GsBase;
        c.FpMant.CopyTo(_fpMant, 0);
        c.FpExp.CopyTo(_fpExp, 0);
        _fpTop = c.FpTop;
        _fpTags = c.FpTags;
        _fpStatus = c.FpStatus;
        FpuControl = c.FpControl;
        c.Xmm.CopyTo(_xmm, 0);
        Mxcsr = c.Mxcsr;
        Halted = false;
    }
}
