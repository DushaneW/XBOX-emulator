using System.Text.Json;
using System.Text.Json.Serialization;

namespace XboxPc.Emulation.Input;

public enum InputMode
{
    /// <summary>Only physical controllers drive the virtual controllers.</summary>
    Controller,

    /// <summary>Keyboard and mouse drive virtual controller 1. Works with no physical controller connected.</summary>
    KeyboardMouse,

    /// <summary>Controller 1 and keyboard/mouse both drive virtual controller 1 at the same time.</summary>
    Hybrid,
}

public enum StickTarget
{
    LeftStick,
    RightStick,
}

/// <summary>Mouse-to-stick translation. Mouse speed (pixels/second) becomes stick deflection.</summary>
public sealed record MouseStickSettings
{
    public bool Enabled { get; init; } = true;
    public StickTarget Target { get; init; } = StickTarget.RightStick;

    /// <summary>Multiplier on mouse speed; 1.0 -> 2000 px/s gives full deflection.</summary>
    public double Sensitivity { get; init; } = 1.0;

    /// <summary>Exponent of the response curve applied after the dead zone (1 = linear, &gt;1 = finer near centre).</summary>
    public double Curve { get; init; } = 1.0;

    /// <summary>Output dead zone (fraction of deflection that is skipped so slow motion still moves the stick).</summary>
    public double DeadZone { get; init; }

    public bool InvertX { get; init; }
    public bool InvertY { get; init; }

    /// <summary>How long the last deflection is held after the mouse stops reporting motion (smooths polling gaps).</summary>
    public int HoldMilliseconds { get; init; } = 40;

    public const double FullDeflectionSpeed = 2000.0;
}

public sealed record ControllerSettings
{
    /// <summary>Radial dead zone of the physical sticks (0-0.9).</summary>
    public double StickDeadZone { get; init; } = 0.15;

    /// <summary>Trigger values at or below this are reported as 0 (0-254).</summary>
    public int TriggerThreshold { get; init; } = 8;

    public bool Rumble { get; init; } = true;

    /// <summary>Physical shoulder buttons map to the Xbox Black/White buttons (the host pad has no Black/White).</summary>
    public XboxControl LeftShoulderAs { get; init; } = XboxControl.White;

    public XboxControl RightShoulderAs { get; init; } = XboxControl.Black;
}

/// <summary>
/// A named input profile (global or per game). Keyboard keys use host key names (e.g. "W", "Space", "LeftShift");
/// mouse buttons use "MouseLeft", "MouseRight", "MouseMiddle", "MouseX1", "MouseX2", "WheelUp", "WheelDown".
/// </summary>
public sealed record InputProfile
{
    public string Name { get; init; } = "Default";
    public InputMode Mode { get; init; } = InputMode.Hybrid;
    public Dictionary<XboxControl, List<string>> Bindings { get; init; } = [];
    public MouseStickSettings Mouse { get; init; } = new();
    public ControllerSettings Controller { get; init; } = new();
    public bool NormalizeKeyboardDiagonals { get; init; } = true;

    /// <summary>Key that releases mouse/keyboard capture. Never delivered to the game.</summary>
    public string ReleaseCaptureKey { get; init; } = "RightCtrl";

    /// <summary>Emulator shortcuts reserved for the application (never delivered to the game, cannot be bound).</summary>
    public static readonly IReadOnlySet<string> ReservedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "F11", "F12", "Pause" };

    public static InputProfile CreateDefault() => new()
    {
        Name = "Default",
        Bindings = new()
        {
            [XboxControl.LeftStickUp] = ["W"], [XboxControl.LeftStickDown] = ["S"], [XboxControl.LeftStickLeft] = ["A"], [XboxControl.LeftStickRight] = ["D"],
            [XboxControl.A] = ["Space"], [XboxControl.B] = ["LeftCtrl"], [XboxControl.X] = ["R"], [XboxControl.Y] = ["E"],
            [XboxControl.Black] = ["Q"], [XboxControl.White] = ["F"],
            [XboxControl.LeftTrigger] = ["MouseRight"], [XboxControl.RightTrigger] = ["MouseLeft"],
            [XboxControl.DPadUp] = ["Up"], [XboxControl.DPadDown] = ["Down"], [XboxControl.DPadLeft] = ["Left"], [XboxControl.DPadRight] = ["Right"],
            [XboxControl.Start] = ["Return"], [XboxControl.Back] = ["Tab"],
            [XboxControl.LeftThumb] = ["LeftShift"], [XboxControl.RightThumb] = ["MouseMiddle"],
            [XboxControl.RightStickUp] = ["I"], [XboxControl.RightStickDown] = ["K"], [XboxControl.RightStickLeft] = ["J"], [XboxControl.RightStickRight] = ["L"],
        },
    };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 64) errors.Add("Profile name must be 1-64 characters.");
        if (!Enum.IsDefined(Mode)) errors.Add("Unknown input mode.");
        Range(Mouse.Sensitivity, 0.05, 20, "Mouse sensitivity");
        Range(Mouse.Curve, 0.2, 4, "Mouse curve");
        Range(Mouse.DeadZone, 0, 0.9, "Mouse dead zone");
        if (Mouse.HoldMilliseconds is < 0 or > 500) errors.Add("Mouse hold time must be 0-500 ms.");
        Range(Controller.StickDeadZone, 0, 0.9, "Stick dead zone");
        if (Controller.TriggerThreshold is < 0 or > 254) errors.Add("Trigger threshold must be 0-254.");
        if (string.IsNullOrWhiteSpace(ReleaseCaptureKey)) errors.Add("A capture-release key is required.");

        var owner = new Dictionary<string, XboxControl>(StringComparer.OrdinalIgnoreCase);
        foreach (var (control, keys) in Bindings)
        {
            if (!Enum.IsDefined(control)) { errors.Add($"Unknown control {control}."); continue; }
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key)) { errors.Add($"Empty binding for {control}."); continue; }
                if (key.Equals(ReleaseCaptureKey, StringComparison.OrdinalIgnoreCase)) errors.Add($"{key} releases mouse capture and cannot be bound to {control}.");
                if (ReservedKeys.Contains(key)) errors.Add($"{key} is reserved for emulator shortcuts and cannot be bound to {control}.");
                if (owner.TryGetValue(key, out var other) && other != control) errors.Add($"{key} is bound to both {other} and {control}.");
                owner[key] = control;
            }
        }
        return errors;

        void Range(double v, double lo, double hi, string what)
        {
            if (!double.IsFinite(v) || v < lo || v > hi) errors.Add($"{what} must be between {lo} and {hi}.");
        }
    }

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
}

/// <summary>Stores profiles as JSON files in one directory and remembers which profile each game uses.</summary>
public sealed class ProfileStore(string directory)
{
    private string AssignmentsFile => Path.Combine(directory, "game-assignments.json");

    private static string FileName(string name)
    {
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_').ToArray()).Trim();
        return (safe.Length == 0 ? "profile" : safe) + ".profile.json";
    }

    public void Save(InputProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = profile.Validate();
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        Directory.CreateDirectory(directory);
        var tmp = Path.Combine(directory, FileName(profile.Name) + ".tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(profile, InputProfile.Json));
        File.Move(tmp, Path.Combine(directory, FileName(profile.Name)), overwrite: true);
    }

    public InputProfile? Load(string name)
    {
        var path = Path.Combine(directory, FileName(name));
        if (!File.Exists(path)) return null;
        try
        {
            var p = JsonSerializer.Deserialize<InputProfile>(File.ReadAllText(path), InputProfile.Json);
            return p is not null && p.Validate().Count == 0 ? p : null;
        }
        catch (JsonException) { return null; }
    }

    public IReadOnlyList<string> List() =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.profile.json").Select(f => Load(Path.GetFileName(f)[..^".profile.json".Length])?.Name).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    public bool Delete(string name)
    {
        var path = Path.Combine(directory, FileName(name));
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Per-game assignment keyed by XBE title ID.</summary>
    public void AssignToGame(uint titleId, string profileName)
    {
        var map = LoadAssignments();
        map[titleId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)] = profileName;
        Directory.CreateDirectory(directory);
        File.WriteAllText(AssignmentsFile, JsonSerializer.Serialize(map, InputProfile.Json));
    }

    public InputProfile ProfileForGame(uint titleId)
    {
        var map = LoadAssignments();
        return map.TryGetValue(titleId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), out var name) && Load(name) is { } p
            ? p
            : Load("Default") ?? InputProfile.CreateDefault();
    }

    private Dictionary<string, string> LoadAssignments()
    {
        try { return File.Exists(AssignmentsFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(AssignmentsFile)) ?? [] : []; }
        catch (JsonException) { return []; }
    }
}
