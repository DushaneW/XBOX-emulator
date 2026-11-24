using XboxPc.DesktopApp.ViewModels;
using XboxPc.Emulation.Input;
using InputMode = XboxPc.Emulation.Input.InputMode;

namespace XboxPc.Tests.Unit;

/// <summary>Input configuration screen logic: rebinding, conflicts, reserved keys, saving and per-game assignment.</summary>
public class InputConfigViewModelTests
{
    private static (InputConfigViewModel Vm, ProfileStore Store, TempDir Dir) Create(uint? titleId = 0x4D530004)
    {
        var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        return (new InputConfigViewModel(store, InputProfile.CreateDefault(), titleId, "Test Title"), store, dir);
    }

    [Fact]
    public void Loads_every_control_with_profile_bindings()
    {
        var (vm, _, dir) = Create();
        using var _d = dir;
        Assert.Equal(Enum.GetValues<XboxControl>().Length, vm.Bindings.Count);
        Assert.Equal(["Space"], vm.Bindings.Single(b => b.Control == XboxControl.A).Keys);
        Assert.Equal(InputMode.Hybrid, vm.Mode);
        Assert.True(vm.IsHybrid);
    }

    [Fact]
    public void Mode_radio_properties_select_the_mode()
    {
        var (vm, _, dir) = Create();
        using var _d = dir;
        vm.IsKeyboardMouse = true;
        Assert.Equal(InputMode.KeyboardMouse, vm.Build().Mode);
        vm.IsController = true;
        Assert.Equal(InputMode.Controller, vm.Build().Mode);
    }

    [Fact]
    public void Rebinding_moves_a_key_away_from_its_previous_control()
    {
        var (vm, _, dir) = Create();
        using var _d = dir;
        var b = vm.Bindings.Single(r => r.Control == XboxControl.B);
        vm.BeginListening(b);
        Assert.True(vm.IsListening);
        Assert.True(vm.OfferKey("Space")); // currently A
        Assert.False(vm.IsListening);
        var p = vm.Build();
        Assert.Equal(["Space"], p.Bindings[XboxControl.B]);
        Assert.False(p.Bindings.ContainsKey(XboxControl.A));
        Assert.Empty(p.Validate());
    }

    [Theory]
    [InlineData("F11")]
    [InlineData("F12")]
    [InlineData("Pause")]
    [InlineData("RightCtrl")]
    public void Reserved_and_release_keys_are_refused(string key)
    {
        var (vm, _, dir) = Create();
        using var _d = dir;
        var a = vm.Bindings.Single(r => r.Control == XboxControl.A);
        vm.BeginListening(a);
        Assert.True(vm.OfferKey(key));
        Assert.True(vm.IsListening); // still waiting for a valid key
        Assert.Equal(["Space"], a.Keys);
    }

    [Fact]
    public void Escape_cancels_and_keys_are_ignored_when_not_listening()
    {
        var (vm, _, dir) = Create();
        using var _d = dir;
        Assert.False(vm.OfferKey("Q"));
        vm.BeginListening(vm.Bindings[0]);
        Assert.True(vm.OfferKey("Escape"));
        Assert.False(vm.IsListening);
        Assert.Equal(["Space"], vm.Bindings[0].Keys);
    }

    [Fact]
    public void Mouse_buttons_can_be_bound()
    {
        var (vm, _, dir) = Create();
        using var _d = dir;
        vm.BeginListening(vm.Bindings.Single(r => r.Control == XboxControl.X));
        vm.OfferKey("MouseX1");
        Assert.Equal(["MouseX1"], vm.Build().Bindings[XboxControl.X]);
    }

    [Fact]
    public void Save_validates_and_assigns_to_the_game()
    {
        var (vm, store, dir) = Create();
        using var _d = dir;
        vm.ProfileName = "Shooter";
        vm.Sensitivity = 2.5;
        vm.InvertY = true;
        vm.MouseDeadZone = 0.1;
        vm.Curve = 1.5;
        vm.IsKeyboardMouse = true;
        Assert.True(vm.Save());
        var loaded = store.ProfileForGame(0x4D530004);
        Assert.Equal("Shooter", loaded.Name);
        Assert.Equal(InputMode.KeyboardMouse, loaded.Mode);
        Assert.Equal(2.5, loaded.Mouse.Sensitivity);
        Assert.True(loaded.Mouse.InvertY);
        Assert.Equal(0.1, loaded.Mouse.DeadZone);
        Assert.Equal(1.5, loaded.Mouse.Curve);
        Assert.Equal("Default", store.ProfileForGame(0x12345678).Name); // other games keep the default
    }

    [Fact]
    public void Invalid_values_are_not_saved()
    {
        var (vm, store, dir) = Create();
        using var _d = dir;
        vm.ProfileName = "Broken";
        vm.Sensitivity = 0;
        Assert.False(vm.Save());
        Assert.Contains("Mouse sensitivity", vm.Message, StringComparison.Ordinal);
        Assert.Null(store.Load("Broken"));
    }

    [Fact]
    public void Without_a_game_the_profile_is_saved_but_not_assigned()
    {
        var (vm, store, dir) = Create(titleId: null);
        using var _d = dir;
        Assert.False(vm.HasGame);
        vm.ProfileName = "Global";
        Assert.True(vm.Save());
        Assert.NotNull(store.Load("Global"));
    }
}
