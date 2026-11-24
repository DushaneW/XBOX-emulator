using System.Collections.ObjectModel;
using System.Windows.Input;
using XboxPc.Emulation.Input;
using InputMode = XboxPc.Emulation.Input.InputMode;

namespace XboxPc.DesktopApp.ViewModels;

/// <summary>One Xbox control and the host keys/buttons bound to it.</summary>
public sealed class BindingRow(XboxControl control, IEnumerable<string> keys) : ObservableObject
{
    private string _keysText = string.Join(", ", keys);
    private bool _isListening;

    public XboxControl Control { get; } = control;
    public string Name { get; } = InputConfigViewModel.DisplayName(control);
    public List<string> Keys { get; } = [.. keys];

    public string KeysText { get => _keysText; private set => Set(ref _keysText, value); }
    public bool IsListening { get => _isListening; set => Set(ref _isListening, value); }

    public void SetKeys(IEnumerable<string> keys)
    {
        Keys.Clear();
        Keys.AddRange(keys);
        KeysText = Keys.Count == 0 ? "(unbound)" : string.Join(", ", Keys);
    }
}

/// <summary>
/// Input configuration screen: mode (Xbox Controller / Keyboard and Mouse / Hybrid), key and mouse-button bindings,
/// mouse-to-stick settings, physical controller settings, and saving as a named profile that can be assigned to the
/// current game (per-game profile, keyed by XBE title ID).
/// </summary>
public sealed class InputConfigViewModel : ObservableObject
{
    private readonly ProfileStore _store;
    private readonly uint? _titleId;
    private InputMode _mode;
    private string _profileName;
    private bool _mouseEnabled;
    private StickTarget _mouseTarget;
    private double _sensitivity, _curve, _mouseDeadZone, _stickDeadZone;
    private int _triggerThreshold, _holdMs;
    private bool _invertX, _invertY, _rumble, _normalizeDiagonals, _useForGame;
    private string _releaseKey;
    private string _message = "";
    private BindingRow? _listening;

    public InputConfigViewModel(ProfileStore store, InputProfile profile, uint? titleId, string? gameName)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(profile);
        _store = store;
        _titleId = titleId;
        GameName = gameName;
        _profileName = profile.Name;
        _releaseKey = profile.ReleaseCaptureKey;
        Bindings = [.. Enum.GetValues<XboxControl>().Select(c => new BindingRow(c, profile.Bindings.TryGetValue(c, out var k) ? k : []))];
        foreach (var b in Bindings) b.SetKeys(b.Keys.ToList());
        Load(profile);
        _useForGame = titleId is not null;
        RebindCommand = new RelayCommand<BindingRow>(BeginListening);
        ClearCommand = new RelayCommand<BindingRow>(r => { r?.SetKeys([]); Message = ""; });
        SaveCommand = new RelayCommand(() => Save());
        ResetCommand = new RelayCommand(() => { Load(InputProfile.CreateDefault() with { Name = ProfileName }); Message = "Defaults restored (not saved yet)."; });
        Modes = [InputMode.Controller, InputMode.KeyboardMouse, InputMode.Hybrid];
    }

    public static string DisplayName(XboxControl c) => c switch
    {
        XboxControl.LeftTrigger => "Left trigger", XboxControl.RightTrigger => "Right trigger",
        XboxControl.DPadUp => "D-pad up", XboxControl.DPadDown => "D-pad down", XboxControl.DPadLeft => "D-pad left", XboxControl.DPadRight => "D-pad right",
        XboxControl.LeftThumb => "Left stick click", XboxControl.RightThumb => "Right stick click",
        XboxControl.LeftStickUp => "Left stick up", XboxControl.LeftStickDown => "Left stick down", XboxControl.LeftStickLeft => "Left stick left", XboxControl.LeftStickRight => "Left stick right",
        XboxControl.RightStickUp => "Right stick up", XboxControl.RightStickDown => "Right stick down", XboxControl.RightStickLeft => "Right stick left", XboxControl.RightStickRight => "Right stick right",
        _ => c.ToString(),
    };

    public static string ModeName(InputMode m) => m switch
    {
        InputMode.Controller => "Xbox Controller",
        InputMode.KeyboardMouse => "Keyboard and Mouse",
        _ => "Hybrid (controller + keyboard and mouse)",
    };

    public IReadOnlyList<InputMode> Modes { get; }
    public ObservableCollection<BindingRow> Bindings { get; }
    public string? GameName { get; }
    public bool HasGame => _titleId is not null;

    public InputMode Mode { get => _mode; set { if (Set(ref _mode, value)) { OnPropertyChanged(nameof(IsController)); OnPropertyChanged(nameof(IsKeyboardMouse)); OnPropertyChanged(nameof(IsHybrid)); } } }
    public bool IsController { get => Mode == InputMode.Controller; set { if (value) Mode = InputMode.Controller; } }
    public bool IsKeyboardMouse { get => Mode == InputMode.KeyboardMouse; set { if (value) Mode = InputMode.KeyboardMouse; } }
    public bool IsHybrid { get => Mode == InputMode.Hybrid; set { if (value) Mode = InputMode.Hybrid; } }

    public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }
    public bool MouseEnabled { get => _mouseEnabled; set => Set(ref _mouseEnabled, value); }
    public StickTarget MouseTarget { get => _mouseTarget; set => Set(ref _mouseTarget, value); }
    public IReadOnlyList<StickTarget> StickTargets { get; } = [StickTarget.RightStick, StickTarget.LeftStick];
    public double Sensitivity { get => _sensitivity; set => Set(ref _sensitivity, value); }
    public double Curve { get => _curve; set => Set(ref _curve, value); }
    public double MouseDeadZone { get => _mouseDeadZone; set => Set(ref _mouseDeadZone, value); }
    public int HoldMilliseconds { get => _holdMs; set => Set(ref _holdMs, value); }
    public bool InvertX { get => _invertX; set => Set(ref _invertX, value); }
    public bool InvertY { get => _invertY; set => Set(ref _invertY, value); }
    public double StickDeadZone { get => _stickDeadZone; set => Set(ref _stickDeadZone, value); }
    public int TriggerThreshold { get => _triggerThreshold; set => Set(ref _triggerThreshold, value); }
    public bool Rumble { get => _rumble; set => Set(ref _rumble, value); }
    public bool NormalizeDiagonals { get => _normalizeDiagonals; set => Set(ref _normalizeDiagonals, value); }
    public string ReleaseCaptureKey { get => _releaseKey; set => Set(ref _releaseKey, value); }
    public bool UseForGame { get => _useForGame; set => Set(ref _useForGame, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public bool IsListening => _listening is not null;

    public ICommand RebindCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ResetCommand { get; }

    /// <summary>The profile that was saved last (the window applies it to the running session).</summary>
    public InputProfile? Saved { get; private set; }

    private void Load(InputProfile p)
    {
        Mode = p.Mode;
        foreach (var row in Bindings) row.SetKeys(p.Bindings.TryGetValue(row.Control, out var k) ? k : []);
        MouseEnabled = p.Mouse.Enabled;
        MouseTarget = p.Mouse.Target;
        Sensitivity = p.Mouse.Sensitivity;
        Curve = p.Mouse.Curve;
        MouseDeadZone = p.Mouse.DeadZone;
        HoldMilliseconds = p.Mouse.HoldMilliseconds;
        InvertX = p.Mouse.InvertX;
        InvertY = p.Mouse.InvertY;
        StickDeadZone = p.Controller.StickDeadZone;
        TriggerThreshold = p.Controller.TriggerThreshold;
        Rumble = p.Controller.Rumble;
        NormalizeDiagonals = p.NormalizeKeyboardDiagonals;
        ReleaseCaptureKey = p.ReleaseCaptureKey;
    }

    public InputProfile Build() => new()
    {
        Name = ProfileName.Trim(),
        Mode = Mode,
        Bindings = Bindings.Where(b => b.Keys.Count > 0).ToDictionary(b => b.Control, b => b.Keys.ToList()),
        Mouse = new MouseStickSettings
        {
            Enabled = MouseEnabled, Target = MouseTarget, Sensitivity = Sensitivity, Curve = Curve, DeadZone = MouseDeadZone,
            HoldMilliseconds = HoldMilliseconds, InvertX = InvertX, InvertY = InvertY,
        },
        Controller = new InputProfile().Controller with { StickDeadZone = StickDeadZone, TriggerThreshold = TriggerThreshold, Rumble = Rumble },
        NormalizeKeyboardDiagonals = NormalizeDiagonals,
        ReleaseCaptureKey = ReleaseCaptureKey,
    };

    public void BeginListening(BindingRow? row)
    {
        if (row is null) return;
        if (_listening is not null) _listening.IsListening = false;
        _listening = row;
        row.IsListening = true;
        Message = $"Press a key or mouse button for {row.Name} (Escape cancels).";
        OnPropertyChanged(nameof(IsListening));
    }

    /// <summary>
    /// Delivers a host key/mouse-button name while listening. The key is removed from any other control (a key drives
    /// exactly one control). Reserved and release keys are refused with an explanation. Returns true if consumed.
    /// </summary>
    public bool OfferKey(string key)
    {
        if (_listening is not { } row) return false;
        if (key == "Escape")
        {
            StopListening("Rebinding cancelled.");
            return true;
        }
        if (InputProfile.ReservedKeys.Contains(key)) { Message = $"{key} is reserved for emulator shortcuts."; return true; }
        if (key.Equals(ReleaseCaptureKey, StringComparison.OrdinalIgnoreCase)) { Message = $"{key} releases mouse capture and cannot be bound."; return true; }
        foreach (var other in Bindings.Where(b => b != row && b.Keys.Contains(key, StringComparer.OrdinalIgnoreCase)))
            other.SetKeys(other.Keys.Where(k => !k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList());
        row.SetKeys([key]);
        StopListening($"{row.Name} = {key}");
        return true;
    }

    private void StopListening(string message)
    {
        if (_listening is not null) _listening.IsListening = false;
        _listening = null;
        Message = message;
        OnPropertyChanged(nameof(IsListening));
    }

    /// <summary>Validates and stores the profile; assigns it to the current game when requested.</summary>
    public bool Save()
    {
        var profile = Build();
        var errors = profile.Validate();
        if (errors.Count > 0) { Message = string.Join(Environment.NewLine, errors); return false; }
        _store.Save(profile);
        if (UseForGame && _titleId is { } id) _store.AssignToGame(id, profile.Name);
        Saved = profile;
        Message = UseForGame && _titleId is not null ? $"Saved \"{profile.Name}\" and assigned it to {GameName}." : $"Saved \"{profile.Name}\".";
        return true;
    }
}
