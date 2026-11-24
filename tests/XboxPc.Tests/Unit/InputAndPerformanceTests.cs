using XboxPc.Domain;
using XboxPc.Input;
using XboxPc.Performance;

namespace XboxPc.Tests.Unit;

public class StickMathTests
{
    [Fact]
    public void Inside_dead_zone_is_zero() => Assert.Equal((0.0, 0.0), StickMath.ApplyRadialDeadZone(5000, 5000, 0.24));

    [Fact]
    public void Full_deflection_is_one()
    {
        var (x, y) = StickMath.ApplyRadialDeadZone(32767, 0, 0.24);
        Assert.Equal(1.0, x, 3);
        Assert.Equal(0.0, y, 3);
    }

    [Fact]
    public void Output_is_rescaled_from_dead_zone_edge()
    {
        var (x, _) = StickMath.ApplyRadialDeadZone((short)(32767 * 0.62), 0, 0.24);
        Assert.Equal(0.5, x, 2);
    }

    [Fact]
    public void Minimum_raw_value_is_clamped() => Assert.Equal(-1.0, StickMath.ApplyRadialDeadZone(short.MinValue, 0, 0).X, 3);

    [Theory]
    [InlineData(0.0, 0.9, PadButtons.DPadUp)]
    [InlineData(0.0, -0.9, PadButtons.DPadDown)]
    [InlineData(0.9, 0.2, PadButtons.DPadRight)]
    [InlineData(-0.9, 0.2, PadButtons.DPadLeft)]
    [InlineData(0.3, 0.3, PadButtons.None)]
    public void Stick_direction(double x, double y, PadButtons expected) => Assert.Equal(expected, ControllerService.StickToDirection(x, y));
}

public class ControllerServiceTests
{
    private static GamepadReading Pad(PadButtons b, short lx = 0, short ly = 0) => new(1, b, 0, 0, lx, ly, 0, 0);

    [Fact]
    public void Connect_press_and_disconnect()
    {
        var src = new ScriptedGamepadSource();
        var svc = new ControllerService(src);
        var actions = new List<UiAction>();
        int connected = 0, disconnected = 0;
        svc.ActionTriggered += (_, e) => actions.Add(e.Action);
        svc.Connected += (_, _) => connected++;
        svc.Disconnected += (_, _) => disconnected++;

        src.Slots[0] = Pad(PadButtons.None);
        svc.Poll(TimeSpan.Zero);
        Assert.Equal(1, connected);

        src.Slots[0] = Pad(PadButtons.A);
        svc.Poll(TimeSpan.FromMilliseconds(16));
        svc.Poll(TimeSpan.FromMilliseconds(32)); // held: no second Accept
        Assert.Equal([UiAction.Accept], actions);

        src.Slots[0] = null;
        svc.Poll(TimeSpan.FromMilliseconds(48));
        Assert.Equal(1, disconnected);
        Assert.False(svc.Slots[0].Connected);
    }

    [Fact]
    public void Buttons_held_at_connect_do_not_fire()
    {
        var src = new ScriptedGamepadSource();
        var svc = new ControllerService(src);
        var count = 0;
        svc.ActionTriggered += (_, _) => count++;
        src.Slots[1] = Pad(PadButtons.A);
        svc.Poll(TimeSpan.Zero);
        svc.Poll(TimeSpan.FromMilliseconds(16));
        Assert.Equal(0, count);
    }

    [Fact]
    public void Held_direction_repeats_after_delay()
    {
        var src = new ScriptedGamepadSource();
        var svc = new ControllerService(src);
        var events = new List<UiActionEventArgs>();
        svc.ActionTriggered += (_, e) => events.Add(e);
        src.Slots[0] = Pad(PadButtons.None);
        svc.Poll(TimeSpan.Zero);
        src.Slots[0] = Pad(PadButtons.DPadDown);
        svc.Poll(TimeSpan.FromMilliseconds(10));
        svc.Poll(TimeSpan.FromMilliseconds(300)); // before initial delay
        Assert.Single(events);
        svc.Poll(TimeSpan.FromMilliseconds(470));
        Assert.Equal(2, events.Count);
        Assert.True(events[1].IsRepeat);
        Assert.Equal(UiAction.Down, events[1].Action);
    }

    [Fact]
    public void Stick_navigation_respects_dead_zone_and_remapping()
    {
        var src = new ScriptedGamepadSource();
        var svc = new ControllerService(src);
        var actions = new List<UiAction>();
        svc.ActionTriggered += (_, e) => actions.Add(e.Action);
        src.Slots[0] = Pad(PadButtons.None);
        svc.Poll(TimeSpan.Zero);

        src.Slots[0] = Pad(PadButtons.None, 0, 9000); // inside default dead zone
        svc.Poll(TimeSpan.FromMilliseconds(16));
        Assert.Empty(actions);

        src.Slots[0] = Pad(PadButtons.None, 0, 32000);
        svc.Poll(TimeSpan.FromMilliseconds(32));
        Assert.Equal([UiAction.Up], actions);

        var bindings = InputMap.CreateDefault().Bindings.ToDictionary(k => k.Key, v => v.Value);
        bindings[UiAction.Accept] = ["Pad.X"];
        svc.Map = InputMap.CreateDefault() with { Bindings = bindings };
        src.Slots[0] = Pad(PadButtons.A);
        svc.Poll(TimeSpan.FromMilliseconds(48));
        src.Slots[0] = Pad(PadButtons.X);
        svc.Poll(TimeSpan.FromMilliseconds(64));
        Assert.Equal([UiAction.Up, UiAction.Accept], actions);
    }

    [Fact]
    public void Real_xinput_source_never_throws()
    {
        var src = new XInputGamepadSource();
        for (var i = -1; i <= 4; i++) _ = src.Read(i);
        Assert.Equal(4, src.SlotCount);
    }
}

public class FrameTimeTrackerTests
{
    [Fact]
    public void Steady_60hz_is_measured()
    {
        var tracker = new FrameTimeTracker();
        for (var i = 0; i <= 120; i++) tracker.RecordFrame(TimeSpan.FromMilliseconds(i * 16.6667));
        var stats = tracker.GetStats(TimeSpan.FromSeconds(2))!;
        Assert.Equal(60, stats.FramesPerSecond, 0);
        Assert.Equal(16.67, stats.AverageMs, 1);
        Assert.Equal(120, stats.FrameCount);
    }

    [Fact]
    public void No_frames_means_no_stats() => Assert.Null(new FrameTimeTracker().GetStats(TimeSpan.FromSeconds(1)));

    [Fact]
    public void Idle_gaps_are_not_counted_as_slow_frames()
    {
        var tracker = new FrameTimeTracker();
        tracker.RecordFrame(TimeSpan.FromMilliseconds(0));
        tracker.RecordFrame(TimeSpan.FromMilliseconds(16));
        tracker.RecordFrame(TimeSpan.FromMilliseconds(5000)); // idle
        tracker.RecordFrame(TimeSpan.FromMilliseconds(5016));
        tracker.RecordFrame(TimeSpan.FromMilliseconds(5032));
        Assert.All(tracker.RecentIntervalsMs, ms => Assert.True(ms < 20));
    }

    [Fact]
    public void Duplicate_timestamps_are_ignored()
    {
        var tracker = new FrameTimeTracker();
        tracker.RecordFrame(TimeSpan.FromMilliseconds(10));
        tracker.RecordFrame(TimeSpan.FromMilliseconds(10));
        tracker.RecordFrame(TimeSpan.FromMilliseconds(26));
        Assert.Single(tracker.RecentIntervalsMs);
    }

    [Fact]
    public void Percentiles_capture_spikes()
    {
        var tracker = new FrameTimeTracker();
        var t = 0.0;
        tracker.RecordFrame(TimeSpan.Zero);
        for (var i = 0; i < 99; i++) { t += 10; tracker.RecordFrame(TimeSpan.FromMilliseconds(t)); }
        t += 100;
        tracker.RecordFrame(TimeSpan.FromMilliseconds(t));
        var stats = tracker.GetStats(TimeSpan.FromSeconds(10))!;
        Assert.Equal(100, stats.MaxMs, 3);
        Assert.Equal(10, stats.P95Ms, 3);
    }

    [Fact]
    public void Capacity_is_bounded()
    {
        var tracker = new FrameTimeTracker(10);
        for (var i = 0; i < 100; i++) tracker.RecordFrame(TimeSpan.FromMilliseconds(i * 16));
        Assert.Equal(10, tracker.RecentIntervalsMs.Count);
    }
}

public class DiagnosticsExportTests
{
    private static readonly PerformanceSample Sample = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero), 59.94, 16.68, 17.5, 12.5, 1.25, 55, 120, null, null);

    [Fact]
    public void Csv_leaves_unavailable_values_empty_and_never_invents_generated_fps()
    {
        var csv = DiagnosticsExporter.ToCsv([Sample]);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.StartsWith("#", lines[0], StringComparison.Ordinal);
        var header = lines[1].Split(',');
        var row = lines[2].Split(',');
        Assert.Equal(header.Length, row.Length);
        Assert.Equal("59.94", row[Array.IndexOf(header, "native_rendered_fps")]);
        Assert.Equal("", row[Array.IndexOf(header, "generated_fps")]);
        Assert.Equal("", row[Array.IndexOf(header, "input_latency_ms")]);
        Assert.Equal("", row[Array.IndexOf(header, "gpu_3d_pct")]);
        Assert.Equal("2026-11-22T12:00:00.0000000+00:00", row[0]);
    }

    [Fact]
    public void Json_uses_null_for_unavailable_values()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(DiagnosticsExporter.ToJson([Sample]));
        var s = doc.RootElement.GetProperty("samples")[0];
        Assert.Equal(System.Text.Json.JsonValueKind.Null, s.GetProperty("generated_fps").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, s.GetProperty("gpu_3d_pct").ValueKind);
        Assert.Equal(59.94, s.GetProperty("native_rendered_fps").GetDouble());
    }

    [Fact]
    public void Empty_capture_exports_header_only() =>
        Assert.Equal(2, DiagnosticsExporter.ToCsv([]).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
}

public class HostSamplerTests
{
    [Fact]
    public void Cpu_first_sample_is_unavailable_then_measured()
    {
        var sampler = new SystemUsageSampler();
        var (first, _) = sampler.SampleCpu();
        Assert.False(first.HasValue);
        Assert.NotNull(first.UnavailableReason);
        Thread.Sleep(150);
        var (second, proc) = sampler.SampleCpu();
        if (second.Value is { } v) Assert.InRange(v, 0, 100);
        else Assert.NotNull(second.UnavailableReason);
        if (proc.Value is { } p) Assert.InRange(p, 0, 100);
    }

    [Fact]
    public void Memory_values_are_plausible()
    {
        var (used, total, ws) = SystemUsageSampler.SampleMemory();
        Assert.InRange(used.Value!.Value, 0, 100);
        Assert.True(total.Value > 0.5);
        Assert.True(ws.Value > 1);
    }

    [Fact]
    public void Gpu_counters_report_value_or_reason_and_never_throw()
    {
        using var gpu = new GpuCounterSampler();
        for (var i = 0; i < 3; i++)
        {
            var (util, mem) = gpu.Sample();
            Assert.True(util.HasValue || util.UnavailableReason is not null);
            Assert.True(mem.HasValue || mem.UnavailableReason is not null);
            if (util.Value is { } u) Assert.InRange(u, 0, 100);
            Thread.Sleep(50);
        }
    }

    [Fact]
    public void Host_info_and_displays_are_collected()
    {
        var host = HostInfoProvider.Collect();
        Assert.False(string.IsNullOrWhiteSpace(host.Os));
        Assert.False(string.IsNullOrWhiteSpace(host.Cpu));
        foreach (var d in DisplayInfoProvider.GetDisplays())
        {
            Assert.True(d.Width > 0 && d.Height > 0);
            Assert.True(d.RefreshHz >= 0);
        }
    }

    [Fact]
    public void Temperature_is_explicitly_unavailable() =>
        Assert.Contains("Not available", SensorSupport.TemperatureUnavailableReason, StringComparison.Ordinal);
}
