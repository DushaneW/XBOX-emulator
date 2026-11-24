using XboxPc.Emulation.Input;
using XboxPc.Emulation.Usb;

namespace XboxPc.Emulation.Tests.Usb;

/// <summary>USB function-level behaviour of the emulated Duke controller, independent of the OHCI controller.</summary>
public class XidGamepadTests
{
    private XboxGamepadState _state;
    private readonly List<RumbleState> _rumble = [];

    private XidGamepad Pad() => new(() => _state, _rumble.Add);

    private static byte[] Setup(byte type, byte request, ushort value, ushort index, ushort length) =>
        [type, request, (byte)value, (byte)(value >> 8), (byte)index, (byte)(index >> 8), (byte)length, (byte)(length >> 8)];

    private static byte[] ControlIn(XidGamepad pad, byte[] setup, int maxPacket = 8)
    {
        Assert.Equal(UsbResponse.Ack, pad.Setup(setup));
        var all = new List<byte>();
        while (true)
        {
            Assert.Equal(UsbResponse.Ack, pad.TransferIn(0, maxPacket, out var chunk));
            all.AddRange(chunk);
            if (chunk.Length < maxPacket) break;
        }
        Assert.Equal(UsbResponse.Ack, pad.TransferOut(0, [])); // status stage
        return [.. all];
    }

    [Fact]
    public void Device_descriptor_identifies_the_duke_controller()
    {
        var d = ControlIn(Pad(), Setup(0x80, 6, 0x0100, 0, 18));
        Assert.Equal(18, d.Length);
        Assert.Equal(XidGamepad.VendorId, BitConverter.ToUInt16(d, 8));
        Assert.Equal(XidGamepad.ProductId, BitConverter.ToUInt16(d, 10));
    }

    [Fact]
    public void Configuration_descriptor_is_truncated_to_requested_length_then_complete()
    {
        var pad = Pad();
        Assert.Equal(9, ControlIn(pad, Setup(0x80, 6, 0x0200, 0, 9)).Length);
        var full = ControlIn(pad, Setup(0x80, 6, 0x0200, 0, 255));
        Assert.Equal(32, full.Length);
        Assert.Equal(0x58, full[9 + 5]); // interface class XID
        Assert.Equal(0x81, full[18 + 2]); // EP1 IN
        Assert.Equal(0x02, full[25 + 2]); // EP2 OUT
    }

    [Fact]
    public void Set_address_takes_effect_after_status_stage()
    {
        var pad = Pad();
        Assert.Equal(UsbResponse.Ack, pad.Setup(Setup(0x00, 5, 7, 0, 0)));
        Assert.Equal(0, pad.Address);
        Assert.Equal(UsbResponse.Ack, pad.TransferIn(0, 8, out _));
        Assert.Equal(7, pad.Address);
    }

    [Fact]
    public void Xid_descriptor_reports_controller_type_and_report_sizes()
    {
        var d = ControlIn(Pad(), Setup(0xC1, 6, 0x4200, 0, 16));
        Assert.Equal(16, d.Length);
        Assert.Equal((byte)0x01, d[4]); // type: game controller
        Assert.Equal((byte)0x02, d[5]); // subtype: Duke
        Assert.Equal((byte)20, d[6]);   // input report size
        Assert.Equal((byte)6, d[7]);    // output report size
    }

    [Fact]
    public void Interrupt_in_stalls_until_configured_then_sends_only_changes()
    {
        var pad = Pad();
        Assert.Equal(UsbResponse.Stall, pad.TransferIn(1, 32, out _));
        Assert.Equal(UsbResponse.Ack, pad.Setup(Setup(0x00, 9, 1, 0, 0)));
        Assert.True(pad.Configured);

        Assert.Equal(UsbResponse.Ack, pad.TransferIn(1, 32, out var first));
        Assert.Equal(20, first.Length);
        Assert.Equal(UsbResponse.Nak, pad.TransferIn(1, 32, out _));

        _state = _state with { A = 200, LeftX = -1234 };
        Assert.Equal(UsbResponse.Ack, pad.TransferIn(1, 32, out var changed));
        Assert.Equal(_state.ToXidReport(), changed);
        Assert.Equal(2, pad.ReportsSent);
        Assert.Equal(UsbResponse.Nak, pad.TransferIn(1, 32, out _));
    }

    [Fact]
    public void Rumble_arrives_on_interrupt_out_and_on_set_report()
    {
        var pad = Pad();
        pad.Setup(Setup(0x00, 9, 1, 0, 0));
        Assert.Equal(UsbResponse.Ack, pad.TransferOut(2, [0, 6, 0x34, 0x12, 0x78, 0x56]));
        Assert.Equal(new RumbleState(0x1234, 0x5678), pad.LastRumble);

        Assert.Equal(UsbResponse.Ack, pad.Setup(Setup(0x21, 9, 0x0200, 0, 6)));
        Assert.Equal(UsbResponse.Ack, pad.TransferOut(0, [0, 6, 0xFF, 0x00, 0x00, 0x01]));
        Assert.Equal([new RumbleState(0x1234, 0x5678), new RumbleState(0x00FF, 0x0100)], _rumble);
    }

    [Fact]
    public void Unknown_requests_stall_and_reset_clears_configuration()
    {
        var pad = Pad();
        Assert.Equal(UsbResponse.Stall, pad.Setup(Setup(0x40, 0x99, 0, 0, 0)));
        pad.Setup(Setup(0x00, 9, 1, 0, 0));
        pad.Reset();
        Assert.False(pad.Configured);
        Assert.Equal(0, pad.Address);
    }
}
