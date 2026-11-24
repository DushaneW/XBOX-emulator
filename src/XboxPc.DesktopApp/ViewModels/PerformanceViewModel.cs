using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using XboxPc.Performance;

namespace XboxPc.DesktopApp.ViewModels;

public sealed record MetricRow(string Name, string Value, string Note);

/// <summary>A headline number: label, value, unit and where it comes from.</summary>
public sealed record StatTile(string Label, string Value, string Unit, string Note);

/// <summary>
/// Host-side diagnostics. Every value shown is measured or explicitly marked unavailable.
/// The frame counter measures this app's own WPF composition callbacks only.
/// </summary>
public sealed class PerformanceViewModel : ObservableObject, IDisposable
{
    public const int MaxRecordedSamples = 36_000; // 10 hours at 1 Hz

    private readonly MainViewModel _main;
    private readonly SystemUsageSampler _system = new();
    private readonly Lazy<GpuCounterSampler> _gpu = new(() => new GpuCounterSampler());
    private readonly List<PerformanceSample> _recorded = [];
    private bool _isMeasuringFrames;
    private bool _isRecording;
    private PointCollection _graph = [];
    private string _recordText = "Not recording.";
    private XboxPc.Emulation.OriginalXbox.EmulationSession? _session;
    private long _lastVBlanks, _lastFlips;
    private double _lastVirtualMs;
    private readonly System.Diagnostics.Stopwatch _sinceLast = new();
    private string _gameTitle = "";
    private string _gameState = "";

    public PerformanceViewModel(MainViewModel main)
    {
        _main = main;
        ToggleFramesCommand = new RelayCommand(() => IsMeasuringFrames = !IsMeasuringFrames);
        ToggleRecordingCommand = new RelayCommand(ToggleRecording);
        ExportCsvCommand = new RelayCommand(() => Export("csv"), () => _recorded.Count > 0);
        ExportJsonCommand = new RelayCommand(() => Export("json"), () => _recorded.Count > 0);
        ClearCommand = new RelayCommand(() => { _recorded.Clear(); Frames.Reset(); UpdateRecordText(); Graph = []; }, () => _recorded.Count > 0 || Frames.RecentIntervalsMs.Count > 0);
        RefreshDisplaysCommand = new RelayCommand(RefreshDisplays);
        RefreshDisplays();
        Tick();
    }

    public FrameTimeTracker Frames { get; } = new(600);
    public ObservableCollection<MetricRow> Metrics { get; } = [];

    /// <summary>Emulated game: measured from the running session. Empty when no game runs.</summary>
    public ObservableCollection<StatTile> Game { get; } = [];
    /// <summary>This PC: system and app counters.</summary>
    public ObservableCollection<StatTile> Host { get; } = [];
    public bool IsGameRunning => Game.Count > 0;
    public string GameTitle { get => _gameTitle; private set => Set(ref _gameTitle, value); }
    public string GameState { get => _gameState; private set => Set(ref _gameState, value); }
    public string BackendText => (_main.Host.ActiveRenderer ?? (_main.Preferences.GpuRenderer ? $"Direct3D 12 at {_main.Preferences.RenderScale}x when a game runs" : "CPU reference renderer")) + " · waveOut audio";
    public ObservableCollection<DisplayInfo> Displays { get; } = [];
    public IReadOnlyList<PerformanceSample> Recorded => _recorded;

    public bool IsMeasuringFrames
    {
        get => _isMeasuringFrames;
        set
        {
            if (!Set(ref _isMeasuringFrames, value)) return;
            if (!value) Frames.Reset();
            OnPropertyChanged(nameof(FrameButtonText));
            MeasuringChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string FrameButtonText => IsMeasuringFrames ? "Stop frame measurement" : "Start frame measurement";
    public bool IsRecording { get => _isRecording; private set { Set(ref _isRecording, value); OnPropertyChanged(nameof(RecordButtonText)); } }
    public string RecordButtonText => IsRecording ? "Stop recording" : "Start recording (1 sample/s)";
    public string RecordText { get => _recordText; private set => Set(ref _recordText, value); }
    public PointCollection Graph { get => _graph; private set => Set(ref _graph, value); }

    public static string FrameNote =>
        "Measures this app's own WPF composition callbacks. While measuring, WPF renders every display refresh, so the value " +
        "reflects the composition rate of this window, not any game. This app never generates or interpolates frames.";

    public ICommand ToggleFramesCommand { get; }
    public ICommand ToggleRecordingCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand RefreshDisplaysCommand { get; }

    public event EventHandler? MeasuringChanged;

    /// <summary>Whether the view must feed render callbacks (frame measurement or status-bar FPS).</summary>
    public bool NeedsFrameCallbacks => IsMeasuringFrames || _main.Preferences.ShowFrameStats;

    public void OnFrame(TimeSpan renderingTime) => Frames.RecordFrame(renderingTime);

    /// <summary>Called once per second by the view.</summary>
    public void Tick()
    {
        var (cpuTotal, cpuProc) = _system.SampleCpu();
        var (memUsed, memTotal, ws) = SystemUsageSampler.SampleMemory();
        var (gpu3d, gpuMem) = _gpu.Value.Sample();
        var stats = NeedsFrameCallbacks ? Frames.GetStats(TimeSpan.FromSeconds(2)) : null;
        var fpsReason = NeedsFrameCallbacks ? "Idle: nothing rendered in the last 2 s." : "Frame measurement is off.";

        UpdateGame();
        Host.Clear();
        Host.Add(Tile("CPU · system", cpuTotal, "0", "%", "GetSystemTimes"));
        Host.Add(Tile("CPU · XBOX PC", cpuProc, "0", "%", "Process CPU time across all logical processors (includes the emulator thread)"));
        Host.Add(Tile("GPU · 3D engine", gpu3d, "0", "%", "PDH GPU Engine counters, busiest adapter"));
        Host.Add(Tile("Memory · XBOX PC", ws, "0", "MB", "Process working set"));
        Host.Add(Tile("Memory · system", memUsed, "0", "%", "GlobalMemoryStatusEx"));
        Host.Add(Tile("GPU memory", gpuMem, "0", "MB", "PDH GPU Adapter Memory, dedicated, all adapters"));
        Metrics.Clear();
        Metrics.Add(new("Native rendered FPS (this app)", stats is { } s ? $"{s.FramesPerSecond:0.0}" : "-", stats is null ? fpsReason : "Measured from WPF render callbacks."));
        Metrics.Add(new("Frame time avg / p95 / p99 / max", stats is { } f ? $"{f.AverageMs:0.00} / {f.P95Ms:0.00} / {f.P99Ms:0.00} / {f.MaxMs:0.00} ms" : "-", stats is { } g ? $"{g.FrameCount} frames in window." : fpsReason));
        Metrics.Add(new("Generated / interpolated FPS", "Not applicable", "This app does not generate or interpolate frames."));
        Metrics.Add(new("Input latency", "Not measured", "No latency measurement is implemented; no value is estimated."));
        Metrics.Add(Row("CPU usage (system)", cpuTotal, "0.0", "%", "GetSystemTimes"));
        Metrics.Add(Row("CPU usage (this app)", cpuProc, "0.0", "%", "Process CPU time / logical processors"));
        Metrics.Add(Row("Memory in use (system)", memUsed, "0", "%", "GlobalMemoryStatusEx"));
        Metrics.Add(Row("Installed memory", memTotal, "0.0", " GB", "GlobalMemoryStatusEx"));
        Metrics.Add(Row("This app's working set", ws, "0", " MB", "Process.WorkingSet64"));
        Metrics.Add(Row("GPU 3D engine utilization", gpu3d, "0", "%", "PDH \\GPU Engine(*engtype_3D), busiest adapter"));
        Metrics.Add(Row("GPU dedicated memory in use", gpuMem, "0", " MB", "PDH \\GPU Adapter Memory, all adapters"));
        Metrics.Add(new("CPU / GPU temperature", "Not available", SensorSupport.TemperatureUnavailableReason));

        if (IsRecording)
        {
            _recorded.Add(new PerformanceSample(DateTimeOffset.UtcNow, stats?.FramesPerSecond, stats?.AverageMs, stats?.P95Ms,
                cpuTotal.Value, cpuProc.Value, memUsed.Value, ws.Value, gpu3d.Value, gpuMem.Value));
            if (_recorded.Count >= MaxRecordedSamples) { IsRecording = false; }
            UpdateRecordText();
        }

        _main.FrameStatsText = _main.Preferences.ShowFrameStats
            ? stats is { } st ? $"UI {st.FramesPerSecond:0} fps · {st.AverageMs:0.0} ms" : "UI idle"
            : "";
        UpdateGraph();
    }

    private static StatTile Tile(string label, Reading r, string format, string unit, string source) =>
        r.Value is { } v ? new StatTile(label, v.ToString(format, CultureInfo.CurrentCulture), unit, source) : new StatTile(label, "-", "", r.UnavailableReason ?? "Unavailable");

    private void UpdateGame()
    {
        var session = _main.Host.ActiveSession;
        var wasRunning = IsGameRunning;
        Game.Clear();
        if (session is null)
        {
            _session = null;
            GameTitle = "";
            GameState = "";
            if (wasRunning) OnPropertyChanged(nameof(IsGameRunning));
            return;
        }
        var st = session.Stats;
        if (!ReferenceEquals(session, _session))
        {
            _session = session;
            _lastVBlanks = st.VBlanks;
            _lastFlips = st.Flips;
            _lastVirtualMs = st.VirtualMs;
            _sinceLast.Restart();
        }
        var dt = Math.Max(0.001, _sinceLast.Elapsed.TotalSeconds);
        var flipsPerSecond = (st.Flips - _lastFlips) / dt;
        var refresh = (st.VBlanks - _lastVBlanks) / dt;
        var speed = (st.VirtualMs - _lastVirtualMs) / 1000 / dt;
        _lastFlips = st.Flips;
        _lastVBlanks = st.VBlanks;
        _lastVirtualMs = st.VirtualMs;
        _sinceLast.Restart();

        GameTitle = _main.Host.ActiveTitle ?? "Original Xbox program";
        OnPropertyChanged(nameof(BackendText));
        GameState = st.State switch
        {
            XboxPc.Emulation.OriginalXbox.SessionState.Running => "Running",
            XboxPc.Emulation.OriginalXbox.SessionState.Paused => "Paused",
            XboxPc.Emulation.OriginalXbox.SessionState.Stopped => "Stopped: " + st.Stop,
            _ => "Starting",
        };
        var running = st.State == XboxPc.Emulation.OriginalXbox.SessionState.Running;
        Game.Add(st.Flips == 0
            ? new StatTile("Game frames", "-", "", "The title has not flipped display buffers yet (single-buffered titles never do), so its frame rate cannot be measured.")
            : new StatTile("Game frames", running ? flipsPerSecond.ToString("0", CultureInfo.CurrentCulture) : "0", "fps", "Display-buffer flips by the emulated title per second."));
        Game.Add(st.Flips == 0 || flipsPerSecond < 0.5
            ? new StatTile("Game frame time", "-", "", "Needs at least one buffer flip per second.")
            : new StatTile("Game frame time", (1000 / flipsPerSecond).ToString("0.0", CultureInfo.CurrentCulture), "ms", "Average time between buffer flips."));
        Game.Add(new StatTile("Emulation speed", (running ? speed * 100 : 0).ToString("0", CultureInfo.CurrentCulture), "%", "Emulated time per real second. 100% = real Xbox speed; it never runs faster."));
        Game.Add(new StatTile("Emulated CPU", st.Mips.ToString("0.0", CultureInfo.CurrentCulture), "MIPS", "Guest instructions executed per real second, averaged over the session."));
        Game.Add(new StatTile("Video refresh", running ? refresh.ToString("0", CultureInfo.CurrentCulture) : "0", "Hz", "Emulated vertical blanks per real second (60 Hz at full speed)."));
        if (!wasRunning) OnPropertyChanged(nameof(IsGameRunning));
    }

    private static MetricRow Row(string name, Reading r, string format, string unit, string source) =>
        r.Value is { } v
            ? new MetricRow(name, v.ToString(format, CultureInfo.CurrentCulture) + unit, source)
            : new MetricRow(name, "Unavailable", r.UnavailableReason ?? "Unavailable");

    private void UpdateGraph()
    {
        var intervals = Frames.RecentIntervalsMs;
        if (intervals.Count < 2) { if (Graph.Count > 0) Graph = []; return; }
        const double width = 600, height = 120, maxMs = 50;
        var points = new PointCollection(intervals.Count);
        var step = width / Math.Max(1, Frames.Capacity - 1);
        var offset = Frames.Capacity - intervals.Count;
        for (var i = 0; i < intervals.Count; i++)
            points.Add(new Point((offset + i) * step, height - (Math.Min(intervals[i], maxMs) / maxMs * height)));
        points.Freeze();
        Graph = points;
    }

    private void ToggleRecording()
    {
        IsRecording = !IsRecording;
        UpdateRecordText();
    }

    private void UpdateRecordText() =>
        RecordText = IsRecording ? $"Recording... {_recorded.Count} samples." : _recorded.Count > 0 ? $"{_recorded.Count} samples captured, ready to export." : "Not recording.";

    private void Export(string format)
    {
        var name = $"xboxpc-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.{format}";
        var filter = format == "csv" ? "CSV (*.csv)|*.csv" : "JSON (*.json)|*.json";
        if (_main.Host.Dialogs.PickSaveFile("Export diagnostics", filter, name) is not { } path) return;
        try
        {
            var content = format == "csv" ? DiagnosticsExporter.ToCsv(_recorded) : DiagnosticsExporter.ToJson(_recorded, HostInfoProvider.Collect());
            File.WriteAllText(path, content);
            _main.StatusMessage = $"Exported {_recorded.Count} samples to {Path.GetFileName(path)}.";
            _main.Host.Log.Write(XboxPc.Application.AppLogLevel.Information, "Performance", $"Exported {_recorded.Count} samples ({format}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _main.Host.Dialogs.ShowError("Export failed", ex.Message);
        }
    }

    private void RefreshDisplays()
    {
        Displays.Clear();
        foreach (var d in DisplayInfoProvider.GetDisplays()) Displays.Add(d);
    }

    public void Dispose()
    {
        if (_gpu.IsValueCreated) _gpu.Value.Dispose();
    }
}
