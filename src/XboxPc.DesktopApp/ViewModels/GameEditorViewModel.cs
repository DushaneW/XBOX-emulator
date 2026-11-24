using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Input;
using XboxPc.Application;
using XboxPc.Domain;
using XboxPc.Runtime.Abstractions;

namespace XboxPc.DesktopApp.ViewModels;

public sealed record ReportRow(CompatibilityReport Report)
{
    public string When => Report.TestDate.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Status => Report.Outcome.ToDisplay();
    public string SourceText => Report.Source == ReportSource.LocalVerified ? "Local, verified" : "Community";
    public string RuntimeText => $"{Report.RuntimeId} {Report.RuntimeVersion}";
    public string Reviewer => Report.Reviewer ?? "-";
    public string Evidence => Report.EvidenceReferences.Count == 0 ? "No evidence" : string.Join(", ", Report.EvidenceReferences);
}

public sealed class GameEditorViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly GameEntry _entry;
    private readonly Func<Guid?, Task> _onChanged;
    private string _name;
    private ConsoleGeneration _generation;
    private string _contentPath;
    private string _coverArtPath;
    private string _notes;
    private RuntimeDescriptor? _selectedRuntime;
    private string _timeoutText = "30";
    private string _compatibilityText = "Not Tested";
    private string _compatibilitySource = "No reports yet";
    private string _regressionText = "";
    private string _launchStatus = "";
    private string _launchOutput = "";
    private string _launchDiagnostics = "";
    private bool _isLaunching;
    private CancellationTokenSource? _launchCts;
    private LaunchOutcome? _lastOutcome;
    private string _section;
    private bool _isFavorite;
    private XbeInfo? _xbe;
    private string _saveSummary = "";

    public GameEditorViewModel(MainViewModel main, GameEntry? entry, Func<Guid?, Task> onChanged)
    {
        _main = main;
        _onChanged = onChanged;
        IsNew = entry is null;
        _entry = entry?.Clone() ?? new GameEntry { DisplayName = "", Generation = ConsoleGeneration.Other };
        _name = _entry.DisplayName;
        _generation = _entry.Generation;
        _contentPath = _entry.ContentPath ?? "";
        _coverArtPath = _entry.CoverArtPath ?? "";
        _notes = _entry.UserNotes ?? "";
        Generations = Enum.GetValues<ConsoleGeneration>().Select(g => new Choice<ConsoleGeneration>(g.ToDisplay(), g)).ToList();

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsLaunching, _main.ReportError);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => !IsNew && !IsLaunching, _main.ReportError);
        BrowseContentCommand = new RelayCommand(BrowseContent);
        BrowseCoverCommand = new RelayCommand(BrowseCover);
        ClearCoverCommand = new RelayCommand(() => CoverArtPath = "", () => CoverArtPath.Length > 0);
        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => !IsNew && !IsLaunching && LaunchBlockReason is null, _main.ReportError);
        CancelLaunchCommand = new RelayCommand(() => _launchCts?.Cancel(), () => IsLaunching);
        AddReportCommand = new AsyncRelayCommand(AddReportAsync, () => !IsNew && !IsLaunching, _main.ReportError);
        PlayCommand = new AsyncRelayCommand(() => _main.PlayAsync(EntryId), () => !IsNew && !IsLaunching && PlayBlock is null, _main.ReportError);
        ToggleFavoriteCommand = new AsyncRelayCommand(ToggleFavoriteAsync, () => !IsNew, _main.ReportError);
        SetSectionCommand = new RelayCommand<string>(k => Section = k ?? "overview");
        InputSettingsCommand = new RelayCommand(() => _main.OpenInputSettings?.Invoke(_xbe?.TitleId, _entry.DisplayName), () => _xbe is not null);
        OpenSaveFolderCommand = new RelayCommand(() => { if (SaveFolder is { } f) _main.Host.Dialogs.OpenFolder(f); }, () => SaveFolder is not null);
        BackCommand = new RelayCommand(() => _main.Library.CloseDetail());
        _section = IsNew ? "edit" : "overview";
        _isFavorite = _entry.IsFavorite;
        UpdateRuntimeOptions();
    }

    public Guid EntryId => _entry.Id;
    public bool IsNew { get; }
    public string Title => IsNew ? "New library entry" : _entry.DisplayName;
    public IReadOnlyList<Choice<ConsoleGeneration>> Generations { get; }
    public ObservableCollection<RuntimeDescriptor> RuntimeOptions { get; } = [];
    public ObservableCollection<ReportRow> History { get; } = [];

    public string Name { get => _name; set => Set(ref _name, value ?? ""); }
    public string ContentPath { get => _contentPath; set { if (Set(ref _contentPath, value ?? "")) { OnLaunchabilityChanged(); OnPropertyChanged(nameof(ContentPathDisplay)); } } }
    /// <summary>The program path for display, with the user's profile folder shortened to %USERPROFILE% (no user name in screenshots).</summary>
    public string ContentPathDisplay => ShortenProfile(ContentPath);

    public static string ShortenProfile(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase) ? "%USERPROFILE%" + path[profile.Length..] : path;
    }

    public string CoverArtPath { get => _coverArtPath; set { if (Set(ref _coverArtPath, value ?? "")) OnPropertyChanged(nameof(CoverPreview)); } }
    public string? CoverPreview => string.IsNullOrWhiteSpace(CoverArtPath) ? null : CoverArtPath;
    public string Notes { get => _notes; set => Set(ref _notes, value ?? ""); }
    public string TimeoutText { get => _timeoutText; set => Set(ref _timeoutText, value ?? ""); }
    public string AddedText => IsNew ? "" : $"Added {_entry.AddedAt.ToLocalTime():g} · Source: {_entry.MetadataSource}";
    public string LastLaunchedText => _entry.LastLaunchedAt is { } l ? $"Last launched {l.ToLocalTime():g}" : "Never launched";

    public Choice<ConsoleGeneration> Generation
    {
        get => Generations.First(g => g.Value == _generation);
        set
        {
            if (value is null || value.Value == _generation) return;
            _generation = value.Value;
            OnPropertyChanged();
            UpdateRuntimeOptions();
        }
    }

    public RuntimeDescriptor? SelectedRuntime
    {
        get => _selectedRuntime;
        set { if ((value is not null || RuntimeOptions.Count == 0) && Set(ref _selectedRuntime, value)) OnLaunchabilityChanged(); }
    }

    public string RuntimeStateText => SelectedRuntime is { } r ? $"{RuntimeCard.StateLabel(r.State)}. {r.Summary}" : "No runtime is registered for this generation.";
    public bool RuntimeAvailable => SelectedRuntime?.State == ImplementationState.Available;

    /// <summary>Why Launch is disabled, or null when it is enabled. Always shown next to the button.</summary>
    public string? LaunchBlockReason
    {
        get
        {
            if (IsNew) return "Save the entry first.";
            if (_generation != _entry.Generation || !string.Equals(ContentPath, _entry.ContentPath ?? "", StringComparison.Ordinal))
                return LaunchCoordinator.CheckLaunchable(SelectedRuntime, Probe()) ?? "Save your changes before launching.";
            return LaunchCoordinator.CheckLaunchable(SelectedRuntime, _entry);

            GameEntry Probe()
            {
                var probe = _entry.Clone();
                probe.Generation = _generation;
                probe.ContentPath = string.IsNullOrWhiteSpace(ContentPath) ? null : ContentPath;
                return probe;
            }
        }
    }

    public string CompatibilityText { get => _compatibilityText; private set => Set(ref _compatibilityText, value); }
    public string CompatibilitySource { get => _compatibilitySource; private set => Set(ref _compatibilitySource, value); }
    public string RegressionText { get => _regressionText; private set => Set(ref _regressionText, value); }
    public string LaunchStatus { get => _launchStatus; private set => Set(ref _launchStatus, value); }
    public string LaunchOutput { get => _launchOutput; private set => Set(ref _launchOutput, value); }
    public string LaunchDiagnostics { get => _launchDiagnostics; private set => Set(ref _launchDiagnostics, value); }
    public bool IsLaunching { get => _isLaunching; private set { Set(ref _isLaunching, value); CommandManager.InvalidateRequerySuggested(); } }

    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand BrowseContentCommand { get; }
    public ICommand BrowseCoverCommand { get; }
    public ICommand ClearCoverCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand CancelLaunchCommand { get; }
    public ICommand AddReportCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand SetSectionCommand { get; }
    public ICommand InputSettingsCommand { get; }
    public ICommand OpenSaveFolderCommand { get; }
    public ICommand BackCommand { get; }

    /// <summary>Visible part of the game page: overview, compat, save, diag or edit.</summary>
    public string Section { get => _section; set => Set(ref _section, value); }

    public bool IsFavorite { get => _isFavorite; private set => Set(ref _isFavorite, value); }
    public string GenerationText => _entry.Generation.ToDisplay();
    public bool IsEmulatorTitle => !IsNew && MainViewModel.IsEmulatorTitle(_entry);

    /// <summary>Why Play is unavailable, or null. Emulator titles need their .xbe; other entries follow the runtime rules.</summary>
    public string? PlayBlock => IsNew ? "Save the entry first." : IsEmulatorTitle ? MainViewModel.PlayBlockReason(_main.Host, _entry) : LaunchBlockReason;
    public bool CanPlay => PlayBlock is null;

    /// <summary>Facts read from the title's .xbe header (Original Xbox entries only).</summary>
    public XbeInfo? Xbe { get => _xbe; private set { Set(ref _xbe, value); OnPropertyChanged(nameof(HasXbe)); OnPropertyChanged(nameof(SaveFolder)); OnPropertyChanged(nameof(SaveFolderDisplay)); } }
    public bool HasXbe => Xbe is not null;

    /// <summary>The title's E: drive on this PC (where its saves go), or null when the title is not an emulator title.</summary>
    public string? SaveFolder => Xbe is { } x ? Path.Combine(_main.Host.DataDirectory, "harddisk", x.TitleId.ToString("X8", CultureInfo.InvariantCulture)) : null;
    public string? SaveFolderDisplay => SaveFolder is { } f ? ShortenProfile(f) : null;
    public string SaveSummary { get => _saveSummary; private set => Set(ref _saveSummary, value); }

    public async Task LoadAsync()
    {
        if (IsNew) return;
        LoadXbe();
        var profile = _main.Host.Profiles.Active!;
        var settings = await _main.Host.GameSettings.GetAsync(_entry.Id, profile.Id).ConfigureAwait(true);
        TimeoutText = settings.LaunchTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        var preferred = RuntimeOptions.FirstOrDefault(r => r.RuntimeId == settings.PreferredRuntimeId);
        if (preferred is not null) SelectedRuntime = preferred;
        await LoadCompatibilityAsync().ConfigureAwait(true);
    }

    private async Task LoadCompatibilityAsync()
    {
        var host = _main.Host;
        var history = await host.Compatibility.HistoryAsync(_entry.Id).ConfigureAwait(true);
        History.Clear();
        foreach (var r in history) History.Add(new ReportRow(r));
        var summary = await host.Compatibility.GetSummaryAsync(_entry.Id).ConfigureAwait(true);
        CompatibilityText = summary.Status.ToDisplay();
        CompatibilitySource = summary.Report is null
            ? "No reports yet. Status stays Not Tested until a real test is recorded."
            : summary.Source == ReportSource.LocalVerified
                ? $"Locally verified report from {summary.Report.TestDate.ToLocalTime():g} (reviewer: {summary.Report.Reviewer})"
                : $"Community report from {summary.Report.TestDate.ToLocalTime():g}, not verified on this machine";
        RegressionText = history.Count > 0 ? CompatibilityService.Compare(history, history[0]).Message : "";
    }

    private void UpdateRuntimeOptions()
    {
        RuntimeOptions.Clear();
        foreach (var d in _main.Host.Runtimes.ForGeneration(_generation)) RuntimeOptions.Add(d);
        SelectedRuntime = _main.Host.Runtimes.Resolve(_generation, null);
        OnLaunchabilityChanged();
    }

    private void OnLaunchabilityChanged()
    {
        OnPropertyChanged(nameof(LaunchBlockReason));
        OnPropertyChanged(nameof(RuntimeStateText));
        OnPropertyChanged(nameof(RuntimeAvailable));
        OnPropertyChanged(nameof(PlayBlock));
        OnPropertyChanged(nameof(CanPlay));
        CommandManager.InvalidateRequerySuggested();
    }

    private void BrowseContent()
    {
        var filter = _generation == ConsoleGeneration.Other ? "XPC test programs (*.xpct)|*.xpct|All files (*.*)|*.*" : "All files (*.*)|*.*";
        if (_main.Host.Dialogs.PickFile("Select content file", filter) is { } path) ContentPath = path;
    }

    private void BrowseCover()
    {
        if (_main.Host.Dialogs.PickFile("Select cover art (your own or licensed image)", "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp") is { } path)
        {
            if (ImageFileValidator.Validate(path) is { } error) _main.Host.Dialogs.ShowError("Cover art", error);
            else CoverArtPath = path;
        }
    }

    private int ParseTimeout()
    {
        if (!int.TryParse(TimeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out var t) || t < PerGameSettings.MinTimeout || t > PerGameSettings.MaxTimeout)
            throw new ValidationException($"Launch timeout must be a whole number of seconds between {PerGameSettings.MinTimeout} and {PerGameSettings.MaxTimeout}.");
        return t;
    }

    public async Task SaveAsync()
    {
        var timeout = ParseTimeout();
        var host = _main.Host;
        _entry.DisplayName = Name;
        _entry.Generation = _generation;
        _entry.ContentPath = ContentPath;
        _entry.CoverArtPath = CoverArtPath;
        _entry.UserNotes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
        _entry.IsFavorite = IsFavorite;
        if (IsNew) await host.Library.AddAsync(_entry).ConfigureAwait(true);
        else await host.Library.UpdateAsync(_entry).ConfigureAwait(true);

        await host.GameSettings.SaveAsync(new PerGameSettings
        {
            GameId = _entry.Id,
            ProfileId = host.Profiles.Active!.Id,
            PreferredRuntimeId = SelectedRuntime?.RuntimeId,
            LaunchTimeoutSeconds = timeout,
        }).ConfigureAwait(true);
        _main.StatusMessage = $"Saved '{_entry.DisplayName}'.";
        await _onChanged(_entry.Id).ConfigureAwait(true);
    }

    private async Task DeleteAsync()
    {
        if (!_main.Host.Dialogs.Confirm("Remove from library", $"Remove '{_entry.DisplayName}' from the library?\n\nOnly the library entry, its settings and its reports are removed. Your files on disk are not touched."))
            return;
        await _main.Host.Library.RemoveAsync(_entry.Id).ConfigureAwait(true);
        _main.StatusMessage = $"Removed '{_entry.DisplayName}'.";
        await _onChanged(null).ConfigureAwait(true);
    }

    private sealed class Progress(GameEditorViewModel vm) : ILaunchProgress
    {
        public void Report(string stage) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => vm.LaunchStatus = stage + "...");
    }

    private async Task LaunchAsync()
    {
        var timeout = ParseTimeout();
        IsLaunching = true;
        LaunchOutput = "";
        LaunchDiagnostics = "";
        LaunchStatus = "Starting...";
        _launchCts = new CancellationTokenSource();
        try
        {
            var outcome = await Task.Run(() => _main.Host.Launcher.LaunchAsync(_entry, SelectedRuntime?.RuntimeId, TimeSpan.FromSeconds(timeout), new Progress(this), _launchCts.Token)).ConfigureAwait(true);
            _lastOutcome = outcome;
            LaunchStatus = $"{outcome.Status}: {outcome.Message}";
            if (outcome.Result is { } result)
            {
                LaunchOutput = result.Output.Count == 0 ? "(no output)" : string.Join(", ", result.Output);
            }
            if (outcome.Diagnostics is { } d)
            {
                var sb = new StringBuilder();
                foreach (var (k, v) in d.Counters) sb.Append(k).Append(": ").AppendLine(v);
                foreach (var e in d.Entries) sb.Append('[').Append(e.Level).Append("] ").AppendLine(e.Message);
                LaunchDiagnostics = sb.ToString().TrimEnd();
            }
            var refreshed = await _main.Host.Library.GetAsync(_entry.Id).ConfigureAwait(true);
            if (refreshed?.LastLaunchedAt is { } launched) { _entry.LastLaunchedAt = launched; OnPropertyChanged(nameof(LastLaunchedText)); }
        }
        finally
        {
            _launchCts.Dispose();
            _launchCts = null;
            IsLaunching = false;
        }
    }

    public void Dispose() => _launchCts?.Cancel();

    private void LoadXbe()
    {
        if (!IsEmulatorTitle || !File.Exists(_entry.ContentPath)) return;
        try
        {
            Xbe = XbeInfo.Read(_entry.ContentPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XboxPc.Emulation.OriginalXbox.XbeFormatException)
        {
            Xbe = null;
            SaveSummary = "The program file could not be read: " + LogRedactor.Redact(ex.Message);
            return;
        }
        var folder = SaveFolder!;
        var files = Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).ToList() : [];
        SaveSummary = files.Count == 0
            ? "No save data yet. Anything the game writes to its E: drive is kept here."
            : $"{files.Count} file{(files.Count == 1 ? "" : "s")}, {files.Sum(f => f.Length) / 1024.0:0.#} KB · last written {files.Max(f => f.LastWriteTime):g}";
    }

    private async Task ToggleFavoriteAsync()
    {
        IsFavorite = !IsFavorite;
        _entry.IsFavorite = IsFavorite;
        var stored = await _main.Host.Library.GetAsync(_entry.Id).ConfigureAwait(true);
        if (stored is null) return;
        stored.IsFavorite = IsFavorite;
        await _main.Host.Library.UpdateAsync(stored).ConfigureAwait(true);
        _main.StatusMessage = IsFavorite ? $"Added {stored.DisplayName} to favourites." : $"Removed {stored.DisplayName} from favourites.";
    }

    private async Task AddReportAsync()
    {
        var vm = new ReportEditorViewModel(_main.Host, _entry, SelectedRuntime, _lastOutcome);
        if (_main.ShowReportDialog?.Invoke(vm) != true) return;
        await LoadCompatibilityAsync().ConfigureAwait(true);
        await _main.Library.RefreshAsync().ConfigureAwait(true);
    }
}
