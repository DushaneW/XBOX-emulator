using XboxPc.Emulation.Core;

namespace XboxPc.Emulation.Gpu.Nv2a;

public enum ScanoutFormat : uint
{
    A1R5G5B5 = 0x10,
    R5G6B5 = 0x11,
    A8R8G8B8 = 0x12,
}

/// <summary>A scanned-out frame in host BGRA32 order.</summary>
public sealed record Frame(int Width, int Height, uint[] Bgra, long VBlank);

/// <summary>
/// NV2A GPU. Register blocks follow the offsets nxdk (hal/video.c, pbkit) and the public NV2A documentation use:
/// PMC 0x000000, PBUS 0x001000, PFIFO 0x002000, PVIDEO 0x008000, PTIMER 0x009000, PFB 0x100000, PGRAPH 0x400000,
/// PCRTC 0x600000, PRMCIO 0x601000, PRAMDAC 0x680000, USER_DAC 0x681000, PRAMIN 0x700000, USER 0x800000.
/// Modelled behaviour: interrupt aggregation (PMC), vertical blank (PCRTC), PTIMER counter, PLL/memory configuration
/// values, CRTC index/data registers, 1 MB instance memory, the PFIFO DMA pusher (<see cref="Pfifo"/>) and the
/// PGRAPH Kelvin 3D engine (<see cref="Pgraph"/>, CPU reference renderer). Configuration registers without side
/// effects (PBUS, PVIDEO, PFB, PRAMDAC, PCRTC config, PGRAPH debug) are kept as plain storage, as on hardware.
/// Every other access is reported as unmodelled.
/// </summary>
public sealed class Nv2aDevice : IMmioDevice, IClockedDevice
{
    public const uint PmcBoot0 = 0x000000, PmcIntr0 = 0x000100, PmcIntrEn0 = 0x000140, PmcEnable = 0x000200;
    public const uint PcrtcIntr = 0x600100, PcrtcIntrEn = 0x600140, PcrtcStart = 0x600800;
    public const uint CrtcIndex = 0x6013D4, CrtcData = 0x6013D5;
    public const uint DacWriteAddress = 0x6813C8, DacPaletteData = 0x6813C9;
    public const uint PtimerIntr = 0x009100, PtimerIntrEn = 0x009140, PtimerNumerator = 0x009200, PtimerDenominator = 0x009210;
    public const uint PtimerTime0 = 0x009400, PtimerTime1 = 0x009410, PtimerAlarm = 0x009420;
    public const uint PfbCfg0 = 0x100200, PfbCfg1 = 0x100204, PfbWcCache = 0x100410;
    public const uint PramdacNvpll = 0x680500, PramdacMpll = 0x680504, PramdacVpll = 0x680508;
    public const uint Pramin = 0x700000, RaminSize = 0x100000, User = 0x800000;
    public const int VBlankIrq = 3;

    public const uint IntrPfifo = 1u << 8, IntrPgraph = 1u << 12, IntrPtimer = 1u << 20, IntrPcrtc = 1u << 24;

    private readonly IInterruptController _irq;
    private readonly IHardwareReport _report;
    private readonly long _cpuHz;
    private readonly byte[] _gamma = Enumerable.Range(0, 768).Select(i => (byte)(i / 3)).ToArray();
    private readonly byte[] _crtc = new byte[256];
    private readonly Dictionary<uint, uint> _storage = new()
    {
        [PfbCfg0] = 0x0307_0103,      // 4 memory partitions (pbkit checks PART_3), values as on retail units
        [PfbCfg1] = 0x1144_8000,
        [PramdacNvpll] = 0x0001_1C01, // M=1 N=28 P=1: 16.667 MHz x 28 / 2 = 233 MHz core clock
        [PramdacMpll] = 0x0000_7702,
        [PramdacVpll] = 0x0003_C20D,
    };
    private int _dacIndex;
    private byte _crtcIndex;
    private long _nextVBlank;
    private bool _irqLevel;
    private uint _ptimerIntr, _ptimerIntrEn;

    public Nv2aDevice(PhysicalBus memory, IInterruptController irq, IHardwareReport report, long cpuHz)
    {
        Memory = memory;
        _irq = irq;
        _report = report;
        _cpuHz = cpuHz;
        _nextVBlank = cpuHz / 60;
        Ramin = new byte[RaminSize];
        Pfifo = new Pfifo(this);
        Pgraph = new Pgraph(this);
    }

    public string Name => "NV2A GPU";

    public PhysicalBus Memory { get; }

    /// <summary>Instance memory (PRAMIN): object tables, DMA objects, RAMHT/RAMFC.</summary>
    public byte[] Ramin { get; }

    public Pfifo Pfifo { get; }
    public Pgraph Pgraph { get; }

    /// <summary>Virtual CPU cycle counter of the machine (time base for PTIMER). Set by the machine.</summary>
    public Func<long> CycleClock { get; set; } = () => 0;

    public uint PmcInterruptEnable { get; private set; }
    public uint PcrtcInterrupt { get; private set; }
    public uint PcrtcInterruptEnable { get; private set; }
    public uint FramebufferStart { get; private set; }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Pitch { get; private set; }
    public ScanoutFormat Format { get; private set; } = ScanoutFormat.A8R8G8B8;
    public int RefreshHz { get; private set; } = 60;
    public bool OutputEnabled { get; set; } = true;
    public long VBlankCount { get; private set; }

    /// <summary>Optional host-GPU renderer that mirrors the Kelvin engine's work (see <see cref="IRenderBackend"/>).</summary>
    public IRenderBackend? Backend { get; set; }

    /// <summary>Times the title pointed scan-out at a different buffer (PCRTC_START changed): presented frames of a double-buffered title.</summary>
    public long FlipCount { get; private set; }

    /// <summary>Raised at every vertical blank, on the emulation thread.</summary>
    public event Action<long>? VBlank;

    internal void Report(string message) => _report.Unmodelled(message);

    /// <summary>Called by the kernel's AvSetDisplayMode: programs the CRTC for a mode and a framebuffer.</summary>
    public void SetDisplayMode(int width, int height, int pitch, ScanoutFormat format, uint framebuffer, int refreshHz)
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format), $"Unsupported scan-out format 0x{(uint)format:X}.");
        Width = width;
        Height = height;
        Pitch = pitch;
        Format = format;
        RefreshHz = refreshHz is 50 or 60 ? refreshHz : 60;
        FramebufferStart = framebuffer & 0x7FFF_FFFF;
    }

    // ------------------------------------------------------------------ PRAMIN helpers

    public uint RaminRead32(uint offset) => offset + 4 <= RaminSize ? BitConverter.ToUInt32(Ramin, (int)offset) : 0;

    public void RaminWrite32(uint offset, uint value)
    {
        if (offset + 4 <= RaminSize) BitConverter.TryWriteBytes(Ramin.AsSpan((int)offset), value);
    }

    // ------------------------------------------------------------------ MMIO

    public uint Read(uint offset, int size, AccessKind kind)
    {
        if (kind == AccessKind.Fetch) throw new BusFaultException(offset, kind, size, Name, "instruction fetch from GPU registers");
        if (offset >= Pramin && offset < Pramin + RaminSize)
        {
            var o = (int)(offset - Pramin);
            uint v = 0;
            for (var i = 0; i < size && o + i < RaminSize; i++) v |= (uint)Ramin[o + i] << (8 * i);
            return v;
        }
        if (offset == CrtcIndex && size == 1) return _crtcIndex;
        if (offset == CrtcData && size == 1) return _crtc[_crtcIndex];
        if (offset == DacWriteAddress && size == 1) return (uint)(_dacIndex / 3);

        var aligned = offset & ~3u;
        var shift = (int)(offset & 3) * 8;
        var value = ReadRegister(aligned);
        return size == 4 ? value : (value >> shift) & (size == 1 ? 0xFFu : 0xFFFFu);
    }

    private uint ReadRegister(uint offset)
    {
        switch (offset)
        {
            case PmcBoot0: return 0x02A0_00A2; // NV2A A2 stepping, as reported by retail consoles
            case PmcIntr0: return PendingPmc();
            case PmcIntrEn0: return PmcInterruptEnable;
            case PcrtcIntr: return PcrtcInterrupt;
            case PcrtcIntrEn: return PcrtcInterruptEnable;
            case PcrtcStart: return FramebufferStart;
            case PtimerIntr: return _ptimerIntr;
            case PtimerIntrEn: return _ptimerIntrEn;
            case PtimerTime0: return (uint)((PtimerTicks() & 0x7FF_FFFF) << 5);
            case PtimerTime1: return (uint)((PtimerTicks() >> 27) & 0x1FFF_FFFF);
            case 0x008100: return 0; // PVIDEO_INTR: the video overlay never raises interrupts (not modelled)
        }
        if (offset is >= 0x002000 and < 0x004000) return Pfifo.ReadRegister(offset);
        if (offset is >= 0x400000 and < 0x402000) return Pgraph.ReadRegister(offset);
        if (offset >= User && offset < User + 0x20_0000) return Pfifo.ReadUser((int)((offset - User) >> 16), (offset - User) & 0xFFFF);
        if (IsStorage(offset)) return _storage.GetValueOrDefault(offset);
        Report($"NV2A read of unmodelled register 0x{offset:X6}{Engine(offset)}");
        return 0;
    }

    public void Write(uint offset, int size, uint value)
    {
        if (offset >= Pramin && offset < Pramin + RaminSize)
        {
            var o = (int)(offset - Pramin);
            for (var i = 0; i < size && o + i < RaminSize; i++) Ramin[o + i] = (byte)(value >> (8 * i));
            return;
        }
        switch (offset)
        {
            case CrtcIndex when size == 1: _crtcIndex = (byte)value; return;
            case CrtcIndex when size == 2: _crtcIndex = (byte)value; _crtc[_crtcIndex] = (byte)(value >> 8); return;
            case CrtcData when size == 1: _crtc[_crtcIndex] = (byte)value; return;
            case DacWriteAddress when size == 1: _dacIndex = (int)(value & 0xFF) * 3; return;
            case DacPaletteData when size == 1:
                _gamma[_dacIndex] = (byte)value;
                _dacIndex = (_dacIndex + 1) % _gamma.Length;
                return;
        }
        var aligned = offset & ~3u;
        if (size != 4)
        {
            var shift = (int)(offset & 3) * 8;
            var mask = (size == 1 ? 0xFFu : 0xFFFFu) << shift;
            value = (ReadRegister(aligned) & ~mask) | ((value << shift) & mask);
        }
        WriteRegister(aligned, value);
        UpdateIrq();
    }

    private void WriteRegister(uint offset, uint value)
    {
        switch (offset)
        {
            case PmcIntrEn0: PmcInterruptEnable = value; return;
            case PcrtcIntr: PcrtcInterrupt &= ~value; return; // write-1-to-clear
            case PcrtcIntrEn: PcrtcInterruptEnable = value; return;
            case PcrtcStart:
                if ((value & 0x7FFF_FFFF) != FramebufferStart) FlipCount++;
                FramebufferStart = value & 0x7FFF_FFFF;
                return;
            case PtimerIntr: _ptimerIntr &= ~value; return;
            case PtimerIntrEn: _ptimerIntrEn = value; return;
            case PtimerTime0 or PtimerTime1:
                Report("NV2A PTIMER time write ignored (the counter is derived from virtual time)");
                return;
            case PfbWcCache: _storage[offset] = value & ~0x0001_0000u; return; // write-combine flush completes at once
            case 0x008100: return; // PVIDEO_INTR acknowledge
        }
        if (offset is >= 0x002000 and < 0x004000) { Pfifo.WriteRegister(offset, value); return; }
        if (offset is >= 0x400000 and < 0x402000) { Pgraph.WriteRegister(offset, value); return; }
        if (offset >= User && offset < User + 0x20_0000) { Pfifo.WriteUser((int)((offset - User) >> 16), (offset - User) & 0xFFFF, value); return; }
        if (IsStorage(offset)) { _storage[offset] = value; return; }
        Report($"NV2A write of unmodelled register 0x{offset:X6} = 0x{value:X}{Engine(offset)} - ignored");
    }

    /// <summary>Configuration blocks whose registers have no side effects that matter to the model.</summary>
    private static bool IsStorage(uint offset) => offset switch
    {
        >= 0x000200 and < 0x000300 => true,   // PMC enable / misc
        >= 0x001000 and < 0x002000 => true,   // PBUS (PCI config mirror, debug)
        >= 0x008000 and < 0x009000 => true,   // PVIDEO overlay configuration (overlay itself not modelled)
        >= 0x009000 and < 0x00A000 => true,   // PTIMER numerator/denominator/alarm
        >= 0x100000 and < 0x101000 => true,   // PFB memory configuration and tiles (tiling is transparent here)
        >= 0x600000 and < 0x601000 => true,   // PCRTC configuration
        >= 0x680000 and < 0x681000 => true,   // PRAMDAC PLLs and timings
        _ => false,
    };

    private static string Engine(uint offset) => offset switch
    {
        >= 0x000000 and < 0x001000 => " [PMC]",
        >= 0x101000 and < 0x102000 => " [PSTRAPS]",
        >= 0x601000 and < 0x602000 => " [PRMCIO]",
        >= 0x681000 and < 0x682000 => " [USER_DAC]",
        >= 0x0C0000 and < 0x0C1000 => " [PRMVIO]",
        >= 0x00D000 and < 0x00E000 => " [PTV]",
        >= 0x004000 and < 0x005000 => " [PMEDIA]",
        _ => "",
    };

    // ------------------------------------------------------------------ PTIMER / interrupts

    /// <summary>Core clock from the NVPLL coefficients: 16.6667 MHz crystal x N / (M x 2^P).</summary>
    public double CoreClockHz
    {
        get
        {
            var c = _storage.GetValueOrDefault(PramdacNvpll);
            double m = c & 0xFF, n = (c >> 8) & 0xFF, p = (c >> 16) & 0xF;
            return m == 0 ? 0 : 16_666_666.0 * n / (m * Math.Pow(2, p));
        }
    }

    private ulong PtimerTicks()
    {
        double num = _storage.GetValueOrDefault(PtimerNumerator) + 1, den = _storage.GetValueOrDefault(PtimerDenominator) + 1;
        var seconds = CycleClock() / (double)_cpuHz;
        return (ulong)(seconds * CoreClockHz * den / num);
    }

    public uint PendingPmc()
    {
        uint s = 0;
        if ((PcrtcInterrupt & PcrtcInterruptEnable & 1) != 0) s |= IntrPcrtc;
        if ((Pgraph.Interrupt & Pgraph.InterruptEnable) != 0) s |= IntrPgraph;
        if ((Pfifo.Interrupt & Pfifo.InterruptEnable) != 0) s |= IntrPfifo;
        if ((_ptimerIntr & _ptimerIntrEn) != 0) s |= IntrPtimer;
        return s;
    }

    /// <summary>The GPU interrupt is level-sensitive: an IRQ is raised whenever the level rises.</summary>
    internal void UpdateIrq()
    {
        var level = PendingPmc() != 0 && (PmcInterruptEnable & 3) != 0;
        if (level && !_irqLevel) _irq.RaiseIrq(VBlankIrq);
        _irqLevel = level;
    }

    // ------------------------------------------------------------------ vertical blank

    public long NextEventCycle => _nextVBlank;

    public void OnEvent(long nowCycles)
    {
        while (nowCycles >= _nextVBlank)
        {
            VBlankCount++;
            PcrtcInterrupt |= 1;
            _nextVBlank += _cpuHz / RefreshHz;
            VBlank?.Invoke(VBlankCount);
        }
        UpdateIrq();
    }

    // ------------------------------------------------------------------ scan-out

    /// <summary>
    /// Reads the framebuffer the CRTC points at (unified memory) and converts it to BGRA32 through the DAC gamma ramp.
    /// Returns null while no display mode is set.
    /// </summary>
    public Frame? ScanOut()
    {
        if (Width <= 0 || Height <= 0 || !OutputEnabled) return null;
        if (Backend?.TryScanOut(FramebufferStart, Width, Height, Pitch, VBlankCount) is { } hostFrame) return ApplyGamma(hostFrame);
        var pixels = new uint[Width * Height];
        var bpp = Format == ScanoutFormat.A8R8G8B8 ? 4 : 2;
        for (var y = 0; y < Height; y++)
        {
            var row = Memory.ReadBytes(FramebufferStart + (uint)(y * Pitch), Width * bpp);
            for (var x = 0; x < Width; x++)
            {
                uint r, g, b;
                if (bpp == 4)
                {
                    b = row[x * 4];
                    g = row[(x * 4) + 1];
                    r = row[(x * 4) + 2];
                }
                else
                {
                    var p = (uint)(row[x * 2] | (row[(x * 2) + 1] << 8));
                    if (Format == ScanoutFormat.R5G6B5) { r = Expand(p >> 11, 5); g = Expand((p >> 5) & 0x3F, 6); b = Expand(p & 0x1F, 5); }
                    else { r = Expand((p >> 10) & 0x1F, 5); g = Expand((p >> 5) & 0x1F, 5); b = Expand(p & 0x1F, 5); }
                }
                pixels[(y * Width) + x] = 0xFF00_0000u | ((uint)_gamma[(r * 3)] << 16) | ((uint)_gamma[(g * 3) + 1] << 8) | _gamma[(b * 3) + 2];
            }
        }
        return new Frame(Width, Height, pixels, VBlankCount);
    }

    /// <summary>Applies the DAC gamma ramp to a host-rendered frame (skipped when the ramp is the identity).</summary>
    private Frame ApplyGamma(Frame f)
    {
        var identity = true;
        for (var i = 0; i < 256 && identity; i++) identity = _gamma[i * 3] == i && _gamma[(i * 3) + 1] == i && _gamma[(i * 3) + 2] == i;
        if (identity) return f;
        var px = f.Bgra;
        for (var i = 0; i < px.Length; i++)
        {
            var p = px[i];
            px[i] = 0xFF00_0000u | ((uint)_gamma[((p >> 16) & 0xFF) * 3] << 16) | ((uint)_gamma[(((p >> 8) & 0xFF) * 3) + 1] << 8) | _gamma[((p & 0xFF) * 3) + 2];
        }
        return f;
    }

    private static uint Expand(uint v, int bits) => (v << (8 - bits)) | (v >> ((2 * bits) - 8));
}
