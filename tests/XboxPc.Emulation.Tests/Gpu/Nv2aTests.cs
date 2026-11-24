using System.Numerics;
using System.Security.Cryptography;
using XboxPc.Emulation.Core;
using XboxPc.Emulation.Gpu.Nv2a;
using XboxPc.Emulation.OriginalXbox;
using Xunit.Abstractions;

namespace XboxPc.Emulation.Tests.Gpu;

/// <summary>NV2A 3D path: vertex programs, register combiners, the DMA pusher and an end-to-end pbkit program.</summary>
public class Nv2aTests(ITestOutputHelper output)
{
    private sealed class Irq : IInterruptController
    {
        public int Raised;

        public void RaiseIrq(int line) => Raised++;
    }

    private sealed class Report : IHardwareReport
    {
        public List<string> Messages { get; } = [];

        public void Unmodelled(string message) => Messages.Add(message);
    }

    // ------------------------------------------------------------------ vertex programs

    /// <summary>
    /// Compiled by NVIDIA cgc 3.1 (profile vp20) from nxdk's samples/triangle vs.vs.cg (MIT licensed sample):
    /// position = mul(float4(I.position.xyz, 1), m_viewport); position.xyz /= position.w; oD0 = I.color.
    /// An oracle independent of this emulator's decoder.
    /// </summary>
    private static readonly uint[] CgcTriangleProgram =
    [
        0x00000000, 0x004c2055, 0x0836186c, 0x2f0007f8,
        0x00000000, 0x008c0000, 0x0836186c, 0x1f0007f8,
        0x00000000, 0x008c40aa, 0x0836186c, 0x1f0007f8,
        0x00000000, 0x006c601b, 0x0436106c, 0x3f0007f8,
        0x00000000, 0x0400001b, 0x08361300, 0x101807f8,
        0x00000000, 0x0040001b, 0x0400286c, 0x2070e800,
        0x00000000, 0x0020061b, 0x0836106c, 0x2070f818,
        0x00000000, 0x0020001b, 0x0436106c, 0x20701801,
    ];

    private static VertexProgram Load(uint[] words)
    {
        var vp = new VertexProgram();
        for (var i = 0; i < words.Length; i++) vp.Program[i / 4, i % 4] = words[i];
        return vp;
    }

    [Fact]
    public void Cgc_compiled_program_transforms_like_its_source()
    {
        var vp = Load(CgcTriangleProgram);
        // m_viewport as uploaded at hardware constants 96..99 (c0..c3), row vectors: mul(v, M) = v.x*row0 + ...
        var m = new Matrix4x4(320, 0, 0, 0, 0, -240, 0, 0, 0, 0, 65536, 0, 320, 240, 0, 2);
        vp.Constant[96] = new Vector4(m.M11, m.M12, m.M13, m.M14);
        vp.Constant[97] = new Vector4(m.M21, m.M22, m.M23, m.M24);
        vp.Constant[98] = new Vector4(m.M31, m.M32, m.M33, m.M34);
        vp.Constant[99] = new Vector4(m.M41, m.M42, m.M43, m.M44);
        var v = new Vector4[16];
        v[0] = new Vector4(0.5f, -0.25f, 0.75f, 7); // w of the input is ignored by the source (float4(xyz, 1))
        v[3] = new Vector4(0.1f, 0.2f, 0.3f, 0.4f);

        var o = vp.Execute(v, 0, false);

        var p = Vector4.Transform(new Vector4(0.5f, -0.25f, 0.75f, 1), m);
        var expected = new Vector4(p.X / p.W, p.Y / p.W, p.Z / p.W, p.W);
        Assert.Equal(expected.X, o[VertexProgram.OPos].X, 3);
        Assert.Equal(expected.Y, o[VertexProgram.OPos].Y, 3);
        Assert.Equal(expected.Z, o[VertexProgram.OPos].Z, 1);
        Assert.Equal(expected.W, o[VertexProgram.OPos].W, 5);
        Assert.Equal(v[3], o[VertexProgram.OD0]);
    }

    [Fact]
    public void Hand_assembled_pass_through_program_of_the_3d_test()
    {
        var vp = Load([0, 0x0020001B, 0x0836006C, 0x0000F800, 0, 0x0020061B, 0x0836006C, 0x0000F819]);
        var v = new Vector4[16];
        v[0] = new Vector4(1, 2, 3, 4);
        v[3] = new Vector4(0.5f, 0.25f, 0, 1);
        var o = vp.Execute(v, 0, false);
        Assert.Equal(v[0], o[VertexProgram.OPos]);
        Assert.Equal(v[3], o[VertexProgram.OD0]);
        Assert.Equal(new Vector4(0, 0, 0, 1), o[VertexProgram.OD1]); // unwritten colour default
    }

    // ------------------------------------------------------------------ combiners

    [Fact]
    public void Final_combiner_passes_diffuse()
    {
        var c = new Combiners { Control = 1, FinalCw0 = 0x4, FinalCw1 = 0x1400 };
        c.Prepare();
        var d = new Vector4(0.2f, 0.4f, 0.6f, 0.8f);
        Assert.Equal(d, c.Evaluate(new CombinerInputs { Diffuse = d }));
    }

    [Fact]
    public void General_stage_modulates_diffuse_by_constant_into_spare0()
    {
        // stage 0 RGB: A = v0 (4), B = c0 (1), C = D = zero; AB -> r0 (12). Final: D = r0; alpha G = v0.a
        var c = new Combiners { Control = 1, FinalCw0 = 0xC, FinalCw1 = 0x1400 };
        c.ColorIcw[0] = (4u << 24) | (1u << 16);
        c.ColorOcw[0] = 12u << 4;
        c.Factor0[0] = 0x80FF8000; // a=0x80 r=1 g=0.502 b=0
        c.Prepare();
        var r = c.Evaluate(new CombinerInputs { Diffuse = new Vector4(0.5f, 1, 1, 1) });
        Assert.Equal(0.5f, r.X, 4);
        Assert.Equal(128 / 255f, r.Y, 4);
        Assert.Equal(0f, r.Z, 4);
        Assert.Equal(1f, r.W, 4);
    }

    [Fact]
    public void Input_mappings_and_final_lerp()
    {
        // final: A = c0 (lerp factor 0.25), B = v0, C = v1, D = 0  -> 0.25*v0 + 0.75*v1
        var c = new Combiners { Control = 0, FinalCw0 = (1u << 24) | (4u << 16) | (5u << 8), FinalCw1 = 0x1400 };
        c.Factor0[0] = 0x00404040;
        c.Prepare();
        var r = c.Evaluate(new CombinerInputs { Diffuse = new Vector4(1, 0, 0, 1), Specular = new Vector4(0, 1, 0, 1) });
        Assert.Equal(64 / 255f, r.X, 4);
        Assert.Equal(1 - (64 / 255f), r.Y, 4);
        // inverse flag on A: 1-0.25 => 0.75*v0 + 0.25*v1
        c.FinalCw0 |= 1u << 29;
        c.Prepare();
        r = c.Evaluate(new CombinerInputs { Diffuse = new Vector4(1, 0, 0, 1), Specular = new Vector4(0, 1, 0, 1) });
        Assert.Equal(1 - (64 / 255f), r.X, 4);
    }

    // ------------------------------------------------------------------ DMA pusher

    private static (Nv2aDevice Gpu, PhysicalBus Bus, Report Report) PusherSetup()
    {
        var bus = new PhysicalBus();
        bus.MapRam("RAM", 0, new byte[0x10_0000]);
        var report = new Report();
        var gpu = new Nv2aDevice(bus, new Irq(), report, 733_333_333);
        // instance memory: DMA object at instance 0x200 (base 0, limit 1 MB), Kelvin object at 0x210, RAMHT at 0x10000
        gpu.RaminWrite32(0x2000, 0x0000_303D); gpu.RaminWrite32(0x2004, 0x000F_FFFF); gpu.RaminWrite32(0x2008, 3);
        gpu.RaminWrite32(0x2100, 0x97);
        gpu.RaminWrite32(0x10000 + (0x13 * 8), 0x13); gpu.RaminWrite32(0x10000 + (0x13 * 8) + 4, 0x8001_0210); // graphics engine, instance 0x210
        gpu.RaminWrite32(0x10000 + (0x14 * 8), 0x14); gpu.RaminWrite32(0x10000 + (0x14 * 8) + 4, 0x8000_0200); // DMA object handle
        gpu.Write(Pfifo.Ramht, 4, 0x100);
        gpu.Write(Pfifo.Mode, 4, 1);
        gpu.Write(Pfifo.Cache1Push1, 4, 0x100);
        gpu.Write(Pfifo.Cache1DmaInstance, 4, 0x200);
        gpu.Write(Pgraph.Fifo, 4, 1);
        gpu.Write(Pgraph.CtxControl, 4, 0x1_0000);
        gpu.Write(Pfifo.Cache1DmaPush, 4, 1);
        gpu.Write(Pfifo.Cache1Pull0, 4, 1);
        gpu.Write(Pfifo.Cache1Push0, 4, 1);
        return (gpu, bus, report);
    }

    private static uint Header(int subchannel, uint method, int count, bool nonIncreasing = false) =>
        (nonIncreasing ? 0x4000_0000u : 0) | ((uint)count << 18) | ((uint)subchannel << 13) | method;

    private static void Push(PhysicalBus bus, uint address, params uint[] words)
    {
        for (var i = 0; i < words.Length; i++) bus.Write32(address + (uint)(i * 4), words[i]);
    }

    [Fact]
    public void Pusher_follows_jumps_calls_and_method_headers_and_binds_objects_through_ramht()
    {
        var (gpu, bus, report) = PusherSetup();
        Push(bus, 0x0, 0x1000 | 1);                                       // jump to 0x1000
        Push(bus, 0x1000,
            Header(0, 0, 1), 0x13,                                         // SET_OBJECT Kelvin
            Header(0, 0x194, 1), 0x14,                                     // SET_CONTEXT_DMA_COLOR (handle -> instance)
            0x3000 | 2,                                                    // call 0x3000
            Header(0, 0x1D98, 2), 0x0010_0000, 0x0020_0000,                // increasing: CLEAR_RECT_H, CLEAR_RECT_V
            Header(0, 0x1D90, 2, nonIncreasing: true), 0x111, 0x222);      // non-increasing: last value wins
        Push(bus, 0x3000, Header(0, 0x1D8C, 1), 0xABCD, 0x0002_0000);      // subroutine, return
        gpu.Write(Nv2aDevice.User + 0x40, 4, 0x1000 + (11 * 4));

        Assert.Empty(report.Messages);
        Assert.Equal(0x97u, gpu.Pgraph.ObjectClass(0));
        var k = gpu.Pgraph.Kelvin;
        Assert.Equal(0x200u, k.State(0x194));
        Assert.Equal(0xABCDu, k.State(0x1D8C));
        Assert.Equal(0x0010_0000u, k.State(0x1D98));
        Assert.Equal(0x0020_0000u, k.State(0x1D9C));
        Assert.Equal(0x222u, k.State(0x1D90));
        Assert.Equal(0xABCDu, gpu.Read(Pgraph.ParameterA, 4, AccessKind.Read)); // mirrored for pbkit's software methods
        Assert.Equal(0x1000u + (11 * 4), gpu.Read(Nv2aDevice.User + 0x44, 4, AccessKind.Read)); // GET reached PUT
    }

    [Fact]
    public void Software_method_traps_stalls_the_fifo_and_resumes_after_acknowledge()
    {
        var (gpu, bus, _) = PusherSetup();
        gpu.Write(Pgraph.IntrEn, 4, 0xFFFF_FFFF);
        gpu.Write(Nv2aDevice.PmcIntrEn0, 4, 1);
        Push(bus, 0x0, Header(0, 0, 1), 0x13, Header(0, 0x100, 1), 0x7, Header(0, 0x1D90, 1), 0x55);
        gpu.Write(Nv2aDevice.User + 0x40, 4, 6 * 4);

        Assert.True(gpu.Pgraph.Stalled);
        Assert.Equal(Pgraph.IntrError, gpu.Read(Pgraph.Intr, 4, AccessKind.Read));
        Assert.Equal(0x100u, gpu.Read(Pgraph.TrappedAddr, 4, AccessKind.Read) & 0x1FFC);
        Assert.Equal(7u, gpu.Read(Pgraph.TrappedDataLow, 4, AccessKind.Read));
        Assert.Equal(Nv2aDevice.IntrPgraph, gpu.Read(Nv2aDevice.PmcIntr0, 4, AccessKind.Read));
        Assert.Equal(1u, gpu.Read(Pgraph.Status, 4, AccessKind.Read));
        Assert.Equal(0u, gpu.Pgraph.Kelvin.State(0x1D90)); // the method after the trap has not run yet

        gpu.Write(Pgraph.Intr, 4, Pgraph.IntrError); // CPU acknowledges
        Assert.False(gpu.Pgraph.Stalled);
        Assert.Equal(0x55u, gpu.Pgraph.Kelvin.State(0x1D90));
        Assert.Equal(6u * 4, gpu.Read(Nv2aDevice.User + 0x44, 4, AccessKind.Read));
    }

    [Fact]
    public void Foreign_channel_raises_context_switch_and_waits()
    {
        var (gpu, bus, _) = PusherSetup();
        gpu.Write(Pgraph.CtxControl, 4, 0); // no channel loaded in PGRAPH
        Push(bus, 0x0, Header(0, 0, 1), 0x13);
        gpu.Write(Nv2aDevice.User + 0x40, 4, 8);
        Assert.Equal(Pgraph.IntrContextSwitch, gpu.Read(Pgraph.Intr, 4, AccessKind.Read));
        Assert.Equal(4u, gpu.Read(Nv2aDevice.User + 0x44, 4, AccessKind.Read)); // header consumed, the data word waits

        gpu.Write(Pgraph.CtxControl, 4, 0x1_0000);            // driver loads channel 0's context
        gpu.Write(Pgraph.Intr, 4, Pgraph.IntrContextSwitch);  // and acknowledges
        Assert.Equal(8u, gpu.Read(Nv2aDevice.User + 0x44, 4, AccessKind.Read));
        Assert.Equal(0x97u, gpu.Pgraph.ObjectClass(0));
    }

    [Fact]
    public void Reserved_command_suspends_the_pusher_with_an_interrupt_and_a_report()
    {
        var (gpu, bus, report) = PusherSetup();
        Push(bus, 0x0, 0xE000_0000);
        gpu.Write(Nv2aDevice.User + 0x40, 4, 4);
        Assert.Equal(Pfifo.IntrDmaPusher, gpu.Pfifo.Interrupt);
        Assert.Contains(report.Messages, m => m.Contains("reserved command", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ end to end

    [Fact]
    public void Pbkit_program_renders_every_region_with_its_expected_colour()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "xpc-3d-test");
        var file = File.ReadAllBytes(Path.Combine(dir, "default.xbe"));
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "default.xbe.sha256")).Trim(), Convert.ToHexStringLower(SHA256.HashData(file)));
        var m = new OriginalXboxMachine();
        m.MountDefaults(dir, null);
        m.Load(XbeImage.Parse(file), file);
        var r = m.Run(400_000_000);
        output.WriteLine($"{r.Stop}: {r.Detail}\n{m.Kernel.DebugOutput}\n{string.Join("\n", m.UnmodelledAccesses.Distinct())}");
        Assert.Equal(MachineStop.TitleExited, r.Stop);

        // CPU read-back of the rendered back buffer (first frame), printed by the title
        Assert.Contains("XPC-3D background ff203040 red ffff0000 green ff00ff00 yellow ffffff00 overlap ff00ff00 white ffffffff\n", m.Kernel.DebugOutput, StringComparison.Ordinal);
        var tri = new[] { (320, 45), (250, 195), (390, 195), (320, 147) }.Select(p => $"{Gouraud(p.Item1, p.Item2):x8}");
        Assert.Contains($"XPC-3D triangle top {string.Join(" ", tri.Take(1))} left {tri.ElementAt(1)} right {tri.ElementAt(2)} centre {tri.ElementAt(3)} outside ff203040\n", m.Kernel.DebugOutput, StringComparison.Ordinal);
        Assert.Contains("XPC-3D end frames=3\n", m.Kernel.DebugOutput, StringComparison.Ordinal);

        // what the display shows after pbkit's flips: the last rendered frame
        var frame = m.Gpu.ScanOut();
        Assert.NotNull(frame);
        uint At(int x, int y) => frame.Bgra[(y * 640) + x] | 0xFF00_0000;
        for (var y = 0; y < 480; y += 9)
            for (var x = 0; x < 640; x += 7)
            {
                var expected = Expected(x, y);
                if (expected is { } e) Assert.True(e == At(x, y), $"pixel ({x},{y}) = {At(x, y):X8}, expected {e:X8}");
            }
        Assert.Equal(21, m.Gpu.Pgraph.Kelvin.TrianglesDrawn); // 7 triangles x 3 frames
    }

    /// <summary>Independent reference for the Gouraud triangle: barycentric weights at the pixel centre.</summary>
    private static uint Gouraud(int x, int y)
    {
        (double X, double Y)[] p = [(320, 40), (400, 200), (240, 200)];
        double Area((double X, double Y) a, (double X, double Y) b, double px, double py) => ((b.X - a.X) * (py - a.Y)) - ((b.Y - a.Y) * (px - a.X));
        var total = Area(p[0], p[1], p[2].X, p[2].Y);
        var l0 = Area(p[1], p[2], x, y) / total; // weight of vertex 0 (red)
        var l1 = Area(p[2], p[0], x, y) / total; // vertex 1 (blue)
        var l2 = 1 - l0 - l1;                    // vertex 2 (green)
        static uint B(double v) => (uint)Math.Clamp(Math.Round(v * 255, MidpointRounding.ToEven), 0, 255);
        return 0xFF00_0000u | (B(l0) << 16) | (B(l2) << 8) | B(l1);
    }

    /// <summary>Expected colour away from region borders (null = on an edge, not checked).</summary>
    private static uint? Expected(int x, int y)
    {
        static bool In(int x, int y, int x0, int y0, int x1, int y1, int margin = 1) => x >= x0 + margin && x < x1 - margin && y >= y0 + margin && y < y1 - margin;
        static bool Near(int x, int y, int x0, int y0, int x1, int y1) => x >= x0 - 2 && x < x1 + 2 && y >= y0 - 2 && y < y1 + 2;
        if (In(x, y, 500, 300, 600, 400)) return 0xFFFFFFFF;
        if (Near(x, y, 500, 300, 600, 400)) return null;
        // triangle (320,40) (400,200) (240,200): inside test with a margin
        var inside = y > 42 && y < 198 && x > 320 - ((y - 40) / 2.0) + 2 && x < 320 + ((y - 40) / 2.0) - 2;
        if (inside) return null; // gradient checked exactly via the title's read-back above
        if (y >= 38 && y <= 202 && x >= 238 && x <= 402) return null;
        if (In(x, y, 240, 260, 400, 380)) return 0xFF00FF00;   // near quad wins the depth test where they overlap
        if (Near(x, y, 240, 260, 400, 380)) return null;
        if (In(x, y, 320, 300, 480, 420)) return 0xFFFFFF00;
        if (Near(x, y, 320, 300, 480, 420)) return null;
        if (In(x, y, 40, 40, 200, 160)) return 0xFFFF0000;
        if (Near(x, y, 40, 40, 200, 160)) return null;
        return 0xFF203040;
    }
}
