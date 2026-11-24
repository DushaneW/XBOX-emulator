using System.Security.Cryptography;
using XboxPc.Emulation.Gpu.Nv2a;
using XboxPc.Emulation.OriginalXbox;
using Xunit.Abstractions;

namespace XboxPc.Emulation.Tests.Gpu;

/// <summary>2D classes, swizzled and anti-aliased render targets, points, lines and the fill rule (xpc-2d-test).</summary>
public class TwoDTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1u, 1)]
    [InlineData(4u, 2)]
    [InlineData(0xAu, 4)]
    [InlineData(0u, 0)]
    public void Surface_formats_have_their_pixel_sizes(uint format, int bytes) => Assert.Equal(bytes, TwoDEngine.SurfaceBytesPerPixel(format));

    [Fact]
    public void Two_d_program_matches_its_definitions()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "xpc-2d-test");
        var file = File.ReadAllBytes(Path.Combine(dir, "default.xbe"));
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "default.xbe.sha256")).Trim(), Convert.ToHexStringLower(SHA256.HashData(file)));
        var m = new OriginalXboxMachine();
        m.MountDefaults(dir, null);
        m.Load(XbeImage.Parse(file), file);
        var r = m.Run(400_000_000);
        output.WriteLine($"{r.Stop}: {r.Detail}\n{m.Kernel.DebugOutput}\n{string.Join("\n", m.UnmodelledAccesses.Distinct())}");
        Assert.Equal(MachineStop.TitleExited, r.Stop);
        Assert.DoesNotContain(m.UnmodelledAccesses, u => u.Contains("NV2A", StringComparison.Ordinal));

        const string bg = "ff404040";
        var o = m.Kernel.DebugOutput;
        // swizzled 64x64 target read back in Morton order: red quadrant, green quadrant, blue clear elsewhere
        // 2x2 supersampled target: samples x 20..59, y 20..39 white (top/left edges included, bottom/right excluded)
        Assert.Contains("XPC-2D swizzle ffff0000 ff00ff00 ff0000ff ff0000ff aa ffffffff ffffffff ff000000 ff000000\n", o, StringComparison.Ordinal);
        // points cover their pixel; lines exclude their last pixel
        Assert.Contains($"XPC-2D points ffffff00 ffffff00 {bg} lines ff00ffff ff00ffff {bg} {bg} ffff00ff ffff00ff {bg}\n", o, StringComparison.Ordinal);
        // fill rule: additive quad covers each pixel of [200,260) x [300,360) exactly once
        Assert.Contains($"XPC-2D fill ff808080 ff808080 ff808080 {bg} {bg} {bg}\n", o, StringComparison.Ordinal);
        // image blit 64x64 from (40,40) to (500,200); memory-to-memory lines 0..7 and 16..23
        Assert.Contains($"XPC-2D blit ffff0000 ff0000ff {bg} m2m 03020100 07060504 13121110 17161514\n", o, StringComparison.Ordinal);
        Assert.Equal(2, m.Gpu.Pgraph.TwoD.Blits);
        Assert.Equal(2, m.Gpu.Pgraph.TwoD.MemoryCopies);
        Assert.Equal(4, m.Gpu.Pgraph.Kelvin.PointsDrawn);
        Assert.Equal(4, m.Gpu.Pgraph.Kelvin.LinesDrawn);

        // displayed frame: whole regions, exact
        var frame = m.Gpu.ScanOut();
        Assert.NotNull(frame);
        uint At(int x, int y) => frame.Bgra[(y * 640) + x];
        for (var y = 295; y < 365; y++)
            for (var x = 195; x < 265; x++)
            {
                var inside = x >= 200 && x < 260 && y >= 300 && y < 360;
                Assert.True((inside ? 0xFF808080u : 0xFF404040u) == At(x, y), $"fill ({x},{y}) = {At(x, y):X8}");
            }
        for (var x = 295; x < 345; x++)
            Assert.Equal(x >= 300 && x < 340 ? 0xFF00FFFFu : 0xFF404040u, At(x, 320));
        for (var y = 295; y < 345; y++)
            Assert.Equal(y >= 300 && y < 340 ? 0xFFFF00FFu : 0xFF404040u, At(360, y));
        for (var y = 200; y < 264; y++)
            for (var x = 500; x < 564; x++)
            {
                var sx = x - 500 + 40;
                var sy = y - 200 + 40;
                var expected = sx >= 72 && sy >= 72 ? 0xFF0000FFu : 0xFFFF0000u;
                Assert.True(expected == At(x, y), $"blit ({x},{y}) = {At(x, y):X8}, expected {expected:X8}");
            }
        Assert.Equal(0xFF404040u, At(564, 230));
        Assert.Equal(0xFF404040u, At(530, 264));
    }
}
