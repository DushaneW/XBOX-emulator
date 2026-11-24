namespace XboxPc.Emulation.Input;

/// <summary>Buttons of a host (XInput-class) controller.</summary>
[Flags]
public enum HostPadButtons : ushort
{
    None = 0,
    DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008,
    Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080,
    LeftShoulder = 0x0100, RightShoulder = 0x0200,
    A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
}

/// <summary>One reading of a physical controller (sticks −32768..32767, +Y = up; triggers 0..255).</summary>
public readonly record struct HostPad(HostPadButtons Buttons, byte LeftTrigger, byte RightTrigger, short LeftX, short LeftY, short RightX, short RightY);

/// <summary>Everything the host reported since the previous engine update.</summary>
public sealed class HostInputFrame
{
    /// <summary>Keyboard keys currently held (host key names, e.g. "W", "LeftShift").</summary>
    public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mouse buttons currently held ("MouseLeft", "MouseRight", "MouseMiddle", "MouseX1", "MouseX2").</summary>
    public HashSet<string> MouseButtons { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Relative mouse motion in pixels since the previous update.</summary>
    public double MouseDeltaX { get; set; }

    public double MouseDeltaY { get; set; }

    /// <summary>Wheel notches since the previous update (positive = away from the user).</summary>
    public int WheelDelta { get; set; }

    /// <summary>Physical controllers by slot; null = not connected.</summary>
    public HostPad?[] Pads { get; } = new HostPad?[4];
}

/// <summary>Pure translation functions; every rule is unit-tested.</summary>
public static class InputMapper
{
    /// <summary>Radial dead zone with re-scaling to the full range. Inside the dead zone -> (0, 0).</summary>
    public static (short X, short Y) ApplyStickDeadZone(short x, short y, double deadZone)
    {
        deadZone = Math.Clamp(double.IsFinite(deadZone) ? deadZone : 0, 0, 0.9);
        double fx = Math.Max(-1, x / 32767.0), fy = Math.Max(-1, y / 32767.0);
        var magnitude = Math.Sqrt((fx * fx) + (fy * fy));
        if (magnitude <= deadZone || magnitude == 0) return (0, 0);
        var scaled = (Math.Min(magnitude, 1) - deadZone) / (1 - deadZone);
        return (ToAxis(fx / magnitude * scaled), ToAxis(fy / magnitude * scaled));
    }

    public static short ToAxis(double v) => (short)Math.Round(Math.Clamp(v, -1, 1) * 32767);

    public static byte ApplyTriggerThreshold(byte value, int threshold) => value <= threshold ? (byte)0 : value;

    /// <summary>Physical controller -> virtual Xbox controller.</summary>
    public static XboxGamepadState MapController(HostPad pad, ControllerSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var b = pad.Buttons;
        XboxDigital d = 0;
        if (b.HasFlag(HostPadButtons.DPadUp)) d |= XboxDigital.DPadUp;
        if (b.HasFlag(HostPadButtons.DPadDown)) d |= XboxDigital.DPadDown;
        if (b.HasFlag(HostPadButtons.DPadLeft)) d |= XboxDigital.DPadLeft;
        if (b.HasFlag(HostPadButtons.DPadRight)) d |= XboxDigital.DPadRight;
        if (b.HasFlag(HostPadButtons.Start)) d |= XboxDigital.Start;
        if (b.HasFlag(HostPadButtons.Back)) d |= XboxDigital.Back;
        if (b.HasFlag(HostPadButtons.LeftThumb)) d |= XboxDigital.LeftThumb;
        if (b.HasFlag(HostPadButtons.RightThumb)) d |= XboxDigital.RightThumb;
        static byte Full(bool on) => on ? (byte)255 : (byte)0;
        var lb = b.HasFlag(HostPadButtons.LeftShoulder);
        var rb = b.HasFlag(HostPadButtons.RightShoulder);
        var black = (s.LeftShoulderAs == XboxControl.Black && lb) || (s.RightShoulderAs == XboxControl.Black && rb);
        var white = (s.LeftShoulderAs == XboxControl.White && lb) || (s.RightShoulderAs == XboxControl.White && rb);
        var (lx, ly) = ApplyStickDeadZone(pad.LeftX, pad.LeftY, s.StickDeadZone);
        var (rx, ry) = ApplyStickDeadZone(pad.RightX, pad.RightY, s.StickDeadZone);
        return new XboxGamepadState(d,
            Full(b.HasFlag(HostPadButtons.A)), Full(b.HasFlag(HostPadButtons.B)), Full(b.HasFlag(HostPadButtons.X)), Full(b.HasFlag(HostPadButtons.Y)),
            Full(black), Full(white),
            ApplyTriggerThreshold(pad.LeftTrigger, s.TriggerThreshold), ApplyTriggerThreshold(pad.RightTrigger, s.TriggerThreshold),
            lx, ly, rx, ry);
    }

    /// <summary>
    /// Keyboard + mouse buttons (+ an already computed mouse stick) -> virtual Xbox controller. Digital inputs press
    /// analog buttons fully (255). Opposite stick directions cancel; diagonals are normalised to unit length when enabled.
    /// </summary>
    public static XboxGamepadState MapKeyboardMouse(InputProfile profile, IReadOnlySet<string> held, (double X, double Y) mouseStick)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(held);
        bool Down(XboxControl c) => profile.Bindings.TryGetValue(c, out var keys) && keys.Any(held.Contains);
        static byte Full(bool on) => on ? (byte)255 : (byte)0;
        XboxDigital d = 0;
        if (Down(XboxControl.DPadUp)) d |= XboxDigital.DPadUp;
        if (Down(XboxControl.DPadDown)) d |= XboxDigital.DPadDown;
        if (Down(XboxControl.DPadLeft)) d |= XboxDigital.DPadLeft;
        if (Down(XboxControl.DPadRight)) d |= XboxDigital.DPadRight;
        if (Down(XboxControl.Start)) d |= XboxDigital.Start;
        if (Down(XboxControl.Back)) d |= XboxDigital.Back;
        if (Down(XboxControl.LeftThumb)) d |= XboxDigital.LeftThumb;
        if (Down(XboxControl.RightThumb)) d |= XboxDigital.RightThumb;

        (double X, double Y) Keys(XboxControl up, XboxControl down, XboxControl left, XboxControl right)
        {
            double x = (Down(right) ? 1 : 0) - (Down(left) ? 1 : 0);
            double y = (Down(up) ? 1 : 0) - (Down(down) ? 1 : 0);
            if (profile.NormalizeKeyboardDiagonals && x != 0 && y != 0) { x *= Math.Sqrt(0.5); y *= Math.Sqrt(0.5); }
            return (x, y);
        }
        var left = Keys(XboxControl.LeftStickUp, XboxControl.LeftStickDown, XboxControl.LeftStickLeft, XboxControl.LeftStickRight);
        var right = Keys(XboxControl.RightStickUp, XboxControl.RightStickDown, XboxControl.RightStickLeft, XboxControl.RightStickRight);
        // The mouse adds to the stick it targets; keyboard and mouse together are clamped to the stick's range.
        if (profile.Mouse.Enabled)
        {
            if (profile.Mouse.Target == StickTarget.RightStick) right = (right.X + mouseStick.X, right.Y + mouseStick.Y);
            else left = (left.X + mouseStick.X, left.Y + mouseStick.Y);
        }
        return new XboxGamepadState(d,
            Full(Down(XboxControl.A)), Full(Down(XboxControl.B)), Full(Down(XboxControl.X)), Full(Down(XboxControl.Y)),
            Full(Down(XboxControl.Black)), Full(Down(XboxControl.White)),
            Full(Down(XboxControl.LeftTrigger)), Full(Down(XboxControl.RightTrigger)),
            ToAxis(left.X), ToAxis(left.Y), ToAxis(right.X), ToAxis(right.Y));
    }

    /// <summary>
    /// Hybrid merge: buttons combine (OR / max pressure); each stick takes whichever source is deflected further, so
    /// the two devices never fight by summing.
    /// </summary>
    public static XboxGamepadState Merge(XboxGamepadState a, XboxGamepadState b)
    {
        static (short, short) Stick(short ax, short ay, short bx, short by) =>
            ((long)ax * ax) + ((long)ay * ay) >= ((long)bx * bx) + ((long)by * by) ? (ax, ay) : (bx, by);
        var (lx, ly) = Stick(a.LeftX, a.LeftY, b.LeftX, b.LeftY);
        var (rx, ry) = Stick(a.RightX, a.RightY, b.RightX, b.RightY);
        return new XboxGamepadState(a.Digital | b.Digital,
            Math.Max(a.A, b.A), Math.Max(a.B, b.B), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y),
            Math.Max(a.Black, b.Black), Math.Max(a.White, b.White), Math.Max(a.LeftTrigger, b.LeftTrigger), Math.Max(a.RightTrigger, b.RightTrigger),
            lx, ly, rx, ry);
    }
}

/// <summary>
/// Turns relative mouse motion into a stick deflection. Speed (px/s) x sensitivity / 2000 gives the raw deflection,
/// then: dead zone -> curve -> clamp -> inversion. Screen Y grows downwards, stick Y grows upwards, so Y is negated
/// before the optional inversion. When motion stops, the last deflection is held for HoldMilliseconds (smooths gaps
/// between mouse reports) and then returns to centre.
/// </summary>
public sealed class MouseStickProcessor
{
    private (double X, double Y) _last;
    private long _lastMotionMs = long.MinValue / 2;

    public (double X, double Y) Update(MouseStickSettings s, double dx, double dy, double dtSeconds, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (dx == 0 && dy == 0)
        {
            if (nowMs - _lastMotionMs > s.HoldMilliseconds) _last = (0, 0);
            return _last;
        }
        if (dtSeconds <= 0) dtSeconds = 1.0 / 1000;
        _lastMotionMs = nowMs;
        double vx = dx / dtSeconds * s.Sensitivity / MouseStickSettings.FullDeflectionSpeed;
        double vy = -dy / dtSeconds * s.Sensitivity / MouseStickSettings.FullDeflectionSpeed;
        var magnitude = Math.Sqrt((vx * vx) + (vy * vy));
        var shaped = Math.Pow(Math.Clamp(magnitude, 0, 1), s.Curve);
        // Output dead zone: any motion maps above it, so small movements still overcome the game's own dead zone.
        var outMag = s.DeadZone + ((1 - s.DeadZone) * shaped);
        double x = vx / magnitude * outMag, y = vy / magnitude * outMag;
        if (s.InvertX) x = -x;
        if (s.InvertY) y = -y;
        _last = (Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1));
        return _last;
    }

    public void Reset()
    {
        _last = (0, 0);
        _lastMotionMs = long.MinValue / 2;
    }
}
