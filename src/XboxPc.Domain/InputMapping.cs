namespace XboxPc.Domain;

/// <summary>Actions of the application UI itself (not game input).</summary>
public enum UiAction
{
    Up,
    Down,
    Left,
    Right,
    Accept,
    Back,
    PreviousPage,
    NextPage,
    Menu,
}

/// <summary>
/// A per-profile mapping of UI actions to bindings. Binding strings are "Pad.&lt;Button&gt;" or "Key.&lt;WpfKeyName&gt;".
/// </summary>
public sealed record InputMap
{
    public static readonly IReadOnlyList<string> PadButtons =
    [
        "Pad.A", "Pad.B", "Pad.X", "Pad.Y", "Pad.DPadUp", "Pad.DPadDown", "Pad.DPadLeft", "Pad.DPadRight",
        "Pad.LeftShoulder", "Pad.RightShoulder", "Pad.Start", "Pad.Back", "Pad.LeftThumb", "Pad.RightThumb",
    ];

    public IReadOnlyDictionary<UiAction, IReadOnlyList<string>> Bindings { get; init; } = new Dictionary<UiAction, IReadOnlyList<string>>();

    /// <summary>Radial dead zone of the left stick, 0..0.9 of full deflection.</summary>
    public double LeftStickDeadZone { get; init; } = 0.24;

    /// <summary>Stick sensitivity multiplier used for navigation repeat speed, 0.25..3.</summary>
    public double StickSensitivity { get; init; } = 1.0;

    public static InputMap CreateDefault() => new()
    {
        Bindings = new Dictionary<UiAction, IReadOnlyList<string>>
        {
            [UiAction.Up] = ["Pad.DPadUp", "Key.Up"],
            [UiAction.Down] = ["Pad.DPadDown", "Key.Down"],
            [UiAction.Left] = ["Pad.DPadLeft", "Key.Left"],
            [UiAction.Right] = ["Pad.DPadRight", "Key.Right"],
            [UiAction.Accept] = ["Pad.A", "Key.Enter"],
            [UiAction.Back] = ["Pad.B", "Key.Escape"],
            [UiAction.PreviousPage] = ["Pad.LeftShoulder", "Key.PageUp"],
            [UiAction.NextPage] = ["Pad.RightShoulder", "Key.PageDown"],
            [UiAction.Menu] = ["Pad.Start", "Key.F1"],
        },
    };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!double.IsFinite(LeftStickDeadZone) || LeftStickDeadZone < 0 || LeftStickDeadZone > 0.9)
            errors.Add("Dead zone must be between 0% and 90%.");
        if (!double.IsFinite(StickSensitivity) || StickSensitivity < 0.25 || StickSensitivity > 3)
            errors.Add("Sensitivity must be between 0.25 and 3.");

        var owners = new Dictionary<string, UiAction>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in Enum.GetValues<UiAction>())
        {
            if (!Bindings.TryGetValue(action, out var list) || list.Count == 0)
            {
                errors.Add($"Action '{action}' has no binding.");
                continue;
            }
            if (!list.Any(b => b.StartsWith("Pad.", StringComparison.Ordinal)) && action is UiAction.Accept or UiAction.Back)
                errors.Add($"Action '{action}' needs a controller binding so the UI stays usable with a controller.");
            foreach (var binding in list)
            {
                if (!IsWellFormed(binding)) { errors.Add($"Binding '{binding}' is not valid."); continue; }
                if (owners.TryGetValue(binding, out var other) && other != action)
                    errors.Add($"Binding '{binding}' is assigned to both '{other}' and '{action}'.");
                owners[binding] = action;
            }
        }
        return errors;
    }

    public static bool IsWellFormed(string binding) =>
        PadButtons.Contains(binding) ||
        (binding.StartsWith("Key.", StringComparison.Ordinal) && binding.Length > 4 && binding[4..].All(char.IsLetterOrDigit));

    public UiAction? Resolve(string binding)
    {
        foreach (var (action, list) in Bindings)
            if (list.Contains(binding, StringComparer.OrdinalIgnoreCase)) return action;
        return null;
    }
}
