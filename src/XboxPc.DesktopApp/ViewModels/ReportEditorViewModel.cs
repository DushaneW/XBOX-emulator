using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Input;
using XboxPc.Application;
using XboxPc.Domain;
using XboxPc.Performance;
using XboxPc.Runtime.Abstractions;

namespace XboxPc.DesktopApp.ViewModels;

/// <summary>Compatibility report form. Saving enforces <see cref="CompatibilityRules"/>; nothing is pre-filled as a claim.</summary>
public sealed class ReportEditorViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly GameEntry _entry;
    private Choice<CompatibilityStatus> _outcome;
    private Choice<ReportSource> _source;
    private string _runtimeId;
    private string _runtimeVersion;
    private string _fingerprint;
    private string _procedure;
    private string _averageFps = "";
    private string _audio = "";
    private string _input = "";
    private string _graphicsIssues = "";
    private string _crashDetails = "";
    private string _evidence;
    private string _reviewer = "";
    private bool _reviewerConfirmed;
    private string _notes = "";
    private string _errors = "";
    private bool _isHashing;

    public ReportEditorViewModel(AppHost host, GameEntry entry, RuntimeDescriptor? runtime, LaunchOutcome? lastLaunch)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(entry);
        _host = host;
        _entry = entry;
        Host = HostInfoProvider.Collect();
        Outcomes = Enum.GetValues<CompatibilityStatus>().Select(s => new Choice<CompatibilityStatus>(s.ToDisplay(), s)).ToList();
        Sources = [new("Local test on this PC", ReportSource.LocalVerified), new("Community report (from someone else)", ReportSource.Community)];
        _outcome = Outcomes[0];
        _source = Sources[0];
        _runtimeId = runtime?.RuntimeId ?? "";
        _runtimeVersion = runtime?.Version ?? "";
        _fingerprint = lastLaunch?.ContentFingerprint ?? "";
        _procedure = lastLaunch is { Result: not null }
            ? $"Launched from the XBOX PC library via LaunchCoordinator on {DateTimeOffset.Now:g}. Session ended: {lastLaunch.Status}, {lastLaunch.Message}"
            : "";
        _evidence = host.Log.CurrentFile is { } log ? log : "";
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ComputeHashCommand = new AsyncRelayCommand(ComputeHashAsync, () => !_isHashing && File.Exists(_entry.ContentPath));
        AddEvidenceFileCommand = new RelayCommand(AddEvidenceFile);
    }

    public HostInfo Host { get; }
    public string GameName => _entry.DisplayName;
    public string HostText => $"{Host.Os}\nCPU: {Host.Cpu}\nGPU: {Host.Gpu}\nRAM: {Host.Ram}\nDriver: {Host.GpuDriver}";
    public IReadOnlyList<Choice<CompatibilityStatus>> Outcomes { get; }
    public IReadOnlyList<Choice<ReportSource>> Sources { get; }

    public Choice<CompatibilityStatus> Outcome { get => _outcome; set { if (value is not null) Set(ref _outcome, value); } }
    public Choice<ReportSource> Source { get => _source; set { if (value is not null) Set(ref _source, value); } }
    public string RuntimeId { get => _runtimeId; set => Set(ref _runtimeId, value ?? ""); }
    public string RuntimeVersion { get => _runtimeVersion; set => Set(ref _runtimeVersion, value ?? ""); }
    public string Fingerprint { get => _fingerprint; set => Set(ref _fingerprint, value ?? ""); }
    public string Procedure { get => _procedure; set => Set(ref _procedure, value ?? ""); }
    public string AverageFps { get => _averageFps; set => Set(ref _averageFps, value ?? ""); }
    public string Audio { get => _audio; set => Set(ref _audio, value ?? ""); }
    public string InputResult { get => _input; set => Set(ref _input, value ?? ""); }
    public string GraphicsIssues { get => _graphicsIssues; set => Set(ref _graphicsIssues, value ?? ""); }
    public string CrashDetails { get => _crashDetails; set => Set(ref _crashDetails, value ?? ""); }
    public string Evidence { get => _evidence; set => Set(ref _evidence, value ?? ""); }
    public string Reviewer { get => _reviewer; set => Set(ref _reviewer, value ?? ""); }
    public bool ReviewerConfirmed { get => _reviewerConfirmed; set => Set(ref _reviewerConfirmed, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value ?? ""); }
    public string Errors { get => _errors; private set { Set(ref _errors, value); OnPropertyChanged(nameof(HasErrors)); } }
    public bool HasErrors => Errors.Length > 0;

    public ICommand SaveCommand { get; }
    public ICommand ComputeHashCommand { get; }
    public ICommand AddEvidenceFileCommand { get; }

    /// <summary>Raised with true after a successful save.</summary>
    public event EventHandler<bool>? CloseRequested;

    public CompatibilityReport Build()
    {
        double? fps = null;
        if (!string.IsNullOrWhiteSpace(AverageFps))
        {
            if (!double.TryParse(AverageFps, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
                !double.TryParse(AverageFps, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                throw new ValidationException("Average FPS must be a number you measured, or empty.");
            fps = v;
        }
        static IReadOnlyList<string> Lines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        static string? Opt(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        return new CompatibilityReport
        {
            GameEntryId = _entry.Id,
            RuntimeId = RuntimeId.Trim(),
            RuntimeVersion = RuntimeVersion.Trim(),
            Host = Host,
            TestAssetFingerprint = Opt(Fingerprint),
            TestProcedure = Procedure.Trim(),
            Outcome = Outcome.Value,
            Source = Source.Value,
            AverageFps = fps,
            AudioResult = Opt(Audio),
            InputResult = Opt(InputResult),
            GraphicsIssues = Lines(GraphicsIssues),
            CrashDetails = Opt(LogRedactor.Redact(CrashDetails)),
            EvidenceReferences = Lines(Evidence),
            Reviewer = Opt(Reviewer),
            ReviewerConfirmed = ReviewerConfirmed,
            ReproducibilityNotes = Opt(Notes),
        };
    }

    public async Task<bool> SaveAsync()
    {
        try
        {
            var report = Build();
            await _host.Compatibility.SubmitAsync(report).ConfigureAwait(true);
            Errors = "";
            CloseRequested?.Invoke(this, true);
            return true;
        }
        catch (ValidationException ex)
        {
            Errors = ex.Message;
            return false;
        }
    }

    private async Task ComputeHashAsync()
    {
        if (_entry.ContentPath is not { } path || !File.Exists(path)) return;
        _isHashing = true;
        try
        {
            var hash = await Task.Run(async () =>
            {
                await using var stream = File.OpenRead(path);
                return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
            }).ConfigureAwait(true);
            Fingerprint = "sha256:" + hash;
        }
        catch (IOException ex) { Errors = $"Could not hash the file: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { Errors = $"Could not hash the file: {ex.Message}"; }
        finally { _isHashing = false; CommandManager.InvalidateRequerySuggested(); }
    }

    private void AddEvidenceFile()
    {
        if (_host.Dialogs.PickFile("Select evidence (log, screenshot, video)", "All files (*.*)|*.*") is { } path)
            Evidence = string.IsNullOrWhiteSpace(Evidence) ? path : Evidence.TrimEnd() + "\n" + path;
    }
}
