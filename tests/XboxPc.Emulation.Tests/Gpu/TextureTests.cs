using System.Numerics;
using System.Security.Cryptography;
using XboxPc.Emulation.Core;
using XboxPc.Emulation.Gpu.Nv2a;
using XboxPc.Emulation.OriginalXbox;
using Xunit.Abstractions;

namespace XboxPc.Emulation.Tests.Gpu;

/// <summary>NV2A texture fetch: layouts, formats, addressing, filtering, and the xpc-texture-test program end to end.</summary>
public class TextureTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0, 0, 0u)]
    [InlineData(1, 0, 1u)]
    [InlineData(0, 1, 2u)]
    [InlineData(1, 1, 3u)]
    [InlineData(2, 0, 4u)]
    [InlineData(3, 3, 15u)]
    public void Square_swizzle_is_morton_order_starting_with_x(int x, int y, uint expected) =>
        Assert.Equal(expected, Textures.SwizzleOffset(x, y, 8, 8));

    [Fact]
    public void Non_square_swizzle_interleaves_only_while_both_dimensions_have_bits()
    {
        // 8x2: x0 y0 x1 x2 -> offset = x0 | y0<<1 | x1<<2 | x2<<3
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 8; x++)
                Assert.Equal((uint)((x & 1) | (y << 1) | ((x >> 1) << 2)), Textures.SwizzleOffset(x, y, 8, 2));
        // 2x4: x0 y0 y1 -> offset = x0 | y0<<1 | y1<<2
        Assert.Equal(1u | 2u | 4u, Textures.SwizzleOffset(1, 3, 2, 4));
    }

    [Theory]
    [InlineData(1, -1, 4, 3)]
    [InlineData(1, 5, 4, 1)]
    [InlineData(2, -1, 4, 0)]
    [InlineData(2, 4, 4, 3)]
    [InlineData(2, 5, 4, 2)]
    [InlineData(3, -3, 4, 0)]
    [InlineData(3, 9, 4, 3)]
    [InlineData(4, 4, 4, -1)]
    public void Addressing_modes(int mode, int i, int size, int expected) => Assert.Equal(expected, Textures.Wrap(i, size, mode));

    [Fact]
    public void Formats_decode_channels()
    {
        Assert.Equal(new Vector4(1, 0, 0, 1), Textures.Decode(0x05, 0xF800));
        Assert.Equal(new Vector4(0, 1, 0, 0), Textures.Decode(0x02, 0x03E0));
        Assert.Equal(new Vector4(1, 0, 0, 0.6f), Textures.Decode(0x04, 0x9F00) with { W = MathF.Round(Textures.Decode(0x04, 0x9F00).W, 1) });
        Assert.Equal(new Vector4(0x11 / 255f, 0x22 / 255f, 0x33 / 255f, 0x44 / 255f), Textures.Decode(0x06, 0x44112233));
        Assert.Equal(new Vector4(0x11 / 255f, 0x22 / 255f, 0x33 / 255f, 0x44 / 255f), Textures.Decode(0x3A, 0x44332211));
        Assert.Equal(1f, Textures.Decode(0x07, 0x00112233).W);
    }

    private static PhysicalBus Ram(out byte[] ram)
    {
        ram = new byte[0x1_0000];
        var bus = new PhysicalBus();
        bus.MapRam("RAM", 0, ram);
        return bus;
    }

    [Fact]
    public void Dxt5_interpolated_alpha_and_dxt3_explicit_alpha()
    {
        var bus = Ram(out var ram);
        // DXT5 block: a0=255 a1=0 (8-step mode), codes for texel 0..: 0,1,2,... ; colour all c0 white
        ram[0] = 255; ram[1] = 0;
        ulong codes = 0;
        for (var t = 0; t < 16; t++) codes |= (ulong)(t % 8) << (3 * t);
        for (var b = 0; b < 6; b++) ram[2 + b] = (byte)(codes >> (8 * b));
        ram[8] = 0xFF; ram[9] = 0xFF; // c0 white, c1 black, indices 0
        var dxt5 = new TextureUnit { Address = 0, Format = 0x0F, Width = 4, Height = 4, Compressed = true, Normalized = true };
        Assert.Equal(1f, Textures.Texel(bus, dxt5, 0, 0).W);
        Assert.Equal(0f, Textures.Texel(bus, dxt5, 1, 0).W);
        Assert.Equal(6 * 255 / 7f / 255f, Textures.Texel(bus, dxt5, 2, 0).W, 5); // code 2 = (6*a0 + 1*a1)/7
        Assert.Equal(new Vector3(1), new Vector3(Textures.Texel(bus, dxt5, 2, 0).X, Textures.Texel(bus, dxt5, 2, 0).Y, Textures.Texel(bus, dxt5, 2, 0).Z));

        Array.Clear(ram);
        ram[0x100] = 0xF0; // DXT3: texel 0 alpha 0, texel 1 alpha 15
        var dxt3 = new TextureUnit { Address = 0x100, Format = 0x0E, Width = 4, Height = 4, Compressed = true, Normalized = true };
        Assert.Equal(0f, Textures.Texel(bus, dxt3, 0, 0).W);
        Assert.Equal(1f, Textures.Texel(bus, dxt3, 1, 0).W);
    }

    [Fact]
    public void Bilinear_filter_blends_the_four_nearest_texel_centres()
    {
        var bus = Ram(out _);
        // linear 2x1 A8R8G8B8: black, white
        bus.Write32(0, 0xFF000000); bus.Write32(4, 0xFFFFFFFF);
        var t = new TextureUnit { Address = 0, Format = 0x12, Width = 2, Height = 1, Pitch = 8, Normalized = true, MagFilter = 2, AddressU = 3, AddressV = 3 };
        Assert.Equal(0.5f, Textures.Sample(bus, t, 0.5f, 0.5f).X, 5);   // halfway between the centres
        Assert.Equal(0.25f, Textures.Sample(bus, t, 0.375f, 0.5f).X, 5);
        Assert.Equal(0f, Textures.Sample(bus, t, 0.1f, 0.5f).X, 5);     // clamped to the first texel
        Assert.Equal(1f, Textures.Sample(bus, t with { MagFilter = 1 }, 0.6f, 0.5f).X);
    }

    // ------------------------------------------------------------------ end to end

    private static uint Pack(double r, double g, double b, double a = 1)
    {
        static uint B(double v) => (uint)Math.Clamp(Math.Round(v * 255, MidpointRounding.ToEven), 0, 255);
        return (B(a) << 24) | (B(r) << 16) | (B(g) << 8) | B(b);
    }

    private static uint TexA(int x, int y) => 0xFF000000u | ((uint)x * 32 << 16) | ((uint)y * 32 << 8) | ((uint)(x ^ y) * 32);

    private static uint TexB(int x, int y) => Pack(x / 31.0, y * 16 / 63.0, (31 - x) / 31.0);

    /// <summary>Expected colour from the program's definitions, or null on a block border.</summary>
    private static uint? Expected(int px, int py)
    {
        static bool Inside(int p, int origin, int cell) => (p - origin) % cell > 0 && (p - origin) % cell < cell - 1;
        if (px >= 40 && px < 104 && py >= 40 && py < 104)
            return Inside(px, 40, 8) && Inside(py, 40, 8) ? TexA((px - 40) / 8, (py - 40) / 8) : null;
        if (px >= 160 && px < 224 && py >= 40 && py < 56)
            return (px - 160) % 4 is 1 or 2 && (py - 40) % 4 is 1 or 2 ? TexB((px - 160) / 4, (py - 40) / 4) : null;
        if (px >= 280 && px < 344 && py >= 40 && py < 104)
        {
            if (!Inside(px, 280, 8) || !Inside(py, 40, 8)) return null;
            var block = ((py - 40) / 32 * 2) + ((px - 280) / 32);
            return block switch { 0 => 0xFFFF0000, 1 => 0xFF0000FF, 2 => Pack(2 / 3.0, 0, 1 / 3.0), _ => 0x00000000 };
        }
        if (px >= 400 && px < 528 && py >= 40 && py < 168)
            return Inside(px, 400, 8) && Inside(py, 40, 8) ? TexA((px - 400) / 8 % 8, (py - 40) / 8 % 8) : null;
        if (px >= 40 && px < 104 && py >= 200 && py < 264)
        {
            if (!Inside(px, 40, 8) || !Inside(py, 200, 8)) return null;
            var t = TexA((px - 40) / 8, (py - 200) / 8);
            return Pack(((t >> 16) & 0xFF) / 255.0 * 0.5, ((t >> 8) & 0xFF) / 255.0, (t & 0xFF) / 255.0 * 0.25);
        }
        bool near = (px >= 38 && px < 106 && py >= 38 && py < 106) || (px >= 158 && px < 226 && py >= 38 && py < 58) || (px >= 278 && px < 346 && py >= 38 && py < 106)
            || (px >= 398 && px < 530 && py >= 38 && py < 170) || (px >= 38 && px < 106 && py >= 198 && py < 266);
        return near ? null : 0xFF404040;
    }

    [Fact]
    public void Texture_program_renders_every_texel_with_its_expected_value()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "xpc-texture-test");
        var file = File.ReadAllBytes(Path.Combine(dir, "default.xbe"));
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "default.xbe.sha256")).Trim(), Convert.ToHexStringLower(SHA256.HashData(file)));
        var m = new OriginalXboxMachine();
        m.MountDefaults(dir, null);
        m.Load(XbeImage.Parse(file), file);
        var r = m.Run(400_000_000);
        output.WriteLine($"{r.Stop}: {r.Detail}\n{m.Kernel.DebugOutput}\n{string.Join("\n", m.UnmodelledAccesses.Distinct())}");
        Assert.Equal(MachineStop.TitleExited, r.Stop);

        // the title's own read-back of the rendered target (first frame)
        var probes = new (int X, int Y)[] { (44, 44), (84, 60), (100, 100), (162, 42), (222, 54), (290, 50), (330, 50), (290, 90), (330, 90), (404, 44), (476, 116), (84, 220) };
        var e = probes.Select(p => $"{Expected(p.X, p.Y)!.Value:x8}").ToArray();
        Assert.Contains($"XPC-TEX A {e[0]} {e[1]} {e[2]} B {e[3]} {e[4]} C {e[5]} {e[6]} {e[7]} {e[8]} D {e[9]} {e[10]} E {e[11]}\n", m.Kernel.DebugOutput, StringComparison.Ordinal);
        Assert.Contains("XPC-TEX end\n", m.Kernel.DebugOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(m.UnmodelledAccesses, u => u.Contains("texture", StringComparison.OrdinalIgnoreCase));

        // every block centre of the displayed frame
        var frame = m.Gpu.ScanOut();
        Assert.NotNull(frame);
        var checkedPixels = 0;
        for (var y = 0; y < 480; y++)
            for (var x = 0; x < 640; x++)
                if (Expected(x, y) is { } want)
                {
                    var got = frame.Bgra[(y * 640) + x];
                    Assert.True((want | 0xFF000000) == got, $"pixel ({x},{y}) = {got:X8}, expected {want | 0xFF000000:X8}"); // scan-out shows RGB only
                    checkedPixels++;
                }
        output.WriteLine($"{checkedPixels} pixels checked");
        Assert.True(checkedPixels > 250_000);
    }
}
