using System.Numerics;
using System.Security.Cryptography;
using XboxPc.Emulation.Gpu.Nv2a;
using XboxPc.Emulation.OriginalXbox;
using Xunit.Abstractions;

namespace XboxPc.Emulation.Tests.Gpu;

/// <summary>Kelvin fixed-function transform and lighting: unit level and the xpc-ff-test program end to end.</summary>
public class FixedFunctionTests(ITestOutputHelper output)
{
    private sealed class State
    {
        private readonly Dictionary<uint, uint> _m = [];

        public uint Get(uint method) => _m.GetValueOrDefault(method);

        public State Set(uint method, params float[] values)
        {
            for (var i = 0; i < values.Length; i++) _m[method + (uint)(i * 4)] = BitConverter.SingleToUInt32Bits(values[i]);
            return this;
        }

        public State SetU(uint method, uint value)
        {
            _m[method] = value;
            return this;
        }
    }

    private static readonly float[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    private static Vector4[] Inputs(Vector4 position, Vector3 normal = default, Vector4 diffuse = default)
    {
        var v = Enumerable.Repeat(new Vector4(0, 0, 0, 1), 16).ToArray();
        v[0] = position;
        v[2] = new Vector4(normal, 0);
        v[3] = diffuse;
        return v;
    }

    [Fact]
    public void Composite_transform_divides_xyz_by_w_and_keeps_w()
    {
        var s = new State().Set(0x680, 2, 0, 0, 100, 0, 2, 0, 50, 0, 0, 1, 0, 0, 0, 0.5f, 0);
        var o = FixedFunction.Execute(Inputs(new Vector4(10, 20, 4, 1)), s.Get, _ => { });
        Assert.Equal(new Vector4((20 + 100) / 2f, (40 + 50) / 2f, 4 / 2f, 2), o[VertexProgram.OPos]);
    }

    [Fact]
    public void Unlit_vertices_pass_colours_and_texture_coordinates_with_optional_texture_matrix()
    {
        var s = new State().Set(0x680, Identity);
        var v = Inputs(new Vector4(0, 0, 0, 1), diffuse: new Vector4(0.1f, 0.2f, 0.3f, 0.4f));
        v[4] = new Vector4(0.5f, 0.6f, 0.7f, 0.8f);
        v[9] = new Vector4(0.25f, 0.5f, 0, 1);
        var o = FixedFunction.Execute(v, s.Get, _ => { });
        Assert.Equal(v[3], o[VertexProgram.OD0]);
        Assert.Equal(new Vector4(0, 0, 0, 1), o[VertexProgram.OD1]);                 // specular disabled
        Assert.Equal(v[9], o[VertexProgram.OT0]);
        s.SetU(0x3B8, 1);                                                            // specular enabled
        Assert.Equal(new Vector4(0.5f, 0.6f, 0.7f, 1), FixedFunction.Execute(v, s.Get, _ => { })[VertexProgram.OD1]); // alpha forced to 1 (LIGHT_CONTROL bit 17 clear)
        s.SetU(0x294, 0x2_0000);
        Assert.Equal(v[4], FixedFunction.Execute(v, s.Get, _ => { })[VertexProgram.OD1]);
        s.SetU(0x3B8, 0).SetU(0x294, 0);

        s.SetU(0x420, 1).Set(0x6C0, 2, 0, 0, 0, 0, 2, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1); // scale s, t by 2
        o = FixedFunction.Execute(v, s.Get, _ => { });
        Assert.Equal(new Vector4(0.5f, 1, 0, 1), o[VertexProgram.OT0]);
    }

    private static State Lit() => new State()
        .Set(0x680, Identity).Set(0x480, Identity).Set(0x580, Identity)
        .SetU(0x314, 1).SetU(0x3A4, 1).Set(0xA10, 0.1f, 0.1f, 0.1f).Set(0x3B4, 1f);

    [Fact]
    public void Infinite_light_diffuse_follows_n_dot_l()
    {
        var s = Lit().SetU(0x3BC, 1).Set(0x100C, 0.8f, 0.4f, 0.2f).Set(0x1034, 0, 0, 1);
        var o = FixedFunction.Execute(Inputs(new Vector4(0, 0, 0, 1), new Vector3(0, 0.8f, 0.6f)), s.Get, _ => { });
        Assert.Equal(0.1f + (0.8f * 0.6f), o[VertexProgram.OD0].X, 5);
        Assert.Equal(0.1f + (0.4f * 0.6f), o[VertexProgram.OD0].Y, 5);
        Assert.Equal(1f, o[VertexProgram.OD0].W);
        // facing away: no diffuse
        o = FixedFunction.Execute(Inputs(new Vector4(0, 0, 0, 1), new Vector3(0, 0, -1)), s.Get, _ => { });
        Assert.Equal(0.1f, o[VertexProgram.OD0].X, 5);
    }

    [Fact]
    public void Local_light_attenuates_with_distance_and_range()
    {
        var s = Lit().Set(0xA10, 0, 0, 0).SetU(0x3BC, 2).Set(0x100C, 1, 1, 1).Set(0x105C, 0, 0, 4).Set(0x1068, 1, 0, 0.25f).Set(0x1024, 10f);
        var o = FixedFunction.Execute(Inputs(new Vector4(0, 0, 0, 1), new Vector3(0, 0, 1)), s.Get, _ => { });
        Assert.Equal(1 / (1 + (0.25f * 16)), o[VertexProgram.OD0].X, 5); // d = 4, N.L = 1
        s.Set(0x1024, 3f);                                                 // range shorter than the distance
        o = FixedFunction.Execute(Inputs(new Vector4(0, 0, 0, 1), new Vector3(0, 0, 1)), s.Get, _ => { });
        Assert.Equal(0f, o[VertexProgram.OD0].X, 5);
    }

    [Fact]
    public void Color_material_takes_diffuse_from_the_vertex_and_reports_unimplemented_features()
    {
        var reports = new List<string>();
        var s = Lit().SetU(0x3BC, 1).Set(0x100C, 0.8f, 0.4f, 0.2f).Set(0x1034, 0, 0, 1).SetU(0x298, 1u << 4).SetU(0x3B8, 1).SetU(0x3C0, 0x2400);
        var o = FixedFunction.Execute(Inputs(new Vector4(0, 0, 0, 1), new Vector3(0, 0, 1), new Vector4(1, 0, 0.5f, 0.75f)), s.Get, reports.Add);
        Assert.True(Vector4.Distance(new Vector4(0.9f, 0.1f, 0.2f, 0.75f), o[VertexProgram.OD0]) < 1e-5f, o[VertexProgram.OD0].ToString());
        Assert.Empty(reports); // specular (black light) and eye-linear texgen are implemented now
    }

    [Fact]
    public void Specular_curve_and_half_vectors()
    {
        var lmn = new Vector3(FixedFunction.DefaultSpecularParams[0], FixedFunction.DefaultSpecularParams[1], FixedFunction.DefaultSpecularParams[2]);
        Assert.Equal(1f, FixedFunction.Curve(1, lmn, 10000), 5);
        Assert.Equal(0.8f / (1.494592f - (0.494592f * 0.8f)), FixedFunction.Curve(0.8f, lmn, 10000), 5);
        Assert.Equal(0f, FixedFunction.Curve(-0.5f, lmn, 10000));
        Assert.Equal(10000f, FixedFunction.Curve(0.5f, new Vector3(0, 0, 0), 10000)); // zero denominator

        // infinite light, infinite viewer: the uploaded half vector is used; separate specular keeps oD1
        var s = Lit().Set(0xA10, 0, 0, 0).SetU(0x3BC, 1).SetU(0x3B8, 1).SetU(0x294, 1)
            .Set(0x1018, 1, 1, 1).Set(0x1034, 0, 0, 1).Set(0x1028, 0, 0.6f, 0.8f)
            .Set(0x9E0, lmn.X, lmn.Y, lmn.Z);
        var o = FixedFunction.Execute(Inputs(new Vector4(0, 0, 0, 1), new Vector3(0, 0, 1)), s.Get, _ => { });
        Assert.Equal(FixedFunction.Curve(0.8f, lmn, 10000), o[VertexProgram.OD1].X, 5);
        Assert.Equal(0f, o[VertexProgram.OD0].X, 5);
        // local eye: half vector = normalize(L + V); eye straight above a vertex at the origin, light along +z -> H = (0,0,1)
        s.SetU(0x294, 1 | 0x1_0000).Set(0xA50, 0, 0, 10, 1).Set(0x1028, 0, 0, 0);
        o = FixedFunction.Execute(Inputs(new Vector4(0, 0, 0, 1), new Vector3(0, 0, 1)), s.Get, _ => { });
        Assert.Equal(1f, o[VertexProgram.OD1].X, 5);
        // light behind the surface: no specular
        s.Set(0x1034, 0, 0, -1);
        o = FixedFunction.Execute(Inputs(new Vector4(0, 0, 0, 1), new Vector3(0, 0, 1)), s.Get, _ => { });
        Assert.Equal(0f, o[VertexProgram.OD1].X, 5);
    }

    [Fact]
    public void Spot_light_falloff()
    {
        Assert.Equal(0f, FixedFunction.SpotFactor(-0.2f, new Vector3(0, 0, 1)));
        Assert.Equal(1f, FixedFunction.SpotFactor(1.5f, new Vector3(0, 0, 1)));
        Assert.Equal(0.5f, FixedFunction.SpotFactor(0.5f, new Vector3(0, 0, 1)), 5);         // (s + L)/(M s + N) with L=0 M=0 N=1
        Assert.Equal(0.25f, FixedFunction.SpotFactor(0.5f, new Vector3(0, 0, 2)), 5);
        // spot light at (0,0,4) aimed at a vertex on its axis: s = dot(dir, VP) + w = 1 - 0.5 = 0.5 -> factor 0.5
        var s = Lit().Set(0xA10, 0, 0, 0).SetU(0x3BC, 3).Set(0x100C, 1, 1, 1).Set(0x105C, 0, 0, 4).Set(0x1068, 1, 0, 0).Set(0x1024, 100f)
            .Set(0x1040, 0, 0, 1).Set(0x104C, 0, 0, 1, -0.5f);
        var o = FixedFunction.Execute(Inputs(new Vector4(0, 0, 0, 1), new Vector3(0, 0, 1)), s.Get, _ => { });
        Assert.Equal(0.5f, o[VertexProgram.OD0].X, 5);
    }

    [Fact]
    public void Texture_coordinate_generation_modes()
    {
        var s = Lit().SetU(0x314, 0);
        var v = Inputs(new Vector4(2, 3, 4, 1), new Vector3(0, 0, 1));
        s.SetU(0x3C0, 0x2401).Set(0x840, 1, 0, 0, 0.5f);           // S = x + 0.5 (object linear)
        s.SetU(0x3C4, 0x2400).Set(0x850, 0, 2, 0, 0);              // T = 2y (eye linear, MV = identity)
        s.SetU(0x3C8, 0x8511);                                     // R = normal.z
        var o = FixedFunction.Execute(v, s.Get, _ => { });
        Assert.Equal(new Vector4(2.5f, 6, 1, 1), o[VertexProgram.OT0]);

        // reflection map, infinite viewer: u = (0,0,1), n = (0,0,1) -> r = (0,0,-1)
        s = Lit().SetU(0x314, 0).SetU(0x9CC, 1).SetU(0x3C0, 0x8512).SetU(0x3C4, 0x8512).SetU(0x3C8, 0x8512);
        o = FixedFunction.Execute(v, s.Get, _ => { });
        Assert.Equal(new Vector3(0, 0, -1), new Vector3(o[VertexProgram.OT0].X, o[VertexProgram.OT0].Y, o[VertexProgram.OT0].Z));

        // sphere map with a tilted normal
        var n = Vector3.Normalize(new Vector3(0.6f, 0, 0.8f));
        s = Lit().SetU(0x314, 0).SetU(0x9CC, 1).SetU(0x3C0, 0x2402).SetU(0x3C4, 0x2402);
        o = FixedFunction.Execute(Inputs(new Vector4(0, 0, 0, 1), n), s.Get, _ => { });
        var u = new Vector3(0, 0, 1);
        var r = u - (2 * Vector3.Dot(n, u) * n);
        var m = 1 / (2 * (r + u).Length());
        Assert.Equal((r.X * m) + 0.5f, o[VertexProgram.OT0].X, 5);
        Assert.Equal((r.Y * m) + 0.5f, o[VertexProgram.OT0].Y, 5);
    }

    [Theory]
    [InlineData(1u, new[] { 0.25f }, 0.75f * 10)]                                         // 2 matrices blended: w1 = 1 - w0
    [InlineData(2u, new[] { 0.25f, 0.5f }, 0.5f * 10 / 0.75f)]                            // 2 explicit: weights sum to 0.75, so w = 0.75
    [InlineData(3u, new[] { 0.2f, 0.3f }, (0.3f * 10) + (0.5f * 20))]                     // 3 blended
    [InlineData(4u, new[] { 0.2f, 0.3f, 0.1f }, ((0.3f * 10) + (0.1f * 20)) / 0.6f)]       // 3 explicit: w = 0.6
    [InlineData(5u, new[] { 0.1f, 0.2f, 0.3f }, (0.2f * 10) + (0.3f * 20) + (0.4f * 30))] // 4 blended
    [InlineData(6u, new[] { 0.1f, 0.2f, 0.3f, 0.4f }, (0.2f * 10) + (0.3f * 20) + (0.4f * 30))] // 4 explicit
    public void Skinning_blends_model_view_matrices(uint mode, float[] weights, float expectedX)
    {
        // matrix i translates x by 10*i; composite = identity, so the window x is the blended eye x
        var s = new State().Set(0x680, Identity).SetU(0x328, mode);
        for (var i = 0; i < 4; i++)
        {
            s.Set(0x480 + (uint)(i * 0x40), 1, 0, 0, 10 * i, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            s.Set(0x580 + (uint)(i * 0x40), Identity);
        }
        var v = Inputs(new Vector4(0, 0, 0, 1));
        var w = new Vector4(0);
        for (var i = 0; i < weights.Length; i++) w[i] = weights[i];
        v[1] = w;
        var o = FixedFunction.Execute(v, s.Get, _ => { });
        Assert.Equal(MathF.Truncate(expectedX * 16) / 16, o[VertexProgram.OPos].X, 4);
    }

    [Theory]
    [InlineData(0x2601u, 0.5f, 1f, 1f, 0.5f)]       // linear: p0 + d*p1 - 1
    [InlineData(0x800u, -0.125f, 1.5f, 0.5f, 0.5f)]  // exp: 1.5 + 2^(16 * -0.125 * 0.5) - 1.5 = 0.5
    [InlineData(0x804u, 0.25f, 0f, 1f, 0.75f)]      // linear abs: |0 + 0.25 - 1|
    public void Fog_factor_modes(uint mode, float d, float p0, float p1, float expected)
        => Assert.Equal(expected, FixedFunction.FogFactor(d, mode, p0, p1), 5);

    [Fact]
    public void Viewport_offset_is_added_after_the_divide_and_snapped_to_sixteenths()
    {
        var s = new State().Set(0x680, Identity).Set(0xA20, 0.53125f, 1.03125f, 0, 0);
        var o = FixedFunction.Execute(Inputs(new Vector4(10.01f, 20, 0, 1)), s.Get, _ => { });
        Assert.Equal(MathF.Truncate((10.01f + 0.53125f) * 16) / 16, o[VertexProgram.OPos].X);
        Assert.Equal(21f, o[VertexProgram.OPos].Y); // 1/32 is below the 1/16 snap
    }

    // ------------------------------------------------------------------ end to end

    private static uint Pack(double r, double g, double b)
    {
        static uint B(double v) => (uint)Math.Clamp(Math.Round(v * 255, MidpointRounding.ToEven), 0, 255);
        return 0xFF00_0000u | (B(r) << 16) | (B(g) << 8) | B(b);
    }

    /// <summary>Expected colour from the program's lighting set-up (lighting equation evaluated here independently).</summary>
    private static uint? Expected(int x, int y)
    {
        static bool In(int x, int y, int x0, int y0, int x1, int y1) => x > x0 + 1 && x < x1 - 2 && y > y0 + 1 && y < y1 - 2;
        static bool Near(int x, int y, int x0, int y0, int x1, int y1) => x >= x0 - 2 && x <= x1 + 2 && y >= y0 - 2 && y <= y1 + 2;
        if (In(x, y, 40, 40, 120, 120)) return Pack(0, 0.5, 1);
        if (In(x, y, 160, 40, 240, 120)) return Pack(0.1 + 0.8, 0.1 + 0.4, 0.1 + 0.2);
        if (In(x, y, 280, 40, 360, 120)) return Pack(0.1 + (0.8 * 0.6), 0.1 + (0.4 * 0.6), 0.1 + (0.2 * 0.6));
        if (In(x, y, 400, 40, 480, 120)) return Pack(0.1 + 0.8, 0.1, 0.1 + 0.1);
        if (In(x, y, 40, 200, 120, 280)) return 0xFFFFFFFF;
        if (x > 202 && x < 277 && y > 202 && y < 277 && y > x + 2) return 0xFFFFFF00;   // immediate-mode triangle interior
        if (x > 198 && x < 282 && y > 198 && y < 282 && Math.Abs(y - x) <= 2) return null;
        if (x > 198 && x < 282 && y > 198 && y < 282 && y > x) return null;
        if (Near(x, y, 40, 40, 120, 120) || Near(x, y, 160, 40, 240, 120) || Near(x, y, 280, 40, 360, 120) || Near(x, y, 400, 40, 480, 120) || Near(x, y, 40, 200, 120, 280)) return null;
        return 0xFF404040;
    }

    private static bool Close(uint a, uint b) =>
        Enumerable.Range(0, 4).All(i => Math.Abs((int)((a >> (8 * i)) & 0xFF) - (int)((b >> (8 * i)) & 0xFF)) <= 1);

    [Fact]
    public void Fixed_function_program_matches_the_lighting_equation()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "xpc-ff-test");
        var file = File.ReadAllBytes(Path.Combine(dir, "default.xbe"));
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "default.xbe.sha256")).Trim(), Convert.ToHexStringLower(SHA256.HashData(file)));
        var m = new OriginalXboxMachine();
        m.MountDefaults(dir, null);
        m.Load(XbeImage.Parse(file), file);
        var r = m.Run(400_000_000);
        output.WriteLine($"{r.Stop}: {r.Detail}\n{m.Kernel.DebugOutput}\n{string.Join("\n", m.UnmodelledAccesses.Distinct())}");
        Assert.Equal(MachineStop.TitleExited, r.Stop);
        Assert.DoesNotContain(m.UnmodelledAccesses, u => u.Contains("fixed-function", StringComparison.Ordinal) || u.Contains("execution mode", StringComparison.Ordinal));

        var line = m.Kernel.DebugOutput.Split('\n').Single(l => l.StartsWith("XPC-FF A ", StringComparison.Ordinal));
        var values = line.Split(' ').Where(t => t.Length == 8).Select(t => Convert.ToUInt32(t, 16)).ToArray();
        var probes = new (int X, int Y)[] { (80, 80), (200, 80), (320, 80), (440, 80), (80, 240), (125, 240), (220, 270), (270, 210) };
        Assert.Equal(probes.Length, values.Length);
        for (var i = 0; i < probes.Length; i++)
            Assert.True(Close(values[i], Expected(probes[i].X, probes[i].Y)!.Value), $"probe {probes[i]}: {values[i]:X8} vs {Expected(probes[i].X, probes[i].Y):X8}");

        var frame = m.Gpu.ScanOut();
        Assert.NotNull(frame);
        for (var y = 0; y < 480; y += 3)
            for (var x = 0; x < 640; x += 3)
                if (Expected(x, y) is { } want)
                    Assert.True(Close(want, frame.Bgra[(y * 640) + x]), $"pixel ({x},{y}) = {frame.Bgra[(y * 640) + x]:X8}, expected {want:X8}");
    }

    /// <summary>Expected colours of xpc-ff2-test, from its definitions (constant per region).</summary>
    private static uint? Expected2(int x, int y)
    {
        static bool In(int x, int y, int x0, int y0, int x1, int y1) => x > x0 + 1 && x < x1 - 2 && y > y0 + 1 && y < y1 - 2;
        static bool Near(int x, int y, int x0, int y0, int x1, int y1) => x >= x0 - 2 && x <= x1 + 2 && y >= y0 - 2 && y <= y1 + 2;
        var pf = 0.8 / (1.494592 - (0.494592 * 0.8));
        if (In(x, y, 40, 40, 104, 104)) return Pack(0.5, 0.25, 0);
        if (In(x, y, 120, 40, 184, 104)) return Pack(0.5 * pf, 0.25 * pf, 0);
        if (In(x, y, 200, 40, 264, 104)) return Pack(1, 0.25, 0.25);
        if (x >= 280 && x < 344 && y >= 40 && y < 104)
        {
            if ((x - 280) % 8 is 0 or 7 || (y - 40) % 8 is 0 or 7) return null;
            int tx = (x - 280) / 8, ty = (y - 40) / 8;
            return 0xFF000000u | ((uint)tx * 32 << 16) | ((uint)ty * 32 << 8) | ((uint)(tx ^ ty) * 32);
        }
        if (In(x, y, 90, 200, 154, 264)) return 0xFF00FF00;
        if (Near(x, y, 40, 40, 104, 104) || Near(x, y, 120, 40, 184, 104) || Near(x, y, 200, 40, 264, 104) || Near(x, y, 280, 40, 344, 104) || Near(x, y, 90, 200, 154, 264)) return null;
        return 0xFF404040;
    }

    [Fact]
    public void Fixed_function_program_two_specular_fog_texgen_skinning_viewport()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "xpc-ff2-test");
        var file = File.ReadAllBytes(Path.Combine(dir, "default.xbe"));
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "default.xbe.sha256")).Trim(), Convert.ToHexStringLower(SHA256.HashData(file)));
        var m = new OriginalXboxMachine();
        m.MountDefaults(dir, null);
        m.Load(XbeImage.Parse(file), file);
        var r = m.Run(400_000_000);
        output.WriteLine($"{r.Stop}: {r.Detail}\n{m.Kernel.DebugOutput}\n{string.Join("\n", m.UnmodelledAccesses.Distinct())}");
        Assert.Equal(MachineStop.TitleExited, r.Stop);
        Assert.DoesNotContain(m.UnmodelledAccesses, u => u.Contains("NV2A", StringComparison.Ordinal) && !u.Contains("class 0x", StringComparison.Ordinal));

        var line = m.Kernel.DebugOutput.Split('\n').Single(l => l.StartsWith("XPC-FF2 S1 ", StringComparison.Ordinal));
        var values = line.Split(' ').Where(t => t.Length == 8).Select(t => Convert.ToUInt32(t, 16)).ToArray();
        var probes = new (int X, int Y)[] { (72, 72), (152, 72), (232, 72), (284, 44), (324, 60), (140, 230), (70, 230) };
        Assert.Equal(probes.Length, values.Length);
        for (var i = 0; i < probes.Length; i++)
            Assert.True(Close(values[i], Expected2(probes[i].X, probes[i].Y)!.Value), $"probe {probes[i]}: {values[i]:X8} vs {Expected2(probes[i].X, probes[i].Y):X8}");

        var frame = m.Gpu.ScanOut();
        Assert.NotNull(frame);
        for (var y = 0; y < 480; y += 3)
            for (var x = 0; x < 640; x += 3)
                if (Expected2(x, y) is { } want)
                    Assert.True(Close(want, frame.Bgra[(y * 640) + x]), $"pixel ({x},{y}) = {frame.Bgra[(y * 640) + x]:X8}, expected {want:X8}");
    }
}
