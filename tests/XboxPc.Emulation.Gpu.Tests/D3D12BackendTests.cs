using XboxPc.Emulation.Gpu.D3D12;
using XboxPc.Emulation.Gpu.Nv2a;
using XboxPc.Emulation.OriginalXbox;
using Xunit.Abstractions;

namespace XboxPc.Emulation.Gpu.Tests;

/// <summary>
/// The Direct3D 12 renderer against the CPU reference renderer: every bundled NV2A test program is run once with the
/// backend attached, then the final displayed frame from the GPU is compared pixel by pixel with the CPU scan-out
/// of the same guest framebuffer.
/// </summary>
public class D3D12BackendTests(ITestOutputHelper output)
{
    private sealed record Run(OriginalXboxMachine Machine, D3D12Backend Backend, MachineStop Stop);

    private static Run Boot(string program, int scale, bool owns = false)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", program);
        var file = File.ReadAllBytes(Path.Combine(dir, "default.xbe"));
        var backend = D3D12Backend.TryCreate(scale, useWarp: false, ownsMemory: owns, out var error) ?? throw new InvalidOperationException(error);
        var m = new OriginalXboxMachine();
        m.MountDefaults(dir, null);
        m.Load(XbeImage.Parse(file), file);
        backend.Attach(m.Gpu);
        var r = m.Run(400_000_000);
        return new Run(m, backend, r.Stop);
    }

    /// <summary>CPU scan-out of the current framebuffer (the backend is detached for this read).</summary>
    private static Frame CpuFrame(Nv2aDevice gpu)
    {
        var backend = gpu.Backend;
        gpu.Backend = null;
        try { return gpu.ScanOut() ?? throw new InvalidOperationException("no display mode"); }
        finally { gpu.Backend = backend; }
    }

    private static (int Differing, int MaxDelta) Compare(Frame cpu, Frame gpu, int tolerance)
    {
        var differing = 0;
        var max = 0;
        for (var i = 0; i < cpu.Bgra.Length; i++)
        {
            uint a = cpu.Bgra[i], b = gpu.Bgra[i];
            var d = Math.Max(Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)), Math.Max(Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)), Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF))));
            max = Math.Max(max, d);
            if (d > tolerance) differing++;
        }
        return (differing, max);
    }

    [Theory]
    [InlineData("xpc-3d-test")]
    [InlineData("xpc-texture-test")]
    [InlineData("xpc-ff-test")]
    [InlineData("xpc-ff2-test")]
    [InlineData("xpc-blend-test")]
    public void Gpu_frame_matches_the_cpu_reference_at_native_resolution(string program)
    {
        var run = Boot(program, scale: 1);
        using var backend = run.Backend;
        Assert.Equal(MachineStop.TitleExited, run.Stop);
        var gpu = run.Machine.Gpu;
        var cpuFrame = CpuFrame(gpu);
        var gpuFrame = backend.TryScanOut(gpu.FramebufferStart, gpu.Width, gpu.Height, gpu.Pitch, gpu.VBlankCount);
        output.WriteLine($"{program}: adapter {backend.AdapterName}, draws {backend.Draws}, triangles {backend.Triangles}, shaders {backend.ShaderCount}, pipelines {backend.PipelineCount}, failure {backend.FailureReason ?? "none"}");
        Assert.Null(backend.FailureReason);
        Assert.True(backend.Draws > 0, "the program drew nothing through the backend");
        Assert.NotNull(gpuFrame);
        Assert.Equal(cpuFrame.Width, gpuFrame.Width);
        Assert.Equal(cpuFrame.Height, gpuFrame.Height);
        var (differing, max) = Compare(cpuFrame, gpuFrame, tolerance: 2);
        output.WriteLine($"{program}: {differing} of {cpuFrame.Bgra.Length} pixels differ by more than 2 (max channel difference {max})");
        // Edges may differ by a pixel where the host rasteriser and the CPU edge functions round differently.
        Assert.True(differing <= cpuFrame.Bgra.Length / 200, $"{differing} pixels differ (more than 0.5 %)");
    }

    [Fact]
    public void Picture_drawn_by_2d_blits_falls_back_to_the_cpu_image()
    {
        var run = Boot("xpc-2d-test", scale: 1);
        using var backend = run.Backend;
        Assert.Equal(MachineStop.TitleExited, run.Stop);
        var gpu = run.Machine.Gpu;
        // The final picture contains 2D-engine copies the host GPU did not draw: the backend must not offer it.
        Assert.Null(backend.TryScanOut(gpu.FramebufferStart, gpu.Width, gpu.Height, gpu.Pitch, gpu.VBlankCount));
        Assert.NotNull(gpu.ScanOut()); // the CPU picture is shown instead
    }

    [Fact]
    public void Scale_renders_the_same_picture_at_four_times_the_pixels()
    {
        var run = Boot("xpc-3d-test", scale: 2);
        using var backend = run.Backend;
        var gpu = run.Machine.Gpu;
        var cpuFrame = CpuFrame(gpu);
        var big = backend.TryScanOut(gpu.FramebufferStart, gpu.Width, gpu.Height, gpu.Pitch, gpu.VBlankCount);
        Assert.NotNull(big);
        Assert.Equal(cpuFrame.Width * 2, big.Width);
        Assert.Equal(cpuFrame.Height * 2, big.Height);
        // each guest pixel's area in the 2x image, sampled at its centre-ish host pixel, matches the CPU colour
        var differing = 0;
        for (var y = 0; y < cpuFrame.Height; y++)
            for (var x = 0; x < cpuFrame.Width; x++)
            {
                uint a = cpuFrame.Bgra[(y * cpuFrame.Width) + x], b = big.Bgra[(((y * 2) + 1) * big.Width) + (x * 2) + 1];
                var d = Math.Max(Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)), Math.Max(Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)), Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF))));
                if (d > 4) differing++;
            }
        output.WriteLine($"2x: {differing} of {cpuFrame.Bgra.Length} guest pixels differ by more than 4");
        Assert.True(differing <= cpuFrame.Bgra.Length / 100, $"{differing} pixels differ (more than 1 %)");
    }
}

/// <summary>
/// The GPU in charge: the CPU rasteriser is off, Direct3D 12 renders, and guest memory is kept coherent on access.
/// Every test program must behave exactly as with the CPU renderer alone, including the pixel values it reads back
/// from its own render targets (printed in its debug output) and the displayed frame.
/// </summary>
public class GpuOwnedTests(ITestOutputHelper output)
{
    private static (OriginalXboxMachine Machine, MachineStop Stop, long Ms) Run(string program, D3D12Backend? backend, long budget = 400_000_000)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", program);
        var file = File.ReadAllBytes(Path.Combine(dir, "default.xbe"));
        var m = new OriginalXboxMachine();
        m.MountDefaults(dir, null);
        m.Load(XbeImage.Parse(file), file);
        backend?.Attach(m.Gpu);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = m.Run(budget);
        return (m, r.Stop, sw.ElapsedMilliseconds);
    }

    [Theory]
    [InlineData("xpc-3d-test")]
    [InlineData("xpc-texture-test")]
    [InlineData("xpc-ff-test")]
    [InlineData("xpc-ff2-test")]
    [InlineData("xpc-blend-test")]
    [InlineData("xpc-2d-test")]
    public void Gpu_owned_rendering_behaves_exactly_like_the_cpu_renderer(string program)
    {
        var cpu = Run(program, null);
        using var backend = D3D12Backend.TryCreate(1, useWarp: false, ownsMemory: true, out var error) ?? throw new InvalidOperationException(error);
        var gpu = Run(program, backend);
        output.WriteLine($"{program}: CPU renderer {cpu.Ms} ms, GPU {gpu.Ms} ms; draws {backend.Draws}, uploads {backend.Uploads}, downloads {backend.Downloads}, GPU 2D blits {backend.GpuBlits}, render-target textures {backend.TargetTextures}");
        foreach (var msg in backend.Messages) output.WriteLine("  note: " + msg);
        foreach (var line in backend.DescribeTargets()) output.WriteLine("  target: " + line);
        foreach (var line in backend.Trace) output.WriteLine("  trace: " + line);
        Assert.Null(backend.FailureReason);
        Assert.Equal(cpu.Stop, gpu.Stop);
        Assert.Equal(0, gpu.Machine.Gpu.Pgraph.Kelvin.PixelsWritten); // the CPU rasteriser did not run

        // What the program read back from its own images (printed by it) is the same. Colour values may differ by
        // 1/255 per channel where blending lands exactly on a rounding tie (host blend units round ties their own way).
        AssertSameOutput(cpu.Machine.Kernel.DebugOutput, gpu.Machine.Kernel.DebugOutput);

        // the displayed frame matches
        var cpuFrame = cpu.Machine.Gpu.ScanOut()!;
        var gpuFrame = gpu.Machine.Gpu.ScanOut()!;
        Assert.Equal(cpuFrame.Width, gpuFrame.Width);
        var differing = 0;
        for (var i = 0; i < cpuFrame.Bgra.Length; i++)
        {
            uint a = cpuFrame.Bgra[i], b = gpuFrame.Bgra[i];
            var d = Math.Max(Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)), Math.Max(Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)), Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF))));
            if (d > 2) differing++;
        }
        output.WriteLine($"{program}: {differing} of {cpuFrame.Bgra.Length} displayed pixels differ by more than 2");
        Assert.True(differing <= cpuFrame.Bgra.Length / 200, $"{differing} pixels differ");
    }

    private void AssertSameOutput(string expected, string actual)
    {
        var e = expected.Split([' ', '\n'], StringSplitOptions.None);
        var a = actual.Split([' ', '\n'], StringSplitOptions.None);
        Assert.Equal(e.Length, a.Length);
        var rounded = 0;
        for (var i = 0; i < e.Length; i++)
        {
            if (e[i] == a[i]) continue;
            var isColour = e[i].Length == 8 && uint.TryParse(e[i], System.Globalization.NumberStyles.HexNumber, null, out var ev)
                & uint.TryParse(a[i], System.Globalization.NumberStyles.HexNumber, null, out var av);
            Assert.True(isColour, $"output differs at token {i}: expected '{e[i]}', got '{a[i]}'");
            uint.TryParse(e[i], System.Globalization.NumberStyles.HexNumber, null, out ev);
            uint.TryParse(a[i], System.Globalization.NumberStyles.HexNumber, null, out av);
            for (var shift = 0; shift < 32; shift += 8)
                Assert.True(Math.Abs((int)((ev >> shift) & 0xFF) - (int)((av >> shift) & 0xFF)) <= 1, $"colour {e[i]} read back as {a[i]}");
            rounded++;
        }
        output.WriteLine($"read-back values: {rounded} differ by a 1/255 rounding tie, the rest are identical");
    }

    /// <summary>The depth/stencil buffer the title last used, read through the bus (a GPU-held buffer is written back on access).</summary>
    private static uint[] ReadZeta(OriginalXboxMachine m, int width, int height)
    {
        var k = m.Gpu.Pgraph.Kelvin;
        var address = DmaObject.Load(m.Gpu, k.State(0x198)).Address + k.State(0x214);
        var pitch = (int)(k.State(0x20C) >> 16);
        var words = new uint[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                words[(y * width) + x] = m.Bus.Read32(address + (uint)((y * pitch) + (x * 4)));
        return words;
    }

    [Theory]
    [InlineData("xpc-3d-test")]
    [InlineData("xpc-blend-test")]
    public void Depth_and_stencil_written_by_the_gpu_reach_guest_memory(string program)
    {
        var cpu = Run(program, null);
        using var backend = D3D12Backend.TryCreate(2, useWarp: false, ownsMemory: true, out var error) ?? throw new InvalidOperationException(error);
        var gpu = Run(program, backend);
        Assert.Null(backend.FailureReason);
        var a = ReadZeta(cpu.Machine, 640, 480);
        var b = ReadZeta(gpu.Machine, 640, 480);
        int depthOff = 0, stencilOff = 0, maxDepth = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = Math.Abs((int)(a[i] >> 8) - (int)(b[i] >> 8));
            maxDepth = Math.Max(maxDepth, d);
            if (d > 2) depthOff++;
            if ((a[i] & 0xFF) != (b[i] & 0xFF)) stencilOff++;
        }
        output.WriteLine($"{program}: depth differs by more than 2 units at {depthOff} samples (max {maxDepth}), stencil differs at {stencilOff}; trace: {string.Join(" | ", backend.Trace.Where(t => t.Contains("depth", StringComparison.Ordinal)))}");
        Assert.Contains(backend.Trace, t => t.StartsWith("download depth", StringComparison.Ordinal));
        Assert.Equal(0, stencilOff);
        Assert.True(depthOff <= a.Length / 500, $"{depthOff} depth samples differ");
    }

    [Fact]
    public void Continuous_demo_is_faster_with_the_gpu_in_charge()
    {
        const long budget = 120_000_000; // ~0.16 s of emulated time
        var cpu = Run("xpc-spin", null, budget);
        using var backend = D3D12Backend.TryCreate(2, useWarp: false, ownsMemory: true, out var error) ?? throw new InvalidOperationException(error);
        var gpu = Run("xpc-spin", backend, budget);
        output.WriteLine($"xpc-spin, {budget:N0} cycles: CPU renderer {cpu.Ms} ms (vblanks {cpu.Machine.Gpu.VBlankCount}, flips {cpu.Machine.Gpu.FlipCount}); GPU at 2x {gpu.Ms} ms (vblanks {gpu.Machine.Gpu.VBlankCount}, flips {gpu.Machine.Gpu.FlipCount}, draws {backend.Draws})");
        Assert.Null(backend.FailureReason);
        Assert.Equal(MachineStop.BudgetExhausted, gpu.Stop);
        var frame = gpu.Machine.Gpu.ScanOut();
        Assert.NotNull(frame);
        Assert.Equal(1280, frame.Width); // the displayed picture is the 2x host image
        var centre = frame.Bgra[(480 * 1280) + 640] & 0xFFFFFF;
        output.WriteLine($"centre pixel {centre:X6}");
        Assert.NotEqual(0u, centre); // the cube is drawn, not a black screen
        Assert.True(gpu.Ms < cpu.Ms, "the GPU path must be faster");
    }
}

public class SessionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Paced_session_publishes_frames(bool gpu)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "xpc-spin");
        var file = File.ReadAllBytes(Path.Combine(dir, "default.xbe"));
        using var backend = gpu ? D3D12Backend.TryCreate(2, useWarp: false, ownsMemory: true, out var error) ?? throw new InvalidOperationException(error) : null;
        var m = new OriginalXboxMachine();
        m.MountDefaults(dir, null);
        m.Load(XbeImage.Parse(file), file);
        backend?.Attach(m.Gpu);
        using var session = new EmulationSession(m);
        session.Start();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 6000 && session.LatestFrame is null) Thread.Sleep(50);
        var firstMs = sw.ElapsedMilliseconds;
        Thread.Sleep(5000);
        var s = session.Stats;
        output.WriteLine($"first frame after {firstMs} ms; frame {(session.LatestFrame is { } f ? $"{f.Width}x{f.Height}" : "none")}; state {s.State} {s.Stop} {s.Detail}; vblanks {s.VBlanks} flips {s.Flips} speed {s.Speed:P0} mips {s.Mips:F1}; gpu {gpu} draws {backend?.Draws} frames {backend?.FramesPresented} failure {backend?.FailureReason ?? "none"}; display {m.Gpu.Width}x{m.Gpu.Height}");
        session.Stop();
        output.WriteLine($"cycles {m.Cycles:N0}, idle skipped {m.IdleCyclesSkipped:N0}, retired {m.Cpu.Retired:N0}, eip 0x{m.Cpu.Eip:X8}, threads {string.Join(", ", m.Threads.Select(t => $"{t.Id}:{t.State}"))}");
        output.WriteLine($"kernel calls (last 12): {string.Join(" | ", m.Kernel.Trace.TakeLast(12).Select(c => c.ToString()))}");
        output.WriteLine(m.Kernel.DebugOutput);
        Assert.NotNull(session.LatestFrame);
        if (gpu) Assert.True(s.Flips > 30, "with the GPU in charge the title presents frames in real time");
        if (gpu) Assert.Equal(1280, session.LatestFrame!.Width);
    }
}
