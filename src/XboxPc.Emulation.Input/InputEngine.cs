namespace XboxPc.Emulation.Input;

/// <summary>Who receives host input: the dashboard (menus) or the running emulated game. Never both.</summary>
public enum InputOwner
{
    Dashboard,
    Emulator,
}

/// <summary>
/// Separates dashboard navigation from game input. While an emulation session window is active and focused, the
/// emulator owns input; dashboard controller navigation must check <see cref="DashboardMayHandle"/> and stay idle.
/// </summary>
public sealed class InputArbiter
{
    public InputOwner Owner { get; private set; } = InputOwner.Dashboard;

    public bool DashboardMayHandle => Owner == InputOwner.Dashboard;

    public event EventHandler<InputOwner>? OwnerChanged;

    public void SetEmulatorFocused(bool focused)
    {
        var next = focused ? InputOwner.Emulator : InputOwner.Dashboard;
        if (next == Owner) return;
        Owner = next;
        OwnerChanged?.Invoke(this, next);
    }
}

/// <summary>What the host should do with a key event.</summary>
public enum KeyDisposition
{
    /// <summary>Not for the emulator (window not focused / capture not active): let the application handle it.</summary>
    Application,

    /// <summary>An emulator shortcut (reserved key or capture release): handled by the emulator UI, never by the game.</summary>
    Shortcut,

    /// <summary>Delivered to the game through the virtual controller.</summary>
    Game,
}

/// <summary>
/// The input engine: translates host devices into four virtual Xbox controllers according to the active profile,
/// and owns the focus/capture rules.
/// Rules: nothing reaches the game unless the emulator window is focused. Keyboard keys reach the game while focused;
/// mouse motion and mouse buttons only while the mouse is captured (click in the window to capture, the profile's
/// release key or focus loss to release). Losing focus releases capture and clears all held keyboard/mouse state so
/// nothing stays stuck. Controller mode ignores keyboard/mouse; Keyboard+Mouse mode ignores controllers; Hybrid merges
/// controller 1 with keyboard/mouse on port 1.
/// </summary>
public sealed class InputEngine
{
    public const int PortCount = 4;

    private readonly XboxGamepadState[] _ports = new XboxGamepadState[PortCount];
    private readonly bool[] _connected = new bool[PortCount];
    private readonly RumbleState[] _rumble = new RumbleState[PortCount];
    private readonly MouseStickProcessor _mouse = new();
    private InputProfile _profile = InputProfile.CreateDefault();

    public InputProfile Profile
    {
        get => _profile;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var errors = value.Validate();
            if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(value));
            _profile = value;
            if (value.Mode == InputMode.Controller) ReleaseCapture();
        }
    }

    public bool WindowFocused { get; private set; }
    public bool MouseCaptured { get; private set; }

    /// <summary>Raised when capture changes; the host shows/hides and confines the cursor accordingly.</summary>
    public event EventHandler<bool>? CaptureChanged;

    /// <summary>Raised when a port's virtual controller is plugged in or removed (true = connected).</summary>
    public event EventHandler<(int Port, bool Connected)>? PortConnectionChanged;

    public XboxGamepadState Port(int port) => _ports[port];

    public bool IsPortConnected(int port) => _connected[port];

    public RumbleState Rumble(int port) => _rumble[port];

    /// <summary>Called by the emulated controller when the game sends a rumble report.</summary>
    public void SetRumble(int port, RumbleState rumble) => _rumble[port] = _profile.Controller.Rumble ? rumble : default;

    public void OnFocusChanged(bool focused)
    {
        WindowFocused = focused;
        if (!focused)
        {
            ReleaseCapture();
            Array.Fill(_ports, XboxGamepadState.Neutral);
        }
    }

    /// <summary>Click inside the emulator window. Captures only when focused and keyboard/mouse is in use.</summary>
    public void RequestCapture()
    {
        if (!WindowFocused || _profile.Mode == InputMode.Controller || MouseCaptured) return;
        MouseCaptured = true;
        _mouse.Reset();
        CaptureChanged?.Invoke(this, true);
    }

    public void ReleaseCapture()
    {
        if (!MouseCaptured) return;
        MouseCaptured = false;
        _mouse.Reset();
        CaptureChanged?.Invoke(this, false);
    }

    /// <summary>Classifies a key press. The release key is consumed here and releases capture.</summary>
    public KeyDisposition OnKeyDown(string key)
    {
        if (!WindowFocused) return KeyDisposition.Application;
        if (key.Equals(_profile.ReleaseCaptureKey, StringComparison.OrdinalIgnoreCase))
        {
            ReleaseCapture();
            return KeyDisposition.Shortcut;
        }
        if (InputProfile.ReservedKeys.Contains(key)) return KeyDisposition.Shortcut;
        return _profile.Mode == InputMode.Controller ? KeyDisposition.Application : KeyDisposition.Game;
    }

    /// <summary>Recomputes all four virtual controllers from the host frame.</summary>
    public void Update(HostInputFrame frame, double dtSeconds, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var mode = _profile.Mode;
        var useKbm = mode != InputMode.Controller;
        var usePads = mode != InputMode.KeyboardMouse;

        for (var port = 0; port < PortCount; port++)
        {
            var pad = usePads ? frame.Pads[port] : null;
            var connected = (port == 0 && useKbm) || pad is not null;
            if (connected != _connected[port])
            {
                _connected[port] = connected;
                PortConnectionChanged?.Invoke(this, (port, connected));
            }
            if (!WindowFocused || !connected) { _ports[port] = XboxGamepadState.Neutral; continue; }

            var state = pad is { } p ? InputMapper.MapController(p, _profile.Controller) : XboxGamepadState.Neutral;
            if (port == 0 && useKbm)
            {
                var held = new HashSet<string>(frame.Keys.Where(k => !InputProfile.ReservedKeys.Contains(k) && !k.Equals(_profile.ReleaseCaptureKey, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
                (double, double) stick = (0, 0);
                if (MouseCaptured)
                {
                    held.UnionWith(frame.MouseButtons);
                    if (frame.WheelDelta > 0) held.Add("WheelUp");
                    if (frame.WheelDelta < 0) held.Add("WheelDown");
                    stick = _mouse.Update(_profile.Mouse, frame.MouseDeltaX, frame.MouseDeltaY, dtSeconds, nowMs);
                }
                var kbm = InputMapper.MapKeyboardMouse(_profile, held, stick);
                state = pad is null ? kbm : InputMapper.Merge(state, kbm);
            }
            _ports[port] = state;
        }
    }
}
