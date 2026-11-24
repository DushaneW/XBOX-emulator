using System.Security.Cryptography;
using XboxPc.Emulation.Input;
using XboxPc.Emulation.OriginalXbox;
using Xunit.Abstractions;

namespace XboxPc.Emulation.Tests.OriginalXbox;

/// <summary>
/// End-to-end runs of the original hardware test programs in homebrew/ (built with nxdk, binaries copied to Assets/).
/// Each program exercises one emulated subsystem through the code path a real title uses; expected values follow
/// from the programs' own definitions (pattern formula, file contents, key bindings), not from the emulator.
/// </summary>
public class HardwareProgramTests(ITestOutputHelper output)
{
    private static string Dir(string program) => Path.Combine(AppContext.BaseDirectory, "Assets", program);

    private static OriginalXboxMachine Boot(string program, string? harddisk = null)
    {
        var path = Path.Combine(Dir(program), "default.xbe");
        var file = File.ReadAllBytes(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(file));
        Assert.Equal(File.ReadAllText(path + ".sha256").Trim(), hash);
        var machine = new OriginalXboxMachine();
        machine.MountDefaults(Dir(program), harddisk);
        machine.Load(XbeImage.Parse(file), file);
        return machine;
    }

    private void Report(OriginalXboxMachine m, MachineRunResult r) =>
        output.WriteLine($"{r.Stop}: {r.Detail}; {m.Cycles:N0} cycles; {m.InterruptsDelivered} irq; DbgPrint:\n{m.Kernel.DebugOutput}");

    [Fact]
    public void Video_test_framebuffer_matches_pattern_and_vblank_interrupts_pace_time()
    {
        var m = Boot("xpc-video-test");
        var r = m.Run(200_000_000);
        Report(m, r);
        Assert.Equal(MachineStop.TitleExited, r.Stop);

        // 30 waits for vertical blank went through ISR -> DPC -> event; at 60 Hz that is 500 ms of system time.
        var line = Assert.Single(m.Kernel.DebugOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("XPC-VIDEO mode=640x480x32 vblanks=30 elapsed_ms=", line, StringComparison.Ordinal);
        var ms = int.Parse(line[(line.LastIndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(ms, 483, 517); // +- one frame: the first wait may end at the very next vblank
        Assert.InRange(m.InterruptsDelivered, 30, 32);
        Assert.Equal(m.InterruptsDelivered, m.DpcsExecuted);

        var frame = m.Gpu.ScanOut();
        Assert.NotNull(frame);
        Assert.Equal((640, 480), (frame.Width, frame.Height));
        for (var y = 0; y < 480; y += 7)
            for (var x = 0; x < 640; x += 5)
            {
                var expected = 0xFF000000u | ((uint)(x & 0xFF) << 16) | ((uint)(y & 0xFF) << 8) | (uint)((x ^ y) & 0xFF);
                Assert.Equal(expected, frame.Bgra[(y * 640) + x]);
            }
    }

    [Fact]
    public void Filesystem_test_reads_disc_refuses_disc_writes_and_round_trips_harddisk_files()
    {
        var hdd = Directory.CreateTempSubdirectory("xpc-fs-");
        try
        {
            var m = Boot("xpc-fs-test", hdd.FullName);
            var r = m.Run(200_000_000);
            Report(m, r);
            Assert.Equal(MachineStop.TitleExited, r.Stop);
            var discXbeSize = new FileInfo(Path.Combine(Dir("xpc-fs-test"), "default.xbe")).Length;
            var expected = $"""
                XPC-FS begin
                read D:input.txt n=24 text=Hello from the XPC disc!
                write to D: refused
                D: entry default.xbe dir=0 size={discXbeSize}
                D: entry default.xbe.sha256 dir=0 size=65
                D: entry input.txt dir=0 size=24
                D: entries=3
                mount E: ok
                wrote 3000 bytes checksum={Fnv(Block()):x8}
                read back 3000 bytes checksum={Fnv(Block()):x8} byte1000={Block()[1000]} size=3000
                rename ok old_exists=0 new_exists=1
                disk space query ok nonzero_total=1
                delete of missing file failed as expected; keep.txt written
                XPC-FS end

                """.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Equal(expected, m.Kernel.DebugOutput);

            // Host side of the sandbox: exactly what the title left behind.
            Assert.False(File.Exists(Path.Combine(hdd.FullName, "xpc", "data.bin")));
            Assert.Equal(Block(), File.ReadAllBytes(Path.Combine(hdd.FullName, "xpc", "renamed.bin")));
            Assert.Equal("kept by xpc-fs-test\n", File.ReadAllText(Path.Combine(hdd.FullName, "xpc", "keep.txt")));
            Assert.False(File.Exists(Path.Combine(Dir("xpc-fs-test"), "nope.txt")));
        }
        finally { hdd.Delete(true); }
    }

    private static byte[] Block() => Enumerable.Range(0, 3000).Select(i => (byte)((i * 31) + 7)).ToArray();

    private static uint Fnv(byte[] data)
    {
        var h = 2166136261u;
        foreach (var b in data) { h ^= b; h *= 16777619u; }
        return h;
    }

    [Fact]
    public void Input_test_receives_keyboard_state_through_usb_xid_and_sends_rumble_back()
    {
        var m = Boot("xpc-input-test");
        m.Input.Profile = InputProfile.CreateDefault() with { Mode = InputMode.KeyboardMouse };
        m.Input.OnFocusChanged(true);
        var now = 0.0;
        void Hold(params string[] keys)
        {
            var frame = new HostInputFrame();
            foreach (var k in keys) frame.Keys.Add(k);
            now += 1000.0 / 60;
            m.Input.Update(frame, 1.0 / 60, (long)now);
        }

        // Phase 1: idle until nxdk's USB stack has enumerated and configured the controller.
        Hold();
        MachineRunResult r;
        do r = m.Run(5_000_000);
        while (r.Stop == MachineStop.BudgetExhausted && !m.Kernel.DebugOutput.Contains("connected", StringComparison.Ordinal));
        Assert.Contains("XPC-INPUT connected vid=045e pid=0202 type=0101\n", m.Kernel.DebugOutput, StringComparison.Ordinal);
        Assert.True(m.Gamepads[0].Configured);
        Assert.NotEqual(0, m.Gamepads[0].Address);

        // Phase 2: press A (Space) and left stick up (W); wait until the title reports it and answers with rumble.
        Hold("Space", "W");
        do r = m.Run(5_000_000);
        while (r.Stop == MachineStop.BudgetExhausted && !m.Kernel.DebugOutput.Contains("rumble sent", StringComparison.Ordinal));

        // Phase 3: release everything; the next report must show neutral state.
        Hold();
        do r = m.Run(20_000_000);
        while (r.Stop == MachineStop.BudgetExhausted);
        Report(m, r);
        Assert.Equal(MachineStop.TitleExited, r.Stop);

        var reports = m.Kernel.DebugOutput.Split('\n').Where(l => l.StartsWith("XPC-INPUT report", StringComparison.Ordinal)).ToList();
        Assert.Contains("XPC-INPUT report buttons=0000 a=255 b=0 x=0 y=0 black=0 white=0 lt=0 rt=0 lx=0 ly=32767 rx=0 ry=0", reports);
        Assert.Equal("XPC-INPUT report buttons=0000 a=0 b=0 x=0 y=0 black=0 white=0 lt=0 rt=0 lx=0 ly=0 rx=0 ry=0", reports[^1]);
        Assert.Contains("XPC-INPUT rumble sent\n", m.Kernel.DebugOutput, StringComparison.Ordinal);
        Assert.Contains($"XPC-INPUT end reports={reports.Count}\n", m.Kernel.DebugOutput, StringComparison.Ordinal);

        // The rumble OUT report reached the emulated controller and the input engine.
        Assert.Equal(new RumbleState(0x1234, 0x5678), m.Gamepads[0].LastRumble);
        Assert.Equal(new RumbleState(0x1234, 0x5678), m.Input.Rumble(0));
        Assert.True(m.InterruptsDelivered > 0);
    }
}
