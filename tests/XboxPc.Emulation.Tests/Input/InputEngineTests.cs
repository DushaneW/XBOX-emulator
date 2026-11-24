using XboxPc.Emulation.Input;

namespace XboxPc.Emulation.Tests.Input;

public class XidReportTests
{
    [Fact]
    public void Report_round_trips_and_matches_nxdk_xid_gamepad_in_layout()
    {
        var s = new XboxGamepadState(XboxDigital.Start | XboxDigital.DPadLeft, 1, 2, 3, 4, 5, 6, 7, 8, -32768, 32767, 100, -100);
        var r = s.ToXidReport();
        Assert.Equal(20, r.Length);
        Assert.Equal(0, r[0]);
        Assert.Equal(20, r[1]);
        Assert.Equal(0x14, r[2]);            // DPadLeft (0x04) | Start (0x10)
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], r[4..12]);
        Assert.Equal([0x00, 0x80, 0xFF, 0x7F], r[12..16]);
        Assert.Equal(s, XboxGamepadState.FromXidReport(r));
        Assert.Throws<ArgumentException>(() => XboxGamepadState.FromXidReport(new byte[19]));
    }
}

public class ControllerMappingTests
{
    private static readonly ControllerSettings Defaults = new();

    public static TheoryData<HostPadButtons, string> Buttons() => new()
    {
        { HostPadButtons.A, "A" }, { HostPadButtons.B, "B" }, { HostPadButtons.X, "X" }, { HostPadButtons.Y, "Y" },
        { HostPadButtons.LeftShoulder, "White" }, { HostPadButtons.RightShoulder, "Black" },
        { HostPadButtons.DPadUp, "DPadUp" }, { HostPadButtons.DPadDown, "DPadDown" }, { HostPadButtons.DPadLeft, "DPadLeft" }, { HostPadButtons.DPadRight, "DPadRight" },
        { HostPadButtons.Start, "Start" }, { HostPadButtons.Back, "Back" }, { HostPadButtons.LeftThumb, "LeftThumb" }, { HostPadButtons.RightThumb, "RightThumb" },
    };

    [Theory]
    [MemberData(nameof(Buttons))]
    public void Every_host_button_maps_to_exactly_one_xbox_control(HostPadButtons button, string expected)
    {
        var s = InputMapper.MapController(new HostPad(button, 0, 0, 0, 0, 0, 0), Defaults);
        var pressed = new List<string>();
        foreach (var d in Enum.GetValues<XboxDigital>().Where(d => d != 0)) if (s.Digital.HasFlag(d)) pressed.Add(d.ToString());
        foreach (var c in new[] { XboxControl.A, XboxControl.B, XboxControl.X, XboxControl.Y, XboxControl.Black, XboxControl.White, XboxControl.LeftTrigger, XboxControl.RightTrigger })
            if (s.Analog(c) > 0) { Assert.Equal(255, s.Analog(c)); pressed.Add(c.ToString()); }
        Assert.Equal([expected], pressed);
    }

    [Fact]
    public void Shoulder_mapping_is_configurable()
    {
        var s = InputMapper.MapController(new HostPad(HostPadButtons.LeftShoulder, 0, 0, 0, 0, 0, 0), Defaults with { LeftShoulderAs = XboxControl.Black, RightShoulderAs = XboxControl.White });
        Assert.Equal(255, s.Black);
        Assert.Equal(0, s.White);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 0)]
    [InlineData(9, 9)]
    [InlineData(255, 255)]
    public void Trigger_threshold(byte raw, byte expected)
    {
        var s = InputMapper.MapController(new HostPad(0, raw, raw, 0, 0, 0, 0), Defaults);
        Assert.Equal(expected, s.LeftTrigger);
        Assert.Equal(expected, s.RightTrigger);
    }

    [Fact]
    public void Stick_dead_zone_and_rescaling()
    {
        Assert.Equal((0, 0), InputMapper.ApplyStickDeadZone(4000, 0, 0.15));          // 0.12 < 0.15
        Assert.Equal((32767, 0), InputMapper.ApplyStickDeadZone(32767, 0, 0.15));     // full stays full
        Assert.Equal((-32767, 0), InputMapper.ApplyStickDeadZone(short.MinValue, 0, 0.15));
        var (x, _) = InputMapper.ApplyStickDeadZone((short)(32767 * 0.575), 0, 0.15);   // halfway between dz and 1
        Assert.InRange(x, 16300, 16450);
        var (dx, dy) = InputMapper.ApplyStickDeadZone(20000, 20000, 0.15);            // direction preserved
        Assert.Equal(dx, dy);
    }
}

public class KeyboardMouseMappingTests
{
    private static readonly InputProfile Profile = InputProfile.CreateDefault();

    public static TheoryData<XboxControl> AllControls()
    {
        var d = new TheoryData<XboxControl>();
        foreach (var c in Enum.GetValues<XboxControl>()) d.Add(c);
        return d;
    }

    [Theory]
    [MemberData(nameof(AllControls))]
    public void Every_control_is_reachable_from_its_default_binding(XboxControl control)
    {
        var key = Profile.Bindings[control][0];
        var s = InputMapper.MapKeyboardMouse(Profile, new HashSet<string> { key }, (0, 0));
        switch (control)
        {
            case XboxControl.LeftStickUp: Assert.Equal(32767, s.LeftY); break;
            case XboxControl.LeftStickDown: Assert.Equal(-32767, s.LeftY); break;
            case XboxControl.LeftStickLeft: Assert.Equal(-32767, s.LeftX); break;
            case XboxControl.LeftStickRight: Assert.Equal(32767, s.LeftX); break;
            case XboxControl.RightStickUp: Assert.Equal(32767, s.RightY); break;
            case XboxControl.RightStickDown: Assert.Equal(-32767, s.RightY); break;
            case XboxControl.RightStickLeft: Assert.Equal(-32767, s.RightX); break;
            case XboxControl.RightStickRight: Assert.Equal(32767, s.RightX); break;
            case XboxControl.A or XboxControl.B or XboxControl.X or XboxControl.Y or XboxControl.Black or XboxControl.White or XboxControl.LeftTrigger or XboxControl.RightTrigger:
                Assert.Equal(255, s.Analog(control));
                break;
            default:
                Assert.True(s.Digital.HasFlag(Enum.Parse<XboxDigital>(control.ToString())), control.ToString());
                break;
        }
    }

    [Fact]
    public void Opposite_directions_cancel_and_diagonals_normalise()
    {
        Assert.Equal(0, InputMapper.MapKeyboardMouse(Profile, new HashSet<string> { "A", "D" }, (0, 0)).LeftX);
        var diag = InputMapper.MapKeyboardMouse(Profile, new HashSet<string> { "W", "D" }, (0, 0));
        Assert.Equal(diag.LeftX, diag.LeftY);
        Assert.InRange(diag.LeftX, 23160, 23175); // 32767 · √½
        var square = InputMapper.MapKeyboardMouse(Profile with { NormalizeKeyboardDiagonals = false }, new HashSet<string> { "W", "D" }, (0, 0));
        Assert.Equal(32767, square.LeftX);
    }

    [Fact]
    public void Rebinding_changes_mapping()
    {
        var bindings = Profile.Bindings.ToDictionary(k => k.Key, v => v.Value.ToList());
        bindings[XboxControl.A] = ["J"];
        bindings[XboxControl.RightStickLeft] = ["Z"];
        var p = Profile with { Bindings = bindings };
        Assert.Empty(p.Validate());
        Assert.Equal(255, InputMapper.MapKeyboardMouse(p, new HashSet<string> { "J" }, (0, 0)).A);
        Assert.Equal(0, InputMapper.MapKeyboardMouse(p, new HashSet<string> { "Space" }, (0, 0)).A);
    }

    [Fact]
    public void Mouse_stick_adds_to_target_and_clamps()
    {
        var s = InputMapper.MapKeyboardMouse(Profile, new HashSet<string> { "L" }, (0.8, 0)); // L = RightStickRight
        Assert.Equal(32767, s.RightX);
        var left = InputMapper.MapKeyboardMouse(Profile with { Mouse = Profile.Mouse with { Target = StickTarget.LeftStick } }, new HashSet<string>(), (-0.5, 0.25));
        Assert.Equal(InputMapper.ToAxis(-0.5), left.LeftX);
        Assert.Equal(0, left.RightX);
    }

    [Fact]
    public void Hybrid_merge_ors_buttons_and_takes_larger_stick()
    {
        var pad = new XboxGamepadState(XboxDigital.Start, 255, 0, 0, 0, 0, 0, 100, 0, 1000, 0, 0, 0);
        var kbm = new XboxGamepadState(XboxDigital.Back, 0, 255, 0, 0, 0, 0, 255, 0, 0, 20000, 0, 0);
        var m = InputMapper.Merge(pad, kbm);
        Assert.Equal(XboxDigital.Start | XboxDigital.Back, m.Digital);
        Assert.Equal((255, 255, 255), (m.A, m.B, m.LeftTrigger));
        Assert.Equal((0, 20000), (m.LeftX, m.LeftY));
    }
}

public class MouseStickTests
{
    private static readonly MouseStickSettings Linear = new() { Sensitivity = 1, Curve = 1, DeadZone = 0, HoldMilliseconds = 40 };

    [Fact]
    public void Speed_maps_linearly_to_deflection()
    {
        var m = new MouseStickProcessor();
        var (x, y) = m.Update(Linear, 10, 0, 0.01, 0); // 1000 px/s -> 0.5
        Assert.Equal(0.5, x, 6);
        Assert.Equal(0, y, 6);
    }

    [Fact]
    public void Sensitivity_scales_and_saturates()
    {
        var m = new MouseStickProcessor();
        Assert.Equal(1.0, m.Update(Linear with { Sensitivity = 2 }, 10, 0, 0.01, 0).X, 6);
        Assert.Equal(1.0, m.Update(Linear, 1000, 0, 0.01, 10).X, 6); // 100 000 px/s clamps
        Assert.Equal(0.25, m.Update(Linear with { Sensitivity = 0.5 }, 10, 0, 0.01, 20).X, 6);
    }

    [Fact]
    public void Screen_down_is_stick_down_and_inversion_flips()
    {
        var m = new MouseStickProcessor();
        Assert.True(m.Update(Linear, 0, 10, 0.01, 0).Y < 0);
        Assert.True(m.Update(Linear with { InvertY = true }, 0, 10, 0.01, 1).Y > 0);
        Assert.True(m.Update(Linear with { InvertX = true }, 10, 0, 0.01, 2).X < 0);
    }

    [Fact]
    public void Dead_zone_offsets_output_and_curve_shapes_it()
    {
        var m = new MouseStickProcessor();
        Assert.Equal(0.2 + (0.8 * 0.05), m.Update(Linear with { DeadZone = 0.2 }, 1, 0, 0.01, 0).X, 6); // 100 px/s -> 0.05 raw
        Assert.Equal(0.25, m.Update(Linear with { Curve = 2 }, 10, 0, 0.01, 1).X, 6);                    // 0.5²
    }

    [Fact]
    public void Deflection_is_held_briefly_then_recentres()
    {
        var m = new MouseStickProcessor();
        m.Update(Linear, 10, 0, 0.01, 1000);
        Assert.Equal(0.5, m.Update(Linear, 0, 0, 0.01, 1030).X, 6);  // within 40 ms hold
        Assert.Equal(0, m.Update(Linear, 0, 0, 0.01, 1050).X, 6);    // after hold
    }
}

public class InputEngineBehaviourTests
{
    private static HostInputFrame Frame(Action<HostInputFrame>? setup = null)
    {
        var f = new HostInputFrame();
        setup?.Invoke(f);
        return f;
    }

    [Fact]
    public void Nothing_reaches_the_game_without_focus()
    {
        var e = new InputEngine();
        e.Update(Frame(f => { f.Keys.Add("Space"); f.Pads[0] = new HostPad(HostPadButtons.A, 0, 0, 0, 0, 0, 0); }), 0.016, 0);
        Assert.Equal(XboxGamepadState.Neutral, e.Port(0));
        Assert.Equal(KeyDisposition.Application, e.OnKeyDown("Space"));
    }

    [Fact]
    public void Keyboard_works_without_any_physical_controller()
    {
        var e = new InputEngine { Profile = InputProfile.CreateDefault() with { Mode = InputMode.KeyboardMouse } };
        e.OnFocusChanged(true);
        e.Update(Frame(f => f.Keys.Add("Space")), 0.016, 0);
        Assert.True(e.IsPortConnected(0));
        Assert.Equal(255, e.Port(0).A);
        Assert.False(e.IsPortConnected(1));
    }

    [Fact]
    public void Mouse_only_counts_while_captured_and_release_key_releases()
    {
        var e = new InputEngine();
        e.OnFocusChanged(true);
        var captured = new List<bool>();
        e.CaptureChanged += (_, c) => captured.Add(c);
        e.Update(Frame(f => { f.MouseDeltaX = 10; f.MouseButtons.Add("MouseLeft"); }), 0.01, 0);
        Assert.Equal(0, e.Port(0).RightX);
        Assert.Equal(0, e.Port(0).RightTrigger);
        e.RequestCapture();
        e.Update(Frame(f => { f.MouseDeltaX = 10; f.MouseButtons.Add("MouseLeft"); }), 0.01, 100);
        Assert.Equal(InputMapper.ToAxis(0.5), e.Port(0).RightX);
        Assert.Equal(255, e.Port(0).RightTrigger);
        Assert.Equal(KeyDisposition.Shortcut, e.OnKeyDown("RightCtrl"));
        Assert.False(e.MouseCaptured);
        Assert.Equal([true, false], captured);
    }

    [Fact]
    public void Focus_loss_releases_capture_and_clears_state()
    {
        var e = new InputEngine();
        e.OnFocusChanged(true);
        e.RequestCapture();
        e.Update(Frame(f => f.Keys.Add("Space")), 0.016, 0);
        Assert.Equal(255, e.Port(0).A);
        e.OnFocusChanged(false);
        Assert.False(e.MouseCaptured);
        Assert.Equal(XboxGamepadState.Neutral, e.Port(0));
        e.RequestCapture(); // cannot capture while unfocused
        Assert.False(e.MouseCaptured);
    }

    [Fact]
    public void Reserved_and_release_keys_never_reach_the_game()
    {
        var e = new InputEngine();
        e.OnFocusChanged(true);
        Assert.Equal(KeyDisposition.Shortcut, e.OnKeyDown("F11"));
        Assert.Equal(KeyDisposition.Game, e.OnKeyDown("W"));
        var bindings = InputProfile.CreateDefault().Bindings.ToDictionary(k => k.Key, v => v.Value.ToList());
        bindings[XboxControl.A] = ["F12"];
        Assert.Contains(InputProfile.CreateDefault() with { Bindings = bindings } is var p ? p.Validate() : [], m => m.Contains("reserved", StringComparison.Ordinal));
        bindings[XboxControl.A] = ["RightCtrl"];
        Assert.Contains((InputProfile.CreateDefault() with { Bindings = bindings }).Validate(), m => m.Contains("releases mouse capture", StringComparison.Ordinal));
    }

    [Fact]
    public void Controller_mode_ignores_keyboard_and_uses_all_pads()
    {
        var e = new InputEngine { Profile = InputProfile.CreateDefault() with { Mode = InputMode.Controller } };
        e.OnFocusChanged(true);
        Assert.Equal(KeyDisposition.Application, e.OnKeyDown("Space"));
        e.RequestCapture();
        Assert.False(e.MouseCaptured);
        e.Update(Frame(f => { f.Keys.Add("Space"); f.Pads[2] = new HostPad(HostPadButtons.B, 0, 0, 0, 0, 0, 0); }), 0.016, 0);
        Assert.False(e.IsPortConnected(0));
        Assert.True(e.IsPortConnected(2));
        Assert.Equal(255, e.Port(2).B);
        Assert.Equal(0, e.Port(0).A);
    }

    [Fact]
    public void Hybrid_uses_pad_and_keyboard_on_port_one_simultaneously()
    {
        var e = new InputEngine();
        e.OnFocusChanged(true);
        e.Update(Frame(f => { f.Keys.Add("Space"); f.Pads[0] = new HostPad(HostPadButtons.B, 0, 0, 0, 0, 0, 0); }), 0.016, 0);
        Assert.Equal((255, 255), (e.Port(0).A, e.Port(0).B));
    }

    [Fact]
    public void Disconnect_and_reconnect_raise_port_events_and_neutralise()
    {
        var e = new InputEngine { Profile = InputProfile.CreateDefault() with { Mode = InputMode.Controller } };
        e.OnFocusChanged(true);
        var events = new List<(int, bool)>();
        e.PortConnectionChanged += (_, x) => events.Add(x);
        e.Update(Frame(f => f.Pads[1] = new HostPad(HostPadButtons.A, 0, 0, 0, 0, 0, 0)), 0.016, 0);
        e.Update(Frame(), 0.016, 16);
        Assert.Equal(XboxGamepadState.Neutral, e.Port(1));
        e.Update(Frame(f => f.Pads[1] = new HostPad(0, 0, 0, 0, 0, 0, 0)), 0.016, 32);
        Assert.Equal([(1, true), (1, false), (1, true)], events);
    }

    [Fact]
    public void Rumble_respects_profile()
    {
        var e = new InputEngine();
        e.SetRumble(0, new RumbleState(1000, 2000));
        Assert.Equal(new RumbleState(1000, 2000), e.Rumble(0));
        e.Profile = InputProfile.CreateDefault() with { Controller = new ControllerSettings { Rumble = false } };
        e.SetRumble(0, new RumbleState(1000, 2000));
        Assert.Equal(default, e.Rumble(0));
    }

    [Fact]
    public void Arbiter_gives_input_to_exactly_one_owner()
    {
        var a = new InputArbiter();
        Assert.True(a.DashboardMayHandle);
        var owners = new List<InputOwner>();
        a.OwnerChanged += (_, o) => owners.Add(o);
        a.SetEmulatorFocused(true);
        Assert.False(a.DashboardMayHandle);
        a.SetEmulatorFocused(true);
        a.SetEmulatorFocused(false);
        Assert.True(a.DashboardMayHandle);
        Assert.Equal([InputOwner.Emulator, InputOwner.Dashboard], owners);
    }

    [Fact]
    public void Invalid_profile_is_rejected_by_engine()
    {
        var e = new InputEngine();
        Assert.Throws<ArgumentException>(() => e.Profile = InputProfile.CreateDefault() with { Mouse = new MouseStickSettings { Sensitivity = 0 } });
    }
}

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xpc-profiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Save_load_list_delete_and_per_game_assignment()
    {
        var store = new ProfileStore(_dir);
        var fps = InputProfile.CreateDefault() with { Name = "FPS mouse", Mode = InputMode.KeyboardMouse, Mouse = new MouseStickSettings { Sensitivity = 3.5, InvertY = true, Curve = 1.4, DeadZone = 0.2 } };
        store.Save(fps);
        store.Save(InputProfile.CreateDefault());
        var loaded = store.Load("FPS mouse")!;
        Assert.Equal(InputMode.KeyboardMouse, loaded.Mode);
        Assert.Equal(fps.Mouse, loaded.Mouse);
        Assert.Equal(fps.Bindings[XboxControl.A], loaded.Bindings[XboxControl.A]);
        Assert.Equal(["Default", "FPS mouse"], store.List());

        store.AssignToGame(0xFFFF0002, "FPS mouse");
        Assert.Equal("FPS mouse", store.ProfileForGame(0xFFFF0002).Name);
        Assert.Equal("Default", store.ProfileForGame(0x12345678).Name);

        Assert.True(store.Delete("FPS mouse"));
        Assert.Equal("Default", store.ProfileForGame(0xFFFF0002).Name); // falls back when the assigned profile is gone
    }

    [Fact]
    public void Invalid_profiles_are_not_saved_and_corrupt_files_are_ignored()
    {
        var store = new ProfileStore(_dir);
        Assert.Throws<InvalidDataException>(() => store.Save(InputProfile.CreateDefault() with { Mouse = new MouseStickSettings { Curve = 9 } }));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Broken.profile.json"), "{ not json");
        Assert.Null(store.Load("Broken"));
        Assert.Empty(store.List());
    }
}
