using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using XboxPc.Domain;
using XboxPc.Input;

namespace XboxPc.DesktopApp.ViewModels;

public sealed class MappingRow(UiAction action, string pad, string key) : ObservableObject
{
    private string _pad = pad;
    private string _key = key;

    public UiAction Action { get; } = action;
    public string ActionText => Action switch
    {
        UiAction.PreviousPage => "Previous page",
        UiAction.NextPage => "Next page",
        UiAction.Menu => "Focus navigation menu",
        _ => Action.ToString(),
    };

    // WPF pushes null into SelectedItem when a ComboBox is torn down (page change). Null never means a user choice
    // ("(none)" is an explicit item), so it is ignored instead of silently clearing the binding.
    public string Pad { get => _pad; set { if (value is not null) Set(ref _pad, value); } }
    public string Key { get => _key; set { if (value is not null) Set(ref _key, value); } }
}

public sealed class ControllerSlotRow(int slot) : ObservableObject
{
    private string _status = "Not connected";
    private string _details = "";

    public string SlotText { get; } = $"Slot {slot + 1}";
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Details { get => _details; set => Set(ref _details, value); }
}

public sealed class SettingsViewModel : ObservableObject
{
    public const string None = "(none)";

    public static readonly IReadOnlyList<string> PadChoices = [None, .. InputMap.PadButtons];

    public static readonly IReadOnlyList<string> KeyChoices =
    [
        None, "Key.Up", "Key.Down", "Key.Left", "Key.Right", "Key.Enter", "Key.Space", "Key.Escape", "Key.Back",
        "Key.PageUp", "Key.PageDown", "Key.Home", "Key.End",
        .. Enumerable.Range(1, 12).Select(i => $"Key.F{i}"),
        .. Enumerable.Range('A', 26).Select(c => $"Key.{(char)c}"),
    ];

    private readonly MainViewModel _main;
    private UserProfile? _selectedProfile;
    private string _newProfileName = "";
    private string _renameText = "";
    private Choice<RenderQualityPreset> _preset;
    private bool _reduceMotion;
    private double _uiScale = 1;
    private bool _showFrameStats;
    private double _deadZonePercent = 24;
    private double _sensitivity = 1;
    private string _inputErrors = "";
    private string _inputSavedText = "";
    private string _section = "general";
    private bool _startupAnimation = true, _startupSound = true, _launchFullscreen, _dashboardFullscreen, _smoothScaling, _keepAspect = true;
    private double _volume = 0.7;
    private bool _gpuRenderer = true;
    private int _renderScale = 2;
    private bool _loading;
    private string _savedText = "";
    private readonly System.Windows.Threading.DispatcherTimer _saveTimer;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Presets =
        [
            new("Quality", RenderQualityPreset.Quality),
            new("Balanced", RenderQualityPreset.Balanced),
            new("Performance", RenderQualityPreset.Performance),
        ];
        _preset = Presets[1];
        Slots = new ObservableCollection<ControllerSlotRow>(Enumerable.Range(0, main.Host.Controllers.Source.SlotCount).Select(i => new ControllerSlotRow(i)));

        SwitchProfileCommand = new AsyncRelayCommand(SwitchAsync, () => SelectedProfile is not null && SelectedProfile.Id != _main.Host.Profiles.Active?.Id, _main.ReportError);
        CreateProfileCommand = new AsyncRelayCommand(CreateAsync, () => NewProfileName.Trim().Length > 0, _main.ReportError);
        RenameProfileCommand = new AsyncRelayCommand(RenameAsync, () => SelectedProfile is not null && RenameText.Trim().Length > 0, _main.ReportError);
        DeleteProfileCommand = new AsyncRelayCommand(DeleteAsync, () => SelectedProfile is not null && Profiles.Count > 1 && SelectedProfile.Id != _main.Host.Profiles.Active?.Id, _main.ReportError);
        SavePreferencesCommand = new AsyncRelayCommand(SavePreferencesAsync, onError: _main.ReportError);
        SaveInputCommand = new AsyncRelayCommand(SaveInputAsync, onError: _main.ReportError);
        ResetInputCommand = new RelayCommand(() => { LoadMap(InputMap.CreateDefault()); InputSavedText = "Defaults loaded, press Save mapping to keep them."; });
        OpenDataFolderCommand = new RelayCommand(() => _main.Host.Dialogs.OpenFolder(_main.Host.DataDirectory));
        OpenLogsFolderCommand = new RelayCommand(() => _main.Host.Dialogs.OpenFolder(_main.Host.LogDirectory));
        SetSectionCommand = new RelayCommand<string>(k => Section = k ?? "general");
        GameInputCommand = new RelayCommand(() => _main.OpenInputSettings?.Invoke(null, null));
        PreviewSoundCommand = new RelayCommand(PreviewSound, () => Volume > 0);
        // Settings apply as they change (console style); saving is batched so a dragged slider writes once.
        _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _ = _main.RunSafeAsync(SavePreferencesAsync); };
    }

    public string Section { get => _section; set => Set(ref _section, value); }
    public ICommand SetSectionCommand { get; }
    public ICommand GameInputCommand { get; }
    public ICommand PreviewSoundCommand { get; }
    public string SavedText { get => _savedText; private set => Set(ref _savedText, value); }

    public bool StartupAnimation { get => _startupAnimation; set { if (Set(ref _startupAnimation, value)) Changed(); } }
    public bool StartupSound { get => _startupSound; set { if (Set(ref _startupSound, value)) Changed(); } }
    public double Volume { get => _volume; set { if (Set(ref _volume, Math.Round(Math.Clamp(value, 0, 1), 2))) { OnPropertyChanged(nameof(VolumeText)); Changed(); } } }
    public string VolumeText => Volume <= 0 ? "Muted" : $"{Volume * 100:0}%";
    public bool LaunchFullscreen { get => _launchFullscreen; set { if (Set(ref _launchFullscreen, value)) Changed(); } }
    public bool DashboardFullscreen { get => _dashboardFullscreen; set { if (Set(ref _dashboardFullscreen, value)) Changed(); } }
    public bool SmoothScaling { get => _smoothScaling; set { if (Set(ref _smoothScaling, value)) Changed(); } }
    public bool KeepAspectRatio { get => _keepAspect; set { if (Set(ref _keepAspect, value)) Changed(); } }

    public IReadOnlyList<Choice<bool>> Renderers { get; } =
    [
        new("Direct3D 12 (graphics card)", true),
        new("CPU reference only", false),
    ];

    public IReadOnlyList<Choice<int>> Scales { get; } =
    [
        new("1x · 640x480 (native)", 1), new("2x · 1280x960", 2), new("3x · 1920x1440 (1080p class)", 3),
        new("4x · 2560x1920 (1440p class)", 4), new("5x · 3200x2400", 5), new("6x · 3840x2880 (4K class)", 6),
    ];

    public Choice<bool> Renderer
    {
        get => Renderers.First(r => r.Value == _gpuRenderer);
        set { if (value is not null && value.Value != _gpuRenderer) { _gpuRenderer = value.Value; OnPropertyChanged(); OnPropertyChanged(nameof(IsGpuRenderer)); Changed(); } }
    }

    public bool IsGpuRenderer => _gpuRenderer;

    public Choice<int> Scale
    {
        get => Scales.First(s => s.Value == _renderScale);
        set { if (value is not null && value.Value != _renderScale) { _renderScale = value.Value; OnPropertyChanged(); Changed(); } }
    }
    public static string AudioDeviceText => "Default Windows output device";
    public static string AudioBackendText => $"waveOut · 48 kHz stereo · up to {WaveOutPlayer.BufferMilliseconds} ms queued";

    private void Changed()
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void PreviewSound()
    {
        try
        {
            var sound = new XboxPc.DesktopApp.StartupSound(Volume);
            sound.Play();
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(XboxPc.DesktopApp.StartupSound.Length + 0.5) };
            t.Tick += (_, _) => { t.Stop(); sound.Dispose(); };
            t.Start();
        }
        catch (InvalidOperationException ex) { _main.Host.Dialogs.ShowError("Sound unavailable", ex.Message); }
    }

    public ObservableCollection<UserProfile> Profiles { get; } = [];
    public ObservableCollection<MappingRow> Mappings { get; } = [];
    public ObservableCollection<ControllerSlotRow> Slots { get; }
    public IReadOnlyList<Choice<RenderQualityPreset>> Presets { get; }
    public IReadOnlyList<string> PadOptions => PadChoices;
    public IReadOnlyList<string> KeyOptions => KeyChoices;
    public string ActiveProfileText => $"Active profile: {_main.Host.Profiles.Active?.DisplayName}";
    public string DataFolder => _main.Host.DataDirectory;
    public string ControllerApiText => _main.Host.Controllers.Source.IsApiAvailable
        ? $"Using {_main.Host.Controllers.Source.ApiName}. Controller input drives this app's menus only."
        : $"{_main.Host.Controllers.Source.ApiName} could not be loaded on this system. Keyboard and mouse still work.";

    public UserProfile? SelectedProfile
    {
        get => _selectedProfile;
        set { if (value is not null && Set(ref _selectedProfile, value)) RenameText = value.DisplayName; }
    }

    public string NewProfileName { get => _newProfileName; set => Set(ref _newProfileName, value ?? ""); }
    public string RenameText { get => _renameText; set => Set(ref _renameText, value ?? ""); }
    public Choice<RenderQualityPreset> Preset { get => _preset; set { if (value is not null && Set(ref _preset, value)) Changed(); } }
    public bool ReduceMotion { get => _reduceMotion; set { if (Set(ref _reduceMotion, value)) Changed(); } }
    public double UiScale { get => _uiScale; set { if (Set(ref _uiScale, Math.Round(value, 2))) { OnPropertyChanged(nameof(UiScaleText)); Changed(); } } }
    public string UiScaleText => $"{UiScale * 100:0}%";
    public bool ShowFrameStats { get => _showFrameStats; set { if (Set(ref _showFrameStats, value)) Changed(); } }
    public double DeadZonePercent { get => _deadZonePercent; set { if (Set(ref _deadZonePercent, Math.Round(value))) OnPropertyChanged(nameof(DeadZoneText)); } }
    public string DeadZoneText => $"{DeadZonePercent:0}%";
    public double Sensitivity { get => _sensitivity; set { if (Set(ref _sensitivity, Math.Round(value, 2))) OnPropertyChanged(nameof(SensitivityText)); } }
    public string SensitivityText => $"{Sensitivity:0.00}x";
    public string InputErrors { get => _inputErrors; private set { Set(ref _inputErrors, value); OnPropertyChanged(nameof(HasInputErrors)); } }
    public bool HasInputErrors => InputErrors.Length > 0;
    public string InputSavedText { get => _inputSavedText; private set => Set(ref _inputSavedText, value); }

    public ICommand SwitchProfileCommand { get; }
    public ICommand CreateProfileCommand { get; }
    public ICommand RenameProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand SavePreferencesCommand { get; }
    public ICommand SaveInputCommand { get; }
    public ICommand ResetInputCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogsFolderCommand { get; }

    public async Task LoadAsync()
    {
        var profiles = _main.Host.Profiles;
        Profiles.Clear();
        foreach (var p in await profiles.ListAsync().ConfigureAwait(true)) Profiles.Add(p);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profiles.Active?.Id);
        var prefs = profiles.Active?.Preferences ?? new UserPreferences();
        _loading = true;
        try
        {
            Preset = Presets.First(p => p.Value == prefs.RenderPreset);
            ReduceMotion = prefs.ReduceMotion;
            UiScale = prefs.UiScale;
            ShowFrameStats = prefs.ShowFrameStats;
            StartupAnimation = prefs.StartupAnimation;
            StartupSound = prefs.StartupSound;
            Volume = prefs.Volume;
            LaunchFullscreen = prefs.LaunchFullscreen;
            DashboardFullscreen = prefs.DashboardFullscreen;
            SmoothScaling = prefs.SmoothScaling;
            KeepAspectRatio = prefs.KeepAspectRatio;
            _gpuRenderer = prefs.GpuRenderer;
            _renderScale = Math.Clamp(prefs.RenderScale, 1, 6);
            OnPropertyChanged(nameof(Renderer));
            OnPropertyChanged(nameof(IsGpuRenderer));
            OnPropertyChanged(nameof(Scale));
        }
        finally { _loading = false; }
        LoadMap(await profiles.GetInputMapAsync().ConfigureAwait(true));
        InputErrors = "";
        InputSavedText = "";
        OnPropertyChanged(nameof(ActiveProfileText));
    }

    private void LoadMap(InputMap map)
    {
        Mappings.Clear();
        foreach (var action in Enum.GetValues<UiAction>())
        {
            var bindings = map.Bindings.TryGetValue(action, out var b) ? b : [];
            Mappings.Add(new MappingRow(action,
                bindings.FirstOrDefault(x => x.StartsWith("Pad.", StringComparison.Ordinal)) ?? None,
                bindings.FirstOrDefault(x => x.StartsWith("Key.", StringComparison.Ordinal)) ?? None));
        }
        DeadZonePercent = map.LeftStickDeadZone * 100;
        Sensitivity = map.StickSensitivity;
    }

    public InputMap BuildMap() => new()
    {
        Bindings = Mappings.ToDictionary(m => m.Action, m => (IReadOnlyList<string>)new[] { m.Pad, m.Key }.Where(b => b != None).ToList()),
        LeftStickDeadZone = DeadZonePercent / 100.0,
        StickSensitivity = Sensitivity,
    };

    public async Task SaveInputAsync()
    {
        var map = BuildMap();
        var errors = map.Validate();
        if (errors.Count > 0)
        {
            InputErrors = string.Join(Environment.NewLine, errors);
            InputSavedText = "";
            return;
        }
        await _main.Host.Profiles.SaveInputMapAsync(map).ConfigureAwait(true);
        _main.Host.Controllers.Map = map;
        InputErrors = "";
        InputSavedText = $"Mapping saved for {_main.Host.Profiles.Active?.DisplayName} at {DateTime.Now:t}.";
        InputMapChanged?.Invoke(this, map);
    }

    public event EventHandler<InputMap>? InputMapChanged;

    public async Task SavePreferencesAsync()
    {
        _saveTimer.Stop();
        await _main.Host.Profiles.SavePreferencesAsync(new UserPreferences
        {
            RenderPreset = Preset.Value,
            ReduceMotion = ReduceMotion,
            UiScale = UiScale,
            ShowFrameStats = ShowFrameStats,
            StartupAnimation = StartupAnimation,
            StartupSound = StartupSound,
            Volume = Volume,
            LaunchFullscreen = LaunchFullscreen,
            DashboardFullscreen = DashboardFullscreen,
            SmoothScaling = SmoothScaling,
            KeepAspectRatio = KeepAspectRatio,
            GpuRenderer = _gpuRenderer,
            RenderScale = _renderScale,
        }).ConfigureAwait(true);
        _main.NotifyPreferencesChanged();
        SavedText = $"Saved {DateTime.Now:t}";
    }

    private async Task SwitchAsync()
    {
        await _main.Host.Profiles.SwitchAsync(SelectedProfile!.Id).ConfigureAwait(true);
        _main.Host.Controllers.Map = await _main.Host.Profiles.GetInputMapAsync().ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
        _main.NotifyPreferencesChanged();
        _main.StatusMessage = $"Switched to {_main.Host.Profiles.Active?.DisplayName}.";
    }

    private async Task CreateAsync()
    {
        var created = await _main.Host.Profiles.CreateAsync(NewProfileName).ConfigureAwait(true);
        NewProfileName = "";
        await LoadAsync().ConfigureAwait(true);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == created.Id);
        _main.StatusMessage = $"Created profile {created.DisplayName}. Select it and press Switch to use it.";
    }

    private async Task RenameAsync()
    {
        var id = SelectedProfile!.Id;
        await _main.Host.Profiles.RenameAsync(id, RenameText).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == id);
    }

    private async Task DeleteAsync()
    {
        var target = SelectedProfile!;
        if (!_main.Host.Dialogs.Confirm("Delete profile", $"Delete profile '{target.DisplayName}' with its preferences, input mapping and per-game settings?")) return;
        await _main.Host.Profiles.DeleteAsync(target.Id).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Called by the view's poll timer to refresh live controller diagnostics.</summary>
    public void RefreshControllerDiagnostics()
    {
        var slots = _main.Host.Controllers.Slots;
        for (var i = 0; i < slots.Count && i < Slots.Count; i++)
        {
            var s = slots[i];
            if (!s.Connected || s.Reading is not { } r)
            {
                Slots[i].Status = "Not connected";
                Slots[i].Details = "";
                continue;
            }
            Slots[i].Status = "Connected";
            Slots[i].Details = string.Format(CultureInfo.InvariantCulture,
                "Buttons: {0} · Left stick raw ({1}, {2}) -> after dead zone ({3:0.00}, {4:0.00}) · Triggers L{5} R{6} · Packet {7}",
                r.Buttons == PadButtons.None ? "none" : r.Buttons.ToString(), r.LeftX, r.LeftY, s.StickX, s.StickY, r.LeftTrigger, r.RightTrigger, r.PacketNumber);
        }
    }
}
