using System.Buffers.Binary;

namespace XboxPc.Emulation.Input;

/// <summary>Digital buttons of the Original Xbox controller (XID dButtons bit layout).</summary>
[Flags]
public enum XboxDigital : ushort
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
}

/// <summary>Every bindable control of the virtual Xbox controller.</summary>
public enum XboxControl
{
    A, B, X, Y, Black, White, LeftTrigger, RightTrigger,
    DPadUp, DPadDown, DPadLeft, DPadRight, Start, Back, LeftThumb, RightThumb,
    LeftStickUp, LeftStickDown, LeftStickLeft, LeftStickRight,
    RightStickUp, RightStickDown, RightStickLeft, RightStickRight,
}

/// <summary>
/// State of one virtual Original Xbox controller ("Duke"): digital buttons, eight pressure-sensitive buttons
/// (A, B, X, Y, Black, White, L, R; 0-255) and two sticks (-32768..32767, +Y = up). Serialises to the 20-byte XID
/// input report (layout as in nxdk's xid_gamepad_in).
/// </summary>
public readonly record struct XboxGamepadState(
    XboxDigital Digital,
    byte A, byte B, byte X, byte Y, byte Black, byte White, byte LeftTrigger, byte RightTrigger,
    short LeftX, short LeftY, short RightX, short RightY)
{
    public const int ReportLength = 20;

    public static XboxGamepadState Neutral => default;

    public byte Analog(XboxControl c) => c switch
    {
        XboxControl.A => A, XboxControl.B => B, XboxControl.X => X, XboxControl.Y => Y,
        XboxControl.Black => Black, XboxControl.White => White,
        XboxControl.LeftTrigger => LeftTrigger, XboxControl.RightTrigger => RightTrigger,
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    public byte[] ToXidReport()
    {
        var r = new byte[ReportLength];
        r[0] = 0x00;
        r[1] = ReportLength;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(2), (ushort)Digital);
        r[4] = A; r[5] = B; r[6] = X; r[7] = Y; r[8] = Black; r[9] = White; r[10] = LeftTrigger; r[11] = RightTrigger;
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(12), LeftX);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(14), LeftY);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(16), RightX);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(18), RightY);
        return r;
    }

    public static XboxGamepadState FromXidReport(ReadOnlySpan<byte> r)
    {
        if (r.Length < ReportLength || r[1] != ReportLength) throw new ArgumentException("Not a 20-byte XID gamepad report.", nameof(r));
        return new XboxGamepadState((XboxDigital)BinaryPrimitives.ReadUInt16LittleEndian(r[2..]), r[4], r[5], r[6], r[7], r[8], r[9], r[10], r[11],
            BinaryPrimitives.ReadInt16LittleEndian(r[12..]), BinaryPrimitives.ReadInt16LittleEndian(r[14..]),
            BinaryPrimitives.ReadInt16LittleEndian(r[16..]), BinaryPrimitives.ReadInt16LittleEndian(r[18..]));
    }
}

/// <summary>Rumble request from the game (XID output report): left = low-frequency motor, right = high-frequency.</summary>
public readonly record struct RumbleState(ushort Left, ushort Right);
