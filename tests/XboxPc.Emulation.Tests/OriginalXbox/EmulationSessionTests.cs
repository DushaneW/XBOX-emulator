using System.Diagnostics;
using XboxPc.Emulation.Input;
using XboxPc.Emulation.OriginalXbox;

namespace XboxPc.Emulation.Tests.OriginalXbox;

/// <summary>The threaded session host used by the desktop app: pacing, posted commands, frames, pause and stop.</summary>
public class EmulationSessionTests
{
    private static OriginalXboxMachine Boot(string program)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", program);
        var file = File.ReadAllBytes(Path.Combine(dir, "default.xbe"));
        var m = new OriginalXboxMachine();
        m.MountDefaults(dir, null);
        m.Load(XbeImage.Parse(file), file);
        return m;
    }

    private static SessionStats WaitStopped(EmulationSession s, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (s.Stats.State != SessionState.Stopped && sw.Elapsed < timeout) Thread.Sleep(10);
        return s.Stats;
    }

    [Fact]
    public void Paced_session_never_runs_ahead_of_real_time()
    {
        // The video test waits for 30 vblanks: ~533 ms of emulated time. Paced, it cannot finish sooner in real time.
        using var s = new EmulationSession(Boot("xpc-video-test"));
        var sw = Stopwatch.StartNew();
        s.Start();
        var stats = WaitStopped(s, TimeSpan.FromSeconds(60));
        Assert.Equal(MachineStop.TitleExited, stats.Stop);
        Assert.True(sw.Elapsed.TotalMilliseconds >= stats.VirtualMs - 20, $"wall {sw.Elapsed.TotalMilliseconds} ms < emulated {stats.VirtualMs} ms");
        Assert.InRange(stats.VirtualMs, 500, 600);
        Assert.NotNull(s.LatestFrame);
        Assert.Equal(640, s.LatestFrame.Width);
    }

    [Fact]
    public void Unpaced_session_runs_idle_time_faster_than_real_time()
    {
        using var s = new EmulationSession(Boot("xpc-video-test")) { Paced = false };
        s.Start();
        var stats = WaitStopped(s, TimeSpan.FromSeconds(60));
        Assert.Equal(MachineStop.TitleExited, stats.Stop);
        Assert.True(stats.Speed > 0);
        Assert.Contains("vblanks=30", s.Machine.Kernel.DebugOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Posted_input_reaches_the_title_and_pause_freezes_emulated_time()
    {
        var m = Boot("xpc-input-test");
        m.Input.Profile = InputProfile.CreateDefault() with { Mode = InputMode.KeyboardMouse };
        using var s = new EmulationSession(m) { Paced = false };
        s.Post(x => x.Input.OnFocusChanged(true));
        var frame = new HostInputFrame();
        frame.Keys.Add("Space");
        s.SubmitInput(frame, 1.0 / 60, 0);
        s.Start();

        var sw = Stopwatch.StartNew();
        while (s.Stats.VirtualMs < 1000 && sw.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(5);
        s.Pause();
        Thread.Sleep(100);
        var frozen = s.Stats.VirtualMs;
        Thread.Sleep(200);
        Assert.Equal(SessionState.Paused, s.Stats.State);
        Assert.Equal(frozen, s.Stats.VirtualMs);
        s.Resume();

        var stats = WaitStopped(s, TimeSpan.FromSeconds(60));
        Assert.Equal(MachineStop.TitleExited, stats.Stop);
        Assert.Contains("a=255", m.Kernel.DebugOutput, StringComparison.Ordinal);
        Assert.Equal(new RumbleState(0x1234, 0x5678), m.Gamepads[0].LastRumble);
    }

    [Fact]
    public void Stop_ends_the_thread_and_reports_it()
    {
        using var s = new EmulationSession(Boot("xpc-input-test"));
        s.Start();
        Thread.Sleep(200);
        s.Stop();
        Assert.Equal(SessionState.Stopped, s.Stats.State);
        Assert.Null(s.Stats.Stop);
    }
}
