using System.Runtime.InteropServices;
using XboxPc.Domain;

namespace XboxPc.Input;

[Flags]
public enum PadButtons : ushort
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
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

public readonly record struct GamepadReading(uint PacketNumber, PadButtons Buttons, byte LeftTrigger, byte RightTrigger, short LeftX, short LeftY, short RightX, short RightY);

/// <summary>Source of controller state. Production uses XInput; tests supply their own implementation.</summary>
public interface IGamepadSource
{
    /// <summary>Human-readable name of the backing API, e.g. "XInput 1.4".</summary>
    string ApiName { get; }

    /// <summary>False if the API could not be loaded on this machine.</summary>
    bool IsApiAvailable { get; }

    int SlotCount { get; }

    /// <summary>Returns null if no controller is connected in <paramref name="slot"/>.</summary>
    GamepadReading? Read(int slot);
}

public sealed class XInputGamepadSource : IGamepadSource
{
    private const uint ErrorSuccess = 0;
    private bool _apiMissing;

    public string ApiName => "XInput 1.4 (xinput1_4.dll)";
    public bool IsApiAvailable => !_apiMissing;
    public int SlotCount => 4;

    public GamepadReading? Read(int slot)
    {
        if (_apiMissing || slot < 0 || slot >= SlotCount) return null;
        try
        {
            var result = NativeMethods.XInputGetState((uint)slot, out var state);
            if (result != ErrorSuccess) return null; // ERROR_DEVICE_NOT_CONNECTED or other failure
            var g = state.Gamepad;
            return new GamepadReading(state.dwPacketNumber, (PadButtons)g.wButtons, g.bLeftTrigger, g.bRightTrigger, g.sThumbLX, g.sThumbLY, g.sThumbRX, g.sThumbRY);
        }
        catch (DllNotFoundException) { _apiMissing = true; return null; }
        catch (EntryPointNotFoundException) { _apiMissing = true; return null; }
    }

    /// <summary>Sets the controller's motor speeds (0-65535; left = low-frequency, right = high-frequency). Returns false if not possible.</summary>
    public bool SetVibration(int slot, ushort left, ushort right)
    {
        if (_apiMissing || slot < 0 || slot >= SlotCount) return false;
        try
        {
            var v = new NativeMethods.XInputVibration { wLeftMotorSpeed = left, wRightMotorSpeed = right };
            return NativeMethods.XInputSetState((uint)slot, ref v) == ErrorSuccess;
        }
        catch (DllNotFoundException) { _apiMissing = true; return false; }
        catch (EntryPointNotFoundException) { _apiMissing = true; return false; }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct XInputGamepad
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct XInputState
        {
            public uint dwPacketNumber;
            public XInputGamepad Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct XInputVibration
        {
            public ushort wLeftMotorSpeed;
            public ushort wRightMotorSpeed;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint XInputSetState(uint dwUserIndex, ref XInputVibration pVibration);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint XInputGetState(uint dwUserIndex, out XInputState pState);
    }
}

public static class StickMath
{
    /// <summary>
    /// Radial dead zone with rescaling. Input in raw XInput units; output in -1..1 per axis.
    /// Values inside the dead zone become exactly (0,0).
    /// </summary>
    public static (double X, double Y) ApplyRadialDeadZone(short rawX, short rawY, double deadZone)
    {
        deadZone = Math.Clamp(double.IsFinite(deadZone) ? deadZone : 0, 0, 0.9);
        var x = Math.Max(-1.0, rawX / 32767.0);
        var y = Math.Max(-1.0, rawY / 32767.0);
        var magnitude = Math.Sqrt((x * x) + (y * y));
        if (magnitude <= deadZone || magnitude == 0) return (0, 0);
        var clamped = Math.Min(magnitude, 1.0);
        var scaled = (clamped - deadZone) / (1 - deadZone);
        return (x / magnitude * scaled, y / magnitude * scaled);
    }
}

public sealed record ControllerSlotStatus(int Slot, bool Connected, GamepadReading? Reading, double StickX, double StickY);

public sealed class ControllerEventArgs(int slot) : EventArgs
{
    public int Slot { get; } = slot;
}

public sealed class UiActionEventArgs(UiAction action, int slot, string binding, bool isRepeat) : EventArgs
{
    public UiAction Action { get; } = action;
    public int Slot { get; } = slot;
    public string Binding { get; } = binding;
    public bool IsRepeat { get; } = isRepeat;
}

/// <summary>
/// Polls controllers, reports connect/disconnect, and translates button presses and stick deflection into UI actions
/// using the active <see cref="InputMap"/>. This drives the application's own UI only; it never sends input to games.
/// </summary>
public sealed class ControllerService(IGamepadSource source)
{
    public static readonly TimeSpan BaseInitialRepeatDelay = TimeSpan.FromMilliseconds(450);
    public static readonly TimeSpan BaseRepeatInterval = TimeSpan.FromMilliseconds(140);

    private readonly PadButtons[] _previousButtons = new PadButtons[source.SlotCount];
    private readonly bool[] _connected = new bool[source.SlotCount];
    private readonly ControllerSlotStatus[] _status = Enumerable.Range(0, source.SlotCount).Select(i => new ControllerSlotStatus(i, false, null, 0, 0)).ToArray();
    private readonly Dictionary<(int Slot, string Binding), TimeSpan> _nextRepeat = [];

    public InputMap Map { get; set; } = InputMap.CreateDefault();

    public IGamepadSource Source => source;

    public event EventHandler<ControllerEventArgs>? Connected;
    public event EventHandler<ControllerEventArgs>? Disconnected;
    public event EventHandler<UiActionEventArgs>? ActionTriggered;

    public IReadOnlyList<ControllerSlotStatus> Slots => _status;

    /// <param name="now">Monotonic time, used for hold-to-repeat.</param>
    public void Poll(TimeSpan now)
    {
        for (var slot = 0; slot < source.SlotCount; slot++)
        {
            var reading = source.Read(slot);
            if (reading is null)
            {
                if (_connected[slot])
                {
                    _connected[slot] = false;
                    _previousButtons[slot] = PadButtons.None;
                    ClearRepeats(slot);
                    _status[slot] = new ControllerSlotStatus(slot, false, null, 0, 0);
                    Disconnected?.Invoke(this, new ControllerEventArgs(slot));
                }
                continue;
            }

            var r = reading.Value;
            var (sx, sy) = StickMath.ApplyRadialDeadZone(r.LeftX, r.LeftY, Map.LeftStickDeadZone);
            _status[slot] = new ControllerSlotStatus(slot, true, r, sx, sy); // update before events so handlers see current state
            if (!_connected[slot])
            {
                _connected[slot] = true;
                // Ignore buttons already held at connect time to avoid phantom presses.
                _previousButtons[slot] = r.Buttons | StickToDirection(sx, sy);
                Connected?.Invoke(this, new ControllerEventArgs(slot));
            }

            var held = r.Buttons | StickToDirection(sx, sy);
            var pressed = held & ~_previousButtons[slot];
            foreach (var button in Enum.GetValues<PadButtons>())
            {
                if (button == PadButtons.None) continue;
                var binding = "Pad." + button;
                if ((pressed & button) != 0)
                {
                    Fire(slot, binding, isRepeat: false);
                    if (IsDirectional(button)) _nextRepeat[(slot, binding)] = now + Scale(BaseInitialRepeatDelay);
                }
                else if ((held & button) != 0 && IsDirectional(button) && _nextRepeat.TryGetValue((slot, binding), out var due) && now >= due)
                {
                    Fire(slot, binding, isRepeat: true);
                    _nextRepeat[(slot, binding)] = now + Scale(BaseRepeatInterval);
                }
                else if ((held & button) == 0)
                {
                    _nextRepeat.Remove((slot, binding));
                }
            }
            _previousButtons[slot] = held;
        }
    }

    private TimeSpan Scale(TimeSpan baseValue) => TimeSpan.FromTicks((long)(baseValue.Ticks / Math.Clamp(Map.StickSensitivity, 0.25, 3)));

    private void Fire(int slot, string binding, bool isRepeat)
    {
        if (Map.Resolve(binding) is { } action) ActionTriggered?.Invoke(this, new UiActionEventArgs(action, slot, binding, isRepeat));
    }

    private void ClearRepeats(int slot)
    {
        foreach (var key in _nextRepeat.Keys.Where(k => k.Slot == slot).ToList()) _nextRepeat.Remove(key);
    }

    private static bool IsDirectional(PadButtons b) => b is PadButtons.DPadUp or PadButtons.DPadDown or PadButtons.DPadLeft or PadButtons.DPadRight;

    /// <summary>Stick deflection beyond half travel acts as the D-pad direction of the dominant axis.</summary>
    public static PadButtons StickToDirection(double x, double y)
    {
        const double threshold = 0.5;
        if (Math.Abs(x) < threshold && Math.Abs(y) < threshold) return PadButtons.None;
        if (Math.Abs(y) >= Math.Abs(x)) return y > 0 ? PadButtons.DPadUp : PadButtons.DPadDown;
        return x > 0 ? PadButtons.DPadRight : PadButtons.DPadLeft;
    }
}
