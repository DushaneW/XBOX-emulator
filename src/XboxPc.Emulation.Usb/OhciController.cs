using XboxPc.Emulation.Core;

namespace XboxPc.Emulation.Usb;

public enum UsbResponse
{
    Ack,
    Nak,
    Stall,
}

/// <summary>A full-speed USB function as seen by the host controller (one transaction per call).</summary>
public interface IUsbDevice
{
    /// <summary>Address assigned by SET_ADDRESS (0 until then).</summary>
    int Address { get; }

    void Reset();

    UsbResponse Setup(ReadOnlySpan<byte> setup);

    UsbResponse TransferIn(int endpoint, int maxLength, out byte[] data);

    UsbResponse TransferOut(int endpoint, ReadOnlySpan<byte> data);
}

/// <summary>
/// OHCI 1.0a host controller (the Original Xbox MCPX has two; this models USB0 at 0xFED00000, IRQ 1).
/// Implemented: operational registers, root hub with 4 ports (connect/disconnect/reset/enable, change bits),
/// 1 ms frames driven by virtual time, HCCA frame number and done-queue write-back, periodic (interrupt), control and
/// bulk lists of general TDs (SETUP/OUT/IN, two-page buffers, short packets with buffer rounding, STALL halting,
/// device-not-responding), WDH/SF/RHSC interrupts with the MIE master enable.
/// Not implemented: isochronous TDs (reported), suspend/resume signalling, overcurrent, low-speed devices, frame
/// overrun (all transfers of a frame complete within it), the second controller (USB1 at 0xFED08000, reported).
/// Simplification: a TD's whole buffer is transferred in one frame rather than split into max-packet transactions.
/// </summary>
public sealed class OhciController(PhysicalBus memory, IInterruptController irq, int irqLine, long cpuHz, IHardwareReport report) : IMmioDevice, IClockedDevice
{
    public const int PortCount = 4;

    // Interrupt status / enable bits
    private const uint SO = 1 << 0, WDH = 1 << 1, SF = 1 << 2, RD = 1 << 3, UE = 1 << 4, FNO = 1 << 5, RHSC = 1 << 6, OC = 1u << 30, MIE = 1u << 31;
    // HcControl
    private const uint PLE = 1 << 2, IE = 1 << 3, CLE = 1 << 4, BLE = 1 << 5;
    // HcCommandStatus
    private const uint HCR = 1 << 0, CLF = 1 << 1, BLF = 1 << 2;
    // Port status bits
    private const uint CCS = 1 << 0, PES = 1 << 1, PSS = 1 << 2, PRS = 1 << 4, PPS = 1 << 8, CSC = 1 << 16, PESC = 1 << 17, PSSC = 1 << 18, PRSC = 1 << 20;

    private readonly IUsbDevice?[] _devices = new IUsbDevice?[PortCount];
    private readonly uint[] _port = new uint[PortCount];
    private uint _control, _commandStatus, _intStatus, _intEnable, _hcca, _periodCurrentEd;
    private uint _controlHead, _controlCurrent, _bulkHead, _bulkCurrent, _doneHead, _fmInterval = 0x2EDF, _fmNumber;
    private uint _periodicStart, _lsThreshold = 0x628, _rhDescriptorA = 0x0100_1000 | PortCount, _rhDescriptorB;
    private bool _globalPower;
    private long _nextFrame = cpuHz / 1000;

    public string Name => "USB0 OHCI";
    public long Frames { get; private set; }
    public long TransfersCompleted { get; private set; }

    public bool Operational => ((_control >> 6) & 3) == 2;

    private bool PortPowered(int p) => (_rhDescriptorA & (1 << 9)) != 0 || _globalPower || (_port[p] & PPS) != 0;

    // ------------------------------------------------------------------ device attach / detach

    public void Attach(int port, IUsbDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _devices[port] = device;
        device.Reset();
        _port[port] |= CCS | CSC;
        _port[port] &= ~PES;
        _intStatus |= RHSC;
        UpdateIrq();
    }

    public void Detach(int port)
    {
        if (_devices[port] is null) return;
        _devices[port] = null;
        var wasEnabled = (_port[port] & PES) != 0;
        _port[port] &= ~(CCS | PES | PSS);
        _port[port] |= CSC | (wasEnabled ? PESC : 0);
        _intStatus |= RHSC;
        UpdateIrq();
    }

    public bool IsAttached(int port) => _devices[port] is not null;

    // ------------------------------------------------------------------ registers

    public uint Read(uint offset, int size, AccessKind kind)
    {
        if (offset >= 0x8000) { report.Unmodelled($"USB1 OHCI (0xFED08000) read 0x{offset - 0x8000:X3}: second controller not modelled -> 0"); return 0; }
        switch (offset)
        {
            case 0x00: return 0x10;
            case 0x04: return _control;
            case 0x08: return _commandStatus;
            case 0x0C: return _intStatus;
            case 0x10: case 0x14: return _intEnable;
            case 0x18: return _hcca;
            case 0x1C: return _periodCurrentEd;
            case 0x20: return _controlHead;
            case 0x24: return _controlCurrent;
            case 0x28: return _bulkHead;
            case 0x2C: return _bulkCurrent;
            case 0x30: return _doneHead;
            case 0x34: return _fmInterval;
            case 0x38: return _fmInterval & 0x3FFF;
            case 0x3C: return _fmNumber;
            case 0x40: return _periodicStart;
            case 0x44: return _lsThreshold;
            case 0x48: return _rhDescriptorA;
            case 0x4C: return _rhDescriptorB;
            case 0x50: return _globalPower ? 0 : 0u; // LPS reads as 0 (no local power status), no overcurrent
            case >= 0x54 and < 0x54 + (4 * PortCount):
            {
                var p = (int)(offset - 0x54) / 4;
                var s = _port[p];
                if (PortPowered(p)) s |= PPS;
                return s;
            }
            default:
                report.Unmodelled($"USB0 OHCI read of unknown register 0x{offset:X3} -> 0");
                return 0;
        }
    }

    public void Write(uint offset, int size, uint value)
    {
        if (offset >= 0x8000) { report.Unmodelled($"USB1 OHCI (0xFED08000) write 0x{offset - 0x8000:X3}: second controller not modelled, ignored"); return; }
        switch (offset)
        {
            case 0x04: _control = value; break;
            case 0x08:
                if ((value & HCR) != 0) SoftwareReset();
                _commandStatus |= value & (CLF | BLF | (1 << 3));
                break;
            case 0x0C: _intStatus &= ~value; break;
            case 0x10: _intEnable |= value; break;
            case 0x14: _intEnable &= ~value; break;
            case 0x18: _hcca = value & ~0xFFu; break;
            case 0x20: _controlHead = value & ~0xFu; break;
            case 0x24: _controlCurrent = value & ~0xFu; break;
            case 0x28: _bulkHead = value & ~0xFu; break;
            case 0x2C: _bulkCurrent = value & ~0xFu; break;
            case 0x34: _fmInterval = value; break;
            case 0x40: _periodicStart = value & 0x3FFF; break;
            case 0x44: _lsThreshold = value & 0xFFF; break;
            case 0x48: _rhDescriptorA = (value & ~0xFFu) | PortCount; break;
            case 0x4C: _rhDescriptorB = value; break;
            case 0x50:
                if ((value & 1) != 0) _globalPower = false;           // LPS: clear global power
                if ((value & (1 << 16)) != 0) _globalPower = true;    // LPSC: set global power
                break;
            case >= 0x54 and < 0x54 + (4 * PortCount):
                WritePort((int)(offset - 0x54) / 4, value);
                break;
            default:
                report.Unmodelled($"USB0 OHCI write of unknown register 0x{offset:X3} = 0x{value:X} ignored");
                break;
        }
        UpdateIrq();
    }

    private void WritePort(int p, uint v)
    {
        _port[p] &= ~(v & (CSC | PESC | PSSC | (1 << 19) | PRSC)); // write-1-to-clear change bits
        var connected = (_port[p] & CCS) != 0;
        if ((v & 1) != 0) _port[p] &= ~PES;                          // ClearPortEnable
        if ((v & 2) != 0 && connected) _port[p] |= PES;              // SetPortEnable
        if ((v & 4) != 0 && connected) _port[p] |= PSS;              // SetPortSuspend
        if ((v & 8) != 0) _port[p] &= ~PSS;                          // ClearSuspendStatus
        if ((v & 0x10) != 0 && connected)                            // SetPortReset: completes immediately
        {
            _devices[p]!.Reset();
            _port[p] = (_port[p] & ~PRS) | PES | PRSC;
            _intStatus |= RHSC;
        }
        if ((v & 0x100) != 0) _port[p] |= PPS;                       // SetPortPower
        if ((v & 0x200) != 0) _port[p] &= ~PPS;                      // ClearPortPower
    }

    private void SoftwareReset()
    {
        _control &= 0xC0;          // keep nothing but functional state bits, then enter UsbSuspend
        _control = 3 << 6;
        _commandStatus = 0;
        _intStatus = 0;
        _intEnable = 0;
        _hcca = _periodCurrentEd = _controlHead = _controlCurrent = _bulkHead = _bulkCurrent = _doneHead = 0;
        _fmInterval = 0x2EDF;
        _fmNumber = 0;
        _periodicStart = 0;
        _lsThreshold = 0x628;
    }

    private void UpdateIrq()
    {
        if ((_intEnable & MIE) != 0 && (_intStatus & _intEnable & 0x7FFF_FFFF) != 0) irq.RaiseIrq(irqLine);
    }

    // ------------------------------------------------------------------ frames

    public long NextEventCycle => _nextFrame;

    public void OnEvent(long nowCycles)
    {
        while (nowCycles >= _nextFrame)
        {
            _nextFrame += cpuHz / 1000;
            if (!Operational) continue;
            RunFrame();
        }
        UpdateIrq();
    }

    private void RunFrame()
    {
        Frames++;
        _fmNumber = (_fmNumber + 1) & 0xFFFF;
        if (_hcca != 0)
        {
            memory.Write16(_hcca + 0x80, (ushort)_fmNumber);
            memory.Write16(_hcca + 0x82, 0);
        }
        _intStatus |= SF;

        if ((_control & PLE) != 0 && _hcca != 0)
            ProcessList(memory.Read32(_hcca + ((_fmNumber & 31) * 4)) & ~0xFu, periodic: true);
        if ((_control & IE) != 0) report.Unmodelled("OHCI isochronous list enabled: isochronous transfers are not modelled");
        if ((_control & CLE) != 0 && (_commandStatus & CLF) != 0)
        {
            if (!ProcessList(_controlHead, periodic: false)) _commandStatus &= ~CLF;
        }
        if ((_control & BLE) != 0 && (_commandStatus & BLF) != 0)
        {
            if (!ProcessList(_bulkHead, periodic: false)) _commandStatus &= ~BLF;
        }

        // Done-queue write-back: only when the driver has consumed the previous one (WDH clear).
        if (_doneHead != 0 && (_intStatus & WDH) == 0 && _hcca != 0)
        {
            var otherPending = (_intStatus & _intEnable & ~WDH & 0x7FFF_FFFF) != 0;
            memory.Write32(_hcca + 0x84, _doneHead | (otherPending ? 1u : 0u));
            _doneHead = 0;
            _intStatus |= WDH;
        }
    }

    /// <summary>Walks an ED list. Returns true if any TD was retired (work was found).</summary>
    private bool ProcessList(uint ed, bool periodic)
    {
        var worked = false;
        for (var guard = 0; ed != 0 && guard < 256; guard++)
        {
            var info = memory.Read32(ed);
            var tail = memory.Read32(ed + 4) & ~0xFu;
            var next = memory.Read32(ed + 12) & ~0xFu;
            var skip = (info & (1 << 14)) != 0;
            if ((info & (1 << 15)) != 0) { report.Unmodelled("OHCI isochronous ED: not modelled"); skip = true; }
            for (var n = 0; !skip && n < (periodic ? 1 : 64); n++)
            {
                var headP = memory.Read32(ed + 8);
                if ((headP & 1) != 0 || (headP & ~0xFu) == tail) break;      // halted or empty
                var r = ProcessTd(ed, info, headP);
                if (r == UsbResponse.Nak) break;
                worked = true;
                if (r == UsbResponse.Stall) break;
            }
            ed = next;
        }
        return worked;
    }

    private const uint CcNoError = 0, CcStall = 4, CcDeviceNotResponding = 5, CcDataUnderrun = 9;

    private UsbResponse ProcessTd(uint ed, uint edInfo, uint headP)
    {
        var td = headP & ~0xFu;
        var tdInfo = memory.Read32(td);
        var cbp = memory.Read32(td + 4);
        var nextTd = memory.Read32(td + 8) & ~0xFu;
        var be = memory.Read32(td + 12);
        var address = (int)(edInfo & 0x7F);
        var endpoint = (int)((edInfo >> 7) & 0xF);
        var edDir = (edInfo >> 11) & 3;
        var pid = edDir is 1 or 2 ? edDir : (tdInfo >> 19) & 3; // 0 SETUP, 1 OUT, 2 IN
        var length = cbp == 0 ? 0 : (int)((cbp & ~0xFFFu) == (be & ~0xFFFu) ? be - cbp + 1 : (0x1000 - (cbp & 0xFFF)) + (be & 0xFFF) + 1);

        var device = Enumerable.Range(0, PortCount).Where(p => (_port[p] & PES) != 0).Select(p => _devices[p]).FirstOrDefault(d => d?.Address == address);
        if (device is null) return Retire(ed, headP, td, tdInfo, CcDeviceNotResponding, cbp, nextTd, halt: true);

        UsbResponse r;
        var transferred = length;
        switch (pid)
        {
            case 0:
                r = device.Setup(ReadBuffer(cbp, be, Math.Min(length, 8)));
                break;
            case 1:
                r = device.TransferOut(endpoint, ReadBuffer(cbp, be, length));
                break;
            case 2:
                r = device.TransferIn(endpoint, length, out var data);
                if (r == UsbResponse.Ack)
                {
                    transferred = Math.Min(data.Length, length);
                    WriteBuffer(cbp, be, data.AsSpan(0, transferred));
                }
                break;
            default:
                report.Unmodelled($"OHCI TD with reserved direction/PID {pid}");
                return Retire(ed, headP, td, tdInfo, CcStall, cbp, nextTd, halt: true);
        }
        if (r == UsbResponse.Nak) return r;
        if (r == UsbResponse.Stall) { Retire(ed, headP, td, tdInfo, CcStall, cbp, nextTd, halt: true); return r; }

        TransfersCompleted++;
        if (transferred < length)
        {
            var rounding = (tdInfo & (1 << 18)) != 0;
            var advanced = Advance(cbp, be, transferred);
            return Retire(ed, headP, td, tdInfo, rounding ? CcNoError : CcDataUnderrun, advanced, nextTd, halt: !rounding);
        }
        return Retire(ed, headP, td, tdInfo, CcNoError, 0, nextTd, halt: false);
    }

    private UsbResponse Retire(uint ed, uint headP, uint td, uint tdInfo, uint cc, uint cbp, uint nextTd, bool halt)
    {
        memory.Write32(td, (tdInfo & 0x03FF_FFFF) | (cc << 28));     // EC = 0, CC set
        memory.Write32(td + 4, cbp);
        // Toggle carry flips for every completed data packet; halting preserves it.
        var carry = (headP & 2) ^ (cc == CcNoError ? 2u : 0u);
        memory.Write32(ed + 8, nextTd | carry | (halt ? 1u : 0u));
        memory.Write32(td + 8, _doneHead);
        _doneHead = td;
        return cc == CcStall ? UsbResponse.Stall : UsbResponse.Ack;
    }

    private static uint Advance(uint cbp, uint be, int count)
    {
        var firstPage = (cbp & ~0xFFFu) == (be & ~0xFFFu) ? int.MaxValue : (int)(0x1000 - (cbp & 0xFFF));
        return count < firstPage ? cbp + (uint)count : (be & ~0xFFFu) + (uint)(count - firstPage);
    }

    private byte[] ReadBuffer(uint cbp, uint be, int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = memory.Read8(Advance(cbp, be, i));
        return data;
    }

    private void WriteBuffer(uint cbp, uint be, ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++) memory.Write8(Advance(cbp, be, i), data[i]);
    }
}
