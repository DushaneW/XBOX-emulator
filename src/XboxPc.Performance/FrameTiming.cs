using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XboxPc.Performance;

public sealed record FrameStats(int FrameCount, double FramesPerSecond, double AverageMs, double P95Ms, double P99Ms, double MaxMs);

/// <summary>
/// Measures frame intervals of frames the application actually rendered. Callers feed it the timestamps of real
/// render callbacks; it never invents frames. Gaps longer than <see cref="IdleGap"/> are treated as idle time
/// (nothing needed rendering), not as slow frames.
/// </summary>
public sealed class FrameTimeTracker(int capacity = 600)
{
    public static readonly TimeSpan IdleGap = TimeSpan.FromMilliseconds(250);

    private readonly Queue<(TimeSpan At, double IntervalMs)> _frames = new();
    private TimeSpan? _last;

    public int Capacity { get; } = capacity > 1 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public void Reset()
    {
        _frames.Clear();
        _last = null;
    }

    /// <summary>Records a rendered frame. Duplicate timestamps (same composition tick) are ignored.</summary>
    public void RecordFrame(TimeSpan renderTime)
    {
        if (_last is { } last)
        {
            var delta = renderTime - last;
            if (delta <= TimeSpan.Zero) return;
            if (delta > IdleGap) { _last = renderTime; return; }
            _frames.Enqueue((renderTime, delta.TotalMilliseconds));
            while (_frames.Count > Capacity) _frames.Dequeue();
        }
        _last = renderTime;
    }

    public IReadOnlyList<double> RecentIntervalsMs => _frames.Select(f => f.IntervalMs).ToList();

    /// <summary>Stats over the most recent <paramref name="window"/>; null if fewer than 2 frames were measured.</summary>
    public FrameStats? GetStats(TimeSpan window)
    {
        if (_last is null) return null;
        var since = _last.Value - window;
        var intervals = _frames.Where(f => f.At >= since).Select(f => f.IntervalMs).ToArray();
        if (intervals.Length < 2) return null;
        Array.Sort(intervals);
        var total = intervals.Sum();
        return new FrameStats(
            intervals.Length,
            intervals.Length / (total / 1000.0),
            total / intervals.Length,
            Percentile(intervals, 0.95),
            Percentile(intervals, 0.99),
            intervals[^1]);
    }

    internal static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        var rank = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo));
    }
}

/// <summary>One row of a diagnostics capture. Null = not measured / unavailable.</summary>
public sealed record PerformanceSample(
    DateTimeOffset Timestamp,
    double? NativeRenderedFps,
    double? FrameTimeAvgMs,
    double? FrameTimeP95Ms,
    double? CpuTotalPercent,
    double? CpuProcessPercent,
    double? MemoryUsedPercent,
    double? ProcessWorkingSetMb,
    double? Gpu3DPercent,
    double? GpuDedicatedMb)
{
    /// <summary>This app never generates or interpolates frames.</summary>
    public static double? GeneratedFps => null;

    /// <summary>Input latency is not measured in this version.</summary>
    public static double? InputLatencyMs => null;
}

public static class DiagnosticsExporter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly string[] Header =
    [
        "timestamp_utc", "native_rendered_fps", "generated_fps", "frame_time_avg_ms", "frame_time_p95_ms", "input_latency_ms",
        "cpu_total_pct", "cpu_process_pct", "memory_used_pct", "process_working_set_mb", "gpu_3d_pct", "gpu_dedicated_mb",
    ];

    public static string ToCsv(IEnumerable<PerformanceSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var sb = new StringBuilder();
        sb.AppendLine("# XBOX PC diagnostics export. Empty cell = not measured/unavailable. generated_fps and input_latency_ms are not measured by this app.");
        sb.AppendLine(string.Join(',', Header));
        foreach (var s in samples)
        {
            sb.AppendJoin(',',
                s.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                F(s.NativeRenderedFps), F(PerformanceSample.GeneratedFps), F(s.FrameTimeAvgMs), F(s.FrameTimeP95Ms), F(PerformanceSample.InputLatencyMs),
                F(s.CpuTotalPercent), F(s.CpuProcessPercent), F(s.MemoryUsedPercent), F(s.ProcessWorkingSetMb), F(s.Gpu3DPercent), F(s.GpuDedicatedMb));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string ToJson(IEnumerable<PerformanceSample> samples, object? host = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var payload = new
        {
            format = "xboxpc-diagnostics/1",
            note = "null = not measured or unavailable. This app does not generate frames and does not measure input latency.",
            host,
            samples = samples.Select(s => new
            {
                timestamp_utc = s.Timestamp.ToUniversalTime(),
                native_rendered_fps = s.NativeRenderedFps,
                generated_fps = PerformanceSample.GeneratedFps,
                frame_time_avg_ms = s.FrameTimeAvgMs,
                frame_time_p95_ms = s.FrameTimeP95Ms,
                input_latency_ms = PerformanceSample.InputLatencyMs,
                cpu_total_pct = s.CpuTotalPercent,
                cpu_process_pct = s.CpuProcessPercent,
                memory_used_pct = s.MemoryUsedPercent,
                process_working_set_mb = s.ProcessWorkingSetMb,
                gpu_3d_pct = s.Gpu3DPercent,
                gpu_dedicated_mb = s.GpuDedicatedMb,
            }),
        };
        return JsonSerializer.Serialize(payload, Indented);
    }

    private static string F(double? value) => value is { } v && double.IsFinite(v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "";
}
