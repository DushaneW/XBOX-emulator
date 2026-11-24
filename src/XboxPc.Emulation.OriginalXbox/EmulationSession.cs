using System.Collections.Concurrent;
using System.Diagnostics;
using XboxPc.Emulation.Gpu.Nv2a;
using XboxPc.Emulation.Input;

namespace XboxPc.Emulation.OriginalXbox;

public enum SessionState
{
    Created,
    Running,
    Paused,

    /// <summary>The title stopped (exit, unsupported feature, error), see <see cref="SessionStats.Stop"/>.</summary>
    Stopped,
}

/// <summary>Snapshot of a running session, safe to read from any thread.</summary>
public sealed record SessionStats(
    SessionState State,
    long Instructions,
    double Mips,
    double VirtualMs,
    double WallMs,
    long VBlanks,
    long Interrupts,
    long UsbFrames,
    MachineStop? Stop,
    string Detail,
    int UnmodelledCount,
    string? LastUnmodelled,
    long Flips = 0)
{
    /// <summary>Emulated time / real time while running; 1.0 = full speed (paced), below 1 = slower than a real Xbox.</summary>
    public double Speed => WallMs > 0 ? VirtualMs / WallMs : 0;
}

/// <summary>
/// Runs one <see cref="OriginalXboxMachine"/> on a dedicated thread, paced so that emulated time never runs ahead of
/// real time (it may fall behind when the interpreter is too slow; <see cref="SessionStats.Speed"/> shows this).
/// All machine access from other threads goes through <see cref="Post"/>, executed between execution slices, so the
/// machine itself never needs locking. Frames are published after every new vertical blank.
/// </summary>
public sealed class EmulationSession : IDisposable
{
    /// <summary>Maximum emulated time per slice; bounds the latency of posted commands (input, pause, stop).</summary>
    public const double SliceMs = 4;

    private readonly ConcurrentQueue<Action<OriginalXboxMachine>> _commands = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Stopwatch _wall = new();
    private Thread? _thread;
    private volatile bool _quit;
    private volatile bool _paused;
    private volatile Frame? _frame;
    private volatile SessionStats _stats;
    private long _lastVBlank = -1;
    private long _instructions;

    public EmulationSession(OriginalXboxMachine machine)
    {
        Machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _stats = new SessionStats(SessionState.Created, 0, 0, 0, 0, 0, 0, 0, null, "", 0, null);
    }

    /// <summary>The machine. Only touch it from <see cref="Post"/> callbacks or after the session stopped.</summary>
    public OriginalXboxMachine Machine { get; }

    /// <summary>Real-time pacing (default). Off = run as fast as the interpreter allows (benchmarks).</summary>
    public bool Paced { get; init; } = true;

    public Frame? LatestFrame => _frame;

    public SessionStats Stats => _stats;

    /// <summary>Raised on the emulation thread when the title stops for any reason.</summary>
    public event EventHandler<SessionStats>? Stopped;

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("Session already started.");
        _thread = new Thread(Loop) { IsBackground = true, Name = "XPC emulation", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Queues work for the emulation thread (input, settings). Runs before the next slice.</summary>
    public void Post(Action<OriginalXboxMachine> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands.Enqueue(command);
        _wake.Set();
    }

    public void Pause() { _paused = true; _wake.Set(); }

    public void Resume() { _paused = false; _wake.Set(); }

    public bool IsPaused => _paused;

    private void Loop()
    {
        var m = Machine;
        var sliceCycles = (long)(OriginalXboxMachine.CpuHz * SliceMs / 1000);
        _wall.Start();
        try
        {
            while (!_quit)
            {
                Drain();
                if (_paused)
                {
                    if (_wall.IsRunning) _wall.Stop();
                    Publish(SessionState.Paused, null, "Paused.");
                    _wake.Wait(100);
                    _wake.Reset();
                    continue;
                }
                if (!_wall.IsRunning) _wall.Start();

                if (Paced)
                {
                    // Never run ahead of real time: wait until the wall clock catches up with emulated time.
                    var ahead = VirtualMs(m) - _wall.Elapsed.TotalMilliseconds;
                    if (ahead > 1)
                    {
                        _wake.Wait(TimeSpan.FromMilliseconds(Math.Min(ahead, 50)));
                        _wake.Reset();
                        continue;
                    }
                }
                m.CycleLimit = m.Cycles + sliceCycles;
                var r = m.Run(long.MaxValue);
                _instructions += r.Instructions;
                PublishFrame(m);
                if (r.Stop != MachineStop.BudgetExhausted)
                {
                    var stats = Publish(SessionState.Stopped, r.Stop, r.Detail);
                    Stopped?.Invoke(this, stats);
                    return;
                }
                Publish(SessionState.Running, null, "");
            }
            Publish(SessionState.Stopped, null, "Stopped by the user.");
        }
#pragma warning disable CA1031 // A failure inside the emulator must end the session with a report, not kill the process.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var stats = Publish(SessionState.Stopped, MachineStop.CpuException, "Emulator failure: " + ex.Message);
            Stopped?.Invoke(this, stats);
        }
    }

    private static double VirtualMs(OriginalXboxMachine m) => m.Cycles * 1000.0 / OriginalXboxMachine.CpuHz;

    private void Drain()
    {
        while (_commands.TryDequeue(out var c)) c(Machine);
    }

    private void PublishFrame(OriginalXboxMachine m)
    {
        if (m.Gpu.VBlankCount == _lastVBlank) return;
        _lastVBlank = m.Gpu.VBlankCount;
        if (m.Gpu.ScanOut() is { } f) _frame = f;
    }

    private SessionStats Publish(SessionState state, MachineStop? stop, string detail)
    {
        var m = Machine;
        var wallMs = _wall.Elapsed.TotalMilliseconds;
        var unmodelled = m.UnmodelledAccesses;
        var s = new SessionStats(state, _instructions, wallMs > 0 ? _instructions / wallMs / 1000 : 0, VirtualMs(m), wallMs,
            m.Gpu.VBlankCount, m.InterruptsDelivered, m.Usb.Frames, stop, detail, unmodelled.Count, unmodelled.Count > 0 ? unmodelled[^1] : null, m.Gpu.FlipCount);
        _stats = s;
        return s;
    }

    /// <summary>Stops the emulation thread and waits for it (the machine is then safe to inspect).</summary>
    public void Stop()
    {
        _quit = true;
        Machine.StopRequested = true;
        _wake.Set();
        _thread?.Join();
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }

    /// <summary>Convenience: applies a host input frame to the machine's input engine on the emulation thread.</summary>
    public void SubmitInput(HostInputFrame frame, double dtSeconds, long nowMs) => Post(m => m.Input.Update(frame, dtSeconds, nowMs));
}
