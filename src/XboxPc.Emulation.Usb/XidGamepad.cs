using System.Buffers.Binary;
using XboxPc.Emulation.Input;

namespace XboxPc.Emulation.Usb;

/// <summary>
/// Original Xbox controller ("Duke") as an XID USB function: device/configuration/XID descriptors, standard requests,
/// XID capability and HID-style report requests, interrupt IN endpoint 1 (20-byte input report, sent when the state
/// changed, NAK otherwise) and interrupt OUT endpoint 2 (6-byte rumble report).
/// Descriptor values follow the public XID documentation (xboxdevwiki "Xbox Input Devices") as used by nxdk's
/// xid_driver: interface class 0x58 / subclass 0x42, XID type 0x01 (game controller) subtype 0x02 (Duke).
/// The USB vendor/product IDs are those the controller reports, needed so title drivers recognise the device.
/// </summary>
public sealed class XidGamepad(Func<XboxGamepadState> state, Action<RumbleState> rumble) : IUsbDevice
{
    public const ushort VendorId = 0x045E, ProductId = 0x0202;

    private static readonly byte[] DeviceDescriptor =
    [
        18, 1, 0x10, 0x01, 0x00, 0x00, 0x00, 0x40,
        0x5E, 0x04, 0x02, 0x02, 0x00, 0x01, 0, 0, 0, 1,
    ];

    private static readonly byte[] ConfigurationDescriptor =
    [
        9, 2, 32, 0, 1, 1, 0, 0x80, 50,              // configuration: 32 bytes total, 1 interface, bus powered, 100 mA
        9, 4, 0, 0, 2, 0x58, 0x42, 0, 0,             // interface 0: 2 endpoints, class 0x58 subclass 0x42
        7, 5, 0x81, 0x03, 0x20, 0x00, 4,             // EP1 IN  interrupt, 32 bytes, 4 ms
        7, 5, 0x02, 0x03, 0x20, 0x00, 4,             // EP2 OUT interrupt, 32 bytes, 4 ms
    ];

    private static readonly byte[] XidDescriptor =
    [
        16, 0x42, 0x00, 0x01, 0x01, 0x02, 20, 6, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    ];

    private byte[] _controlIn = [];
    private int _controlInPos;
    private int? _pendingAddress;
    private bool _expectRumbleData;
    private byte[]? _lastReport;

    public int Address { get; private set; }
    public bool Configured { get; private set; }
    public long ReportsSent { get; private set; }
    public RumbleState LastRumble { get; private set; }

    public void Reset()
    {
        Address = 0;
        Configured = false;
        _controlIn = [];
        _pendingAddress = null;
        _lastReport = null;
    }

    public UsbResponse Setup(ReadOnlySpan<byte> setup)
    {
        var s = setup;
        if (s.Length < 8) return UsbResponse.Stall;
        byte type = s[0], request = s[1];
        var value = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(s[6..]);
        _controlIn = [];
        _controlInPos = 0;
        _expectRumbleData = false;
        byte[]? reply = (type, request) switch
        {
            (0x80, 6) => (value >> 8) switch { 1 => DeviceDescriptor, 2 => ConfigurationDescriptor, _ => null },
            (0x80, 0) or (0x81, 0) or (0x82, 0) => [0, 0],              // GET_STATUS
            (0x80, 8) => [(byte)(Configured ? 1 : 0)],                  // GET_CONFIGURATION
            (0xC1, 6) when value == 0x4200 => XidDescriptor,            // XID descriptor
            (0xC1, 1) when value == 0x0100 => InputCapabilities(),      // GET_CAPABILITIES (input)
            (0xC1, 1) when value == 0x0200 => [0x00, 0x06, 0xFF, 0xFF, 0xFF, 0xFF], // GET_CAPABILITIES (output)
            (0xA1, 1) when value == 0x0100 => state().ToXidReport(),   // GET_REPORT (input)
            _ => null,
        };
        if (reply is not null)
        {
            _controlIn = reply.Length > length ? reply[..length] : reply;
            return UsbResponse.Ack;
        }
        switch (type, request)
        {
            case (0x00, 5): _pendingAddress = value & 0x7F; return UsbResponse.Ack;     // SET_ADDRESS (applies after status)
            case (0x00, 9): Configured = value == 1; _lastReport = null; return UsbResponse.Ack; // SET_CONFIGURATION
            case (0x01, 11): return UsbResponse.Ack;                                     // SET_INTERFACE
            case (0x02, 1): case (0x00, 1): case (0x02, 3): return UsbResponse.Ack;      // CLEAR/SET_FEATURE
            case (0x21, 9) when value == 0x0200: _expectRumbleData = true; return UsbResponse.Ack; // SET_REPORT (output)
            default: return UsbResponse.Stall;
        }
    }

    private static byte[] InputCapabilities()
    {
        var caps = new byte[20];
        caps[1] = 20;
        caps.AsSpan(2).Fill(0xFF);
        return caps;
    }

    public UsbResponse TransferIn(int endpoint, int maxLength, out byte[] data)
    {
        data = [];
        if (endpoint == 0)
        {
            var remaining = _controlIn.Length - _controlInPos;
            if (remaining <= 0)
            {
                // zero-length status stage of an OUT/no-data request
                if (_pendingAddress is { } a) { Address = a; _pendingAddress = null; }
                return UsbResponse.Ack;
            }
            var n = Math.Min(remaining, maxLength);
            data = _controlIn.AsSpan(_controlInPos, n).ToArray();
            _controlInPos += n;
            return UsbResponse.Ack;
        }
        if (endpoint != 1 || !Configured) return UsbResponse.Stall;
        var report = state().ToXidReport();
        if (_lastReport is not null && report.AsSpan().SequenceEqual(_lastReport)) return UsbResponse.Nak;
        _lastReport = report;
        ReportsSent++;
        data = report.Length > maxLength ? report[..maxLength] : report;
        return UsbResponse.Ack;
    }

    public UsbResponse TransferOut(int endpoint, ReadOnlySpan<byte> data)
    {
        if (endpoint == 0)
        {
            if (_expectRumbleData && data.Length >= 6) ApplyRumble(data);
            _expectRumbleData = false;
            return UsbResponse.Ack; // data stage or zero-length status stage of an IN request
        }
        if (endpoint != 2 || !Configured) return UsbResponse.Stall;
        if (data.Length >= 6 && data[1] == 6) ApplyRumble(data);
        return UsbResponse.Ack;
    }

    private void ApplyRumble(ReadOnlySpan<byte> d)
    {
        LastRumble = new RumbleState(BinaryPrimitives.ReadUInt16LittleEndian(d[2..]), BinaryPrimitives.ReadUInt16LittleEndian(d[4..]));
        rumble(LastRumble);
    }
}
