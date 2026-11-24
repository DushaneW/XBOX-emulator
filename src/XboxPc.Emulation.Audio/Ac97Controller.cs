using XboxPc.Emulation.Core;

namespace XboxPc.Emulation.Audio;

/// <summary>Receives the emulated console's analog PCM output: interleaved 16-bit stereo at <see cref="Ac97Controller.SampleRate"/>.</summary>
public interface IAudioSink
{
    void Write(ReadOnlySpan<short> interleavedStereo);
}

/// <summary>
/// MCPX AC97 audio controller (MMIO at 0xFEC00000, IRQ 6): the interface used by nxdk's hal/audio.c and other
/// homebrew audio back-ends. Modelled: the bus-master DMA engines for PCM out (+0x110) and S/PDIF out (+0x170),
/// 32-entry buffer descriptor lists, CIV/LVI/SR/PICB/PIV/CR semantics as in the Intel AC'97 controller specification
/// (and QEMU's implementation of it), buffer-completion and last-valid-buffer interrupts, and the global control/status
/// registers (cold reset, codec ready). Samples are consumed at 48 kHz stereo in virtual time; PCM-out samples go to
/// <see cref="Output"/>. Not modelled (reported): PCM in, codec mixer registers, and the separate MCPX APU (DirectSound
/// hardware voice processor) that commercial titles use.
/// </summary>
public sealed class Ac97Controller(PhysicalBus memory, IInterruptController irq, int irqLine, long cpuHz, IHardwareReport report) : IMmioDevice, IClockedDevice
{
    public const int SampleRate = 48000;
    public const uint PcmInBase = 0x100, PcmOutBase = 0x110, SpdifBase = 0x170;
    public const uint GlobalControl = 0x12C, GlobalStatus = 0x130;

    // status register bits
    public const ushort SrDch = 0x01, SrCelv = 0x02, SrLvbci = 0x04, SrBcis = 0x08, SrFifoe = 0x10;

    // control register bits
    public const byte CrRpbm = 0x01, CrRr = 0x02, CrLvbie = 0x04, CrIoce = 0x08, CrFeie = 0x10;

    /// <summary>One bus-master DMA engine.</summary>
    public sealed class Channel(string name)
    {
        public string Name { get; } = name;
        public uint Bdbar { get; internal set; }
        public byte Civ { get; internal set; }
        public byte Lvi { get; internal set; }
        public ushort Sr { get; internal set; } = SrDch;
        public ushort Picb { get; internal set; }
        public byte Piv { get; internal set; }
        public byte Cr { get; internal set; }
        internal uint BufferAddress;
        internal ushort BufferControl;
        internal ushort BufferLength;
        public long BuffersCompleted { get; internal set; }
        public long SamplesConsumed { get; internal set; }

        internal void Reset()
        {
            Bdbar = 0; Civ = 0; Lvi = 0; Sr = SrDch; Picb = 0; Piv = 0;
            Cr &= CrLvbie | CrIoce | CrFeie;
        }
    }

    private readonly long _cyclesPerMs = cpuHz / 1000;
    private long _nextTick;
    private long _msTicks;
    private uint _globalControl;
    private readonly short[] _mix = new short[SampleRate / 1000 * 2];

    public string Name => "MCPX AC97 audio";

    public Channel PcmOut { get; } = new("PCM out");
    public Channel Spdif { get; } = new("S/PDIF out");
    public Channel PcmIn { get; } = new("PCM in");

    /// <summary>Destination for PCM-out samples (null = discarded after being consumed in time).</summary>
    public IAudioSink? Output { get; set; }

    public long InterruptsRaised { get; private set; }

    private Channel? ChannelAt(uint offset, out uint reg)
    {
        reg = offset & 0xF;
        return (offset & ~0xFu) switch { PcmInBase => PcmIn, PcmOutBase => PcmOut, SpdifBase => Spdif, _ => null };
    }

    // ------------------------------------------------------------------ registers (decomposed into bytes)

    public uint Read(uint offset, int size, AccessKind kind)
    {
        uint v = 0;
        for (var i = 0; i < size; i++) v |= (uint)ReadByte(offset + (uint)i) << (8 * i);
        return v;
    }

    public void Write(uint offset, int size, uint value)
    {
        // Status is write-1-to-clear over its full width; everything else is byte-wise.
        for (var i = 0; i < size; i++) WriteByte(offset + (uint)i, (byte)(value >> (8 * i)));
    }

    private byte ReadByte(uint offset)
    {
        if (ChannelAt(offset, out var reg) is { } c && reg <= 0xB)
        {
            return reg switch
            {
                <= 3 => (byte)(c.Bdbar >> (8 * (int)reg)),
                4 => c.Civ,
                5 => c.Lvi,
                6 => (byte)c.Sr,
                7 => (byte)(c.Sr >> 8),
                8 => (byte)c.Picb,
                9 => (byte)(c.Picb >> 8),
                0xA => c.Piv,
                _ => c.Cr,
            };
        }
        if (offset is >= GlobalControl and < GlobalControl + 4) return (byte)(_globalControl >> (8 * (int)(offset - GlobalControl)));
        if (offset is >= GlobalStatus and < GlobalStatus + 4)
        {
            // bit 8: primary codec ready (after cold reset is released); bits 5/6: PCM out / in interrupt pending
            var sta = ((_globalControl & 2) != 0 ? 0x100u : 0) | (Pending(PcmOut) ? 0x40u : 0) | (Pending(PcmIn) ? 0x20u : 0);
            return (byte)(sta >> (8 * (int)(offset - GlobalStatus)));
        }
        report.Unmodelled($"AC97 read of unmodelled register 0x{offset:X3}{(offset < 0x80 ? " (codec mixer)" : "")}");
        return 0;
    }

    private void WriteByte(uint offset, byte value)
    {
        if (ChannelAt(offset, out var reg) is { } c && reg <= 0xB)
        {
            switch (reg)
            {
                case <= 3:
                    var shift = 8 * (int)reg;
                    c.Bdbar = (c.Bdbar & ~(0xFFu << shift)) | ((uint)value << shift);
                    c.Bdbar &= ~7u;
                    break;
                case 5: WriteLvi(c, value); break;
                case 6: c.Sr &= (ushort)~(value & (SrLvbci | SrBcis | SrFifoe)); break;
                case 0xB: WriteCr(c, value); break;
                default: break; // CIV, SR high byte, PICB, PIV are read-only
            }
            return;
        }
        if (offset is >= GlobalControl and < GlobalControl + 4)
        {
            var shift = 8 * (int)(offset - GlobalControl);
            _globalControl = (_globalControl & ~(0xFFu << shift)) | ((uint)value << shift);
            return;
        }
        if (offset is >= GlobalStatus and < GlobalStatus + 4) return; // status bits we model are read-only
        report.Unmodelled($"AC97 write of unmodelled register 0x{offset:X3}{(offset < 0x80 ? " (codec mixer)" : "")} = 0x{value:X2} ignored");
    }

    private void WriteLvi(Channel c, byte value)
    {
        // A new last-valid index restarts an engine that halted at the end of the list (QEMU/ICH behaviour).
        if ((c.Cr & CrRpbm) != 0 && (c.Sr & SrDch) != 0)
        {
            c.Sr &= unchecked((ushort)~(SrDch | SrCelv));
            Advance(c);
        }
        c.Lvi = (byte)(value & 31);
    }

    private void WriteCr(Channel c, byte value)
    {
        if ((value & CrRr) != 0)
        {
            c.Reset();
            return;
        }
        c.Cr = (byte)(value & (CrRpbm | CrLvbie | CrIoce | CrFeie));
        if ((c.Cr & CrRpbm) == 0)
        {
            c.Sr |= SrDch;
            return;
        }
        if (c == PcmIn) report.Unmodelled("AC97 PCM-in DMA started: audio input is not emulated (no samples are captured)");
        Advance(c);
        c.Sr &= unchecked((ushort)~SrDch);
    }

    private static bool Pending(Channel c) =>
        ((c.Sr & SrBcis) != 0 && (c.Cr & CrIoce) != 0) || ((c.Sr & SrLvbci) != 0 && (c.Cr & CrLvbie) != 0);

    /// <summary>CIV = PIV, PIV + 1, and load the descriptor at the new CIV.</summary>
    private void Advance(Channel c)
    {
        c.Civ = c.Piv;
        c.Piv = (byte)((c.Piv + 1) & 31);
        var d = c.Bdbar + (uint)(c.Civ * 8);
        c.BufferAddress = memory.Read32(d) & ~1u;
        var ctlLen = memory.Read32(d + 4);
        c.BufferLength = (ushort)ctlLen;
        c.BufferControl = (ushort)(ctlLen >> 16);
        c.Picb = c.BufferLength;
    }

    // ------------------------------------------------------------------ DMA in virtual time

    public long NextEventCycle => _nextTick;

    public void OnEvent(long nowCycles)
    {
        _msTicks++;
        _nextTick = nowCycles + _cyclesPerMs;
        var words = SampleRate / 1000 * 2; // 48 stereo frames = 96 16-bit samples per millisecond
        var produced = Run(PcmOut, words, _mix);
        Run(Spdif, words, null);
        if (produced > 0) Output?.Write(_mix.AsSpan(0, produced));
    }

    /// <summary>Consumes up to <paramref name="budget"/> samples; returns how many were written to <paramref name="into"/>.</summary>
    private int Run(Channel c, int budget, short[]? into)
    {
        var written = 0;
        var raise = false;
        while (budget > 0 && (c.Sr & SrDch) == 0 && (c.Cr & CrRpbm) != 0)
        {
            if (c.Picb == 0)
            {
                if (c.BufferLength == 0)
                {
                    // empty descriptor: stop at the end of the list, otherwise skip it
                    if (c.Civ == c.Lvi) { c.Sr |= SrDch; break; }
                    c.Sr &= unchecked((ushort)~SrCelv);
                    Advance(c);
                    continue;
                }
            }
            var n = Math.Min(budget, c.Picb);
            var start = (uint)(c.BufferLength - c.Picb) * 2;
            for (var i = 0; i < n; i++)
            {
                var s = (short)memory.Read16(c.BufferAddress + start + (uint)(i * 2));
                if (into is not null) into[written++] = s;
            }
            c.Picb -= (ushort)n;
            c.SamplesConsumed += n;
            budget -= n;
            if (c.Picb > 0) continue;

            // buffer completed
            c.BuffersCompleted++;
            var sr = (ushort)(c.Sr & ~SrCelv);
            if ((c.BufferControl & 0x8000) != 0) sr |= SrBcis;
            if (c.Civ == c.Lvi)
            {
                sr |= SrLvbci | SrDch | SrCelv;
                c.Sr = sr;
            }
            else
            {
                c.Sr = sr;
                Advance(c);
            }
            raise |= Pending(c);
        }
        if (raise)
        {
            InterruptsRaised++;
            irq.RaiseIrq(irqLine);
        }
        return written;
    }

    /// <summary>Virtual milliseconds of DMA time elapsed since the device was created.</summary>
    public long ElapsedMs => _msTicks;
}
