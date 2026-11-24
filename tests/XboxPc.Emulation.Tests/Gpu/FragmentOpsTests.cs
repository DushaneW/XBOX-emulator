using System.Numerics;
using System.Security.Cryptography;
using XboxPc.Emulation.Gpu.Nv2a;
using XboxPc.Emulation.OriginalXbox;
using Xunit.Abstractions;

namespace XboxPc.Emulation.Tests.Gpu;

/// <summary>Blending, alpha test, stencil and logic ops: unit level and the xpc-blend-test program end to end.</summary>
public class FragmentOpsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0x1E00u, 5u, 9u, 5u)]     // KEEP
    [InlineData(0x0000u, 5u, 9u, 0u)]     // ZERO
    [InlineData(0x1E01u, 5u, 9u, 9u)]     // REPLACE
    [InlineData(0x1E02u, 255u, 9u, 255u)] // INCR_SAT saturates
    [InlineData(0x1E03u, 0u, 9u, 0u)]     // DECR_SAT saturates
    [InlineData(0x150Au, 0x0Fu, 9u, 0xF0u)] // INVERT
    [InlineData(0x8507u, 255u, 9u, 0u)]   // INCR wraps
    [InlineData(0x8508u, 0u, 9u, 255u)]   // DECR wraps
    public void Stencil_operations(uint op, uint stencil, uint reference, uint expected) =>
        Assert.Equal(expected, FragmentOps.StencilOp(op, stencil, reference));

    [Theory]
    [InlineData(0x1500u, 0u)]
    [InlineData(0x1501u, 0x0F00_0F00u)]
    [InlineData(0x1503u, 0x0F0F_0F0Fu)]
    [InlineData(0x1505u, 0xFF00_FF00u)]
    [InlineData(0x1506u, 0xF00F_F00Fu)]
    [InlineData(0x1507u, 0xFF0F_FF0Fu)]
    [InlineData(0x150Au, 0x00FF_00FFu)]
    [InlineData(0x150Fu, 0xFFFF_FFFFu)]
    public void Logic_operations(uint op, uint expected) => Assert.Equal(expected, FragmentOps.Logic(op, 0x0F0F_0F0F, 0xFF00_FF00));

    private static FragmentOps Blend(uint sf, uint df, uint eq, uint constant = 0) =>
        FragmentOps.FromState(m => m switch { 0x304 => 1u, 0x344 => sf, 0x348 => df, 0x350 => eq, 0x34C => constant, _ => 0u });

    [Fact]
    public void Blend_factors_and_equations()
    {
        var src = new Vector4(0.8f, 0.4f, 0.2f, 0.25f);
        var dst = new Vector4(0.2f, 0.6f, 1.0f, 0.5f);
        Assert.Equal((src * 0.25f) + (dst * 0.75f), Blend(0x302, 0x303, 0x8006).BlendColor(src, dst));
        Assert.Equal(Vector4.Clamp(src + dst, Vector4.Zero, Vector4.One), Blend(1, 1, 0x8006).BlendColor(src, dst));
        Assert.Equal(Vector4.Clamp(src - dst, Vector4.Zero, Vector4.One), Blend(1, 1, 0x800A).BlendColor(src, dst));
        Assert.Equal(Vector4.Clamp(dst - src, Vector4.Zero, Vector4.One), Blend(1, 1, 0x800B).BlendColor(src, dst));
        Assert.Equal(Vector4.Min(src, dst), Blend(0, 0, 0x8007).BlendColor(src, dst)); // MIN/MAX ignore the factors
        Assert.Equal(Vector4.Max(src, dst), Blend(0, 0, 0x8008).BlendColor(src, dst));
        Assert.Equal(src * dst, Blend(0x306, 0, 0x8006).BlendColor(src, dst));        // DST_COLOR
        Assert.Equal(dst * new Vector4(0.5f), Blend(0, 0x304, 0x8006).BlendColor(src, dst)); // DST_ALPHA
        // SRC_ALPHA_SATURATE: min(As, 1 - Ad) for colour, 1 for alpha
        var sat = Blend(0x308, 0, 0x8006).BlendColor(src, dst);
        Assert.Equal(new Vector4(0.8f * 0.25f, 0.4f * 0.25f, 0.2f * 0.25f, 0.25f), sat);
        // constant colour 0x80FF0000: (1, 0, 0, 0.502)
        var c = Blend(0x8001, 0x8004, 0x8006, 0x80FF0000).BlendColor(src, dst);
        Assert.Equal(src.X + (dst.X * (1 - (0x80 / 255f))), c.X, 5);
        Assert.Equal(dst.Y * (1 - (0x80 / 255f)), c.Y, 5);
    }

    // ------------------------------------------------------------------ end to end

    private static uint Pack(double r, double g, double b, double a)
    {
        static uint B(double v) => (uint)Math.Clamp(Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.ToEven), 0, 255);
        return (B(a) << 24) | (B(r) << 16) | (B(g) << 8) | B(b);
    }

    private static double Q(double v) => Math.Round(v * 255, MidpointRounding.ToEven) / 255; // value after an 8-bit store

    /// <summary>Expected render-target values of xpc-blend-test, derived from its definitions.</summary>
    private static uint[] ExpectedProbes() =>
    [
        Pack(1, 0.75, 0.75, (0.25 * 0.25) + 0.75),                                            // 1 alpha blend
        Pack(Q(0.25) + 0.5, Q(0.5) + 0.25, Q(0.25) + 0.5, 1),                                 // 2 additive (alpha 1+1 clamps)
        Pack(1 - 0.25, 1 - 0.5, 1 - 0.75, 0),                                                 // 3 reverse subtract
        Pack(Math.Min(Q(0.2), 0.6), Math.Min(Q(0.8), 0.4), Math.Min(Q(0.5), 0.5), 1),         // 4 MIN
        0x80406080,                                                                           // 5 constant colour x white
        0xFF404040,                                                                           // 6 left: alpha 0.3 rejected
        Pack(1, 1, 0, 0.7),                                                                   // 6 right: alpha 0.7 passes
        0xFF00FF00, 0xFF0000FF, 0xFF404040,                                                   // 7 stencil: inside, ring, outside
        0xFFFFFFFFu ^ 0xFF00FF00u,                                                            // 8 XOR
    ];

    [Fact]
    public void Blend_program_matches_the_fragment_operation_definitions()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "xpc-blend-test");
        var file = File.ReadAllBytes(Path.Combine(dir, "default.xbe"));
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "default.xbe.sha256")).Trim(), Convert.ToHexStringLower(SHA256.HashData(file)));
        var m = new OriginalXboxMachine();
        m.MountDefaults(dir, null);
        m.Load(XbeImage.Parse(file), file);
        var r = m.Run(400_000_000);
        output.WriteLine($"{r.Stop}: {r.Detail}\n{m.Kernel.DebugOutput}\n{string.Join("\n", m.UnmodelledAccesses.Distinct())}");
        Assert.Equal(MachineStop.TitleExited, r.Stop);
        Assert.DoesNotContain(m.UnmodelledAccesses, u => u.Contains("NV2A", StringComparison.Ordinal) && !u.Contains("class 0x", StringComparison.Ordinal));

        var expected = ExpectedProbes();
        var line = m.Kernel.DebugOutput.Split('\n').Single(l => l.StartsWith("XPC-BLEND 1 ", StringComparison.Ordinal));
        var values = line.Split(' ').Where(t => t.Length == 8).Select(t => Convert.ToUInt32(t, 16)).ToArray();
        Assert.Equal(expected.Select(e => e.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)), values.Select(v => v.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)));

        // displayed frame: interiors of every region (scan-out shows RGB)
        var frame = m.Gpu.ScanOut();
        Assert.NotNull(frame);
        void Region(int x0, int y0, int x1, int y1, uint want)
        {
            for (var y = y0 + 2; y < y1 - 2; y += 4)
                for (var x = x0 + 2; x < x1 - 2; x += 4)
                    Assert.True((want | 0xFF000000) == frame.Bgra[(y * 640) + x], $"pixel ({x},{y}) = {frame.Bgra[(y * 640) + x]:X8}, expected {want | 0xFF000000:X8}");
        }
        Region(40, 40, 104, 104, expected[0]);
        Region(120, 40, 184, 104, expected[1]);
        Region(200, 40, 264, 104, expected[2]);
        Region(280, 40, 344, 104, expected[3]);
        Region(360, 40, 424, 104, expected[4]);
        Region(40, 140, 72, 204, expected[5]);
        Region(72, 140, 104, 204, expected[6]);
        Region(40, 300, 120, 380, expected[7]);
        Region(20, 280, 200, 298, expected[8]);  // ring of the big quad outside the stencilled square
        Region(122, 300, 200, 400, expected[8]);
        Region(440, 40, 504, 104, expected[10]);
    }
}
