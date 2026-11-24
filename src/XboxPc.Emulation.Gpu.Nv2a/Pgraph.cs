using System.Numerics;

namespace XboxPc.Emulation.Gpu.Nv2a;

/// <summary>
/// PGRAPH, the graphics engine. Modelled: the register interface pbkit/drivers use (interrupt status/enable with
/// write-1-to-clear, NSOURCE, trapped address/data, status, FIFO access, context control/user, parameter A/B),
/// channel ownership with CONTEXT_SWITCH interrupts, object binding per subchannel (class read from instance memory),
/// and the Kelvin (class 0x97) 3D engine with a CPU reference renderer (<see cref="KelvinRenderer"/>):
/// surfaces, clears, transform programs, constants, vertex arrays / inline arrays / immediate vertices, primitive
/// assembly, register combiners, depth test. NO_OPERATION with a non-zero parameter traps to the CPU (the
/// "software method" mechanism pbkit uses) and the engine stalls until the interrupt is acknowledged.
/// Not modelled: context save/restore to instance memory (single-context use), 2D classes beyond binding.
/// </summary>
public sealed class Pgraph
{
    public const uint Intr = 0x400100, Nsource = 0x400108, IntrEn = 0x400140, CtxControl = 0x400144, CtxUser = 0x400148;
    public const uint CtxSwitch1 = 0x40014C, Status = 0x400700, TrappedAddr = 0x400704, TrappedDataLow = 0x400708;
    public const uint TrappedDataHigh = 0x40070C, Increment = 0x40071C, Fifo = 0x400720, ParameterB = 0x40186C, ParameterA = 0x401A88;

    public const uint IntrNotify = 0x1, IntrContextSwitch = 0x1000, IntrError = 0x10_0000;
    private const uint CtxControlChidValid = 0x1_0000;

    private readonly Nv2aDevice _gpu;
    private readonly Dictionary<uint, uint> _regs = [];
    private readonly uint[] _objectInstance = new uint[8];
    private readonly uint[] _objectClass = new uint[8];
    private uint _waitBits; // interrupt bits that must be acknowledged before the engine continues

    public Pgraph(Nv2aDevice gpu)
    {
        _gpu = gpu;
        Kelvin = new KelvinRenderer(gpu);
        TwoD = new TwoDEngine(gpu);
    }

    public TwoDEngine TwoD { get; }

    public uint Interrupt { get; private set; }
    public uint InterruptEnable { get; private set; }
    public KelvinRenderer Kelvin { get; }

    /// <summary>True while waiting for the CPU to acknowledge an interrupt (reported as busy in PGRAPH_STATUS).</summary>
    public bool Stalled => _waitBits != 0;

    public uint ObjectClass(int subchannel) => _objectClass[subchannel];

    private uint Reg(uint r) => _regs.GetValueOrDefault(r);

    internal uint ReadRegister(uint offset) => offset switch
    {
        Intr => Interrupt,
        IntrEn => InterruptEnable,
        Status => Stalled ? 1u : 0,
        _ => Reg(offset),
    };

    internal void WriteRegister(uint offset, uint value)
    {
        switch (offset)
        {
            case Intr:
                Interrupt &= ~value;
                if ((Interrupt & _waitBits) == 0) _waitBits = 0;
                break;
            case IntrEn: InterruptEnable = value; break;
            case Status: break; // read-only
            case Increment:
                _regs[offset] = 0;
                if ((value & 2) != 0) Kelvin.FlipReadIncrement();
                break;
            default: _regs[offset] = value; break;
        }
        if (offset is Intr or Fifo or CtxControl or CtxUser) _gpu.Pfifo.Run();
    }

    private bool Accepting(int channel, int subchannel, uint method)
    {
        if ((Reg(Fifo) & 1) == 0 || Stalled) return false;
        var owner = (int)((Reg(CtxUser) >> 24) & 0x1F);
        if ((Reg(CtxControl) & CtxControlChidValid) != 0 && owner == channel) return true;
        // another (or no) channel's context is loaded: ask the CPU to switch contexts, then retry this method
        Trap(channel, subchannel, method, 0, IntrContextSwitch, nsource: 0);
        return false;
    }

    private void Trap(int channel, int subchannel, uint method, uint data, uint bit, uint nsource)
    {
        _regs[TrappedAddr] = ((uint)channel << 20) | ((uint)subchannel << 16) | (method & 0x1FFC);
        _regs[TrappedDataLow] = data;
        _regs[Nsource] = nsource;
        Interrupt |= bit;
        _waitBits |= bit;
        _gpu.UpdateIrq();
    }

    /// <summary>Binds the object at <paramref name="instance"/> (16-byte units) to a subchannel. False = stalled.</summary>
    public bool SetObject(int channel, int subchannel, uint instance)
    {
        if (!Accepting(channel, subchannel, 0)) return false;
        _objectInstance[subchannel] = instance;
        _objectClass[subchannel] = _gpu.RaminRead32(instance << 4) & 0xFF;
        _regs[CtxSwitch1] = _objectClass[subchannel];
        return true;
    }

    /// <summary>Executes one method. Returns false when the engine cannot accept it now (it will be retried).</summary>
    public bool Method(int channel, int subchannel, uint method, uint parameter)
    {
        if (!Accepting(channel, subchannel, method)) return false;
        _regs[CtxSwitch1] = _objectClass[subchannel];
        if (method == 0x100) // NO_OPERATION: a non-zero parameter is a software method call (trap to the CPU)
        {
            if (parameter != 0) Trap(channel, subchannel, method, parameter, IntrError, nsource: 1);
            return true;
        }
        switch (_objectClass[subchannel])
        {
            case 0x97:
                if (method == 0x1D8C) _regs[ParameterA] = parameter;
                if (method == 0x1D90) _regs[ParameterB] = parameter;
                Kelvin.Method(method, parameter);
                return true;
            case var cls when TwoDEngine.Handles(cls):
                TwoD.Method(cls, _objectInstance[subchannel], method, parameter);
                return true;
            default:
                if (method >= 0x180) _gpu.Report($"NV2A class 0x{_objectClass[subchannel]:X2} method 0x{method:X4} = 0x{parameter:X} ignored (class not modelled)");
                return true;
        }
    }
}

/// <summary>Kelvin (NV20/NV2A 3D class 0x97) state and CPU reference renderer.</summary>
public sealed class KelvinRenderer(Nv2aDevice gpu)
{
    private readonly uint[] _state = InitialState();
    private readonly Vector4[] _currentAttribute = Enumerable.Repeat(new Vector4(0, 0, 0, 1), 16).ToArray();
    private readonly List<Vector4[]> _vertices = [];
    private readonly HashSet<string> _reported = [];
    private int _programLoad, _constantLoad, _primitive;

    public VertexProgram Program { get; } = new();
    public Combiners Combiners { get; } = new();
    public long TrianglesDrawn { get; private set; }
    public long PointsDrawn { get; private set; }
    public long LinesDrawn { get; private set; }
    public long PixelsWritten { get; private set; }
    public long Clears { get; private set; }

    /// <summary>Host time spent clearing, transforming vertices and rasterising (profiling).</summary>
    public TimeSpan ClearTime { get; private set; }
    public TimeSpan VertexTime { get; private set; }
    public TimeSpan RasterTime { get; private set; }
    public uint FlipRead { get; private set; }
    public uint FlipWrite { get; private set; }

    public uint State(uint method) => _state[(method & 0x1FFC) >> 2];

    private static uint[] InitialState()
    {
        var s = new uint[0x2000 / 4];
        for (var i = 0; i < 3; i++) s[(0x9E0 / 4) + i] = BitConverter.SingleToUInt32Bits(FixedFunction.DefaultSpecularParams[i]);
        return s;
    }

    private float StateF(uint method) => BitConverter.UInt32BitsToSingle(State(method));

    private void ReportOnce(string message)
    {
        if (_reported.Add(message)) gpu.Report(message);
    }

    internal void FlipReadIncrement() => FlipRead = (FlipRead + 1) % Math.Max(1, State(0x128));

    public void Method(uint method, uint p)
    {
        _state[(method & 0x1FFC) >> 2] = p;
        switch (method)
        {
            case 0x12C: FlipWrite = (FlipWrite + 1) % Math.Max(1, State(0x128)); return; // FLIP_INCREMENT_WRITE
            case 0x260 or 0x264 or 0x268 or 0x26C or 0x270 or 0x274 or 0x278 or 0x27C: Combiners.AlphaIcw[(method - 0x260) / 4] = p; return;
            case 0x288: Combiners.FinalCw0 = p; return;
            case 0x28C: Combiners.FinalCw1 = p; return;
            case >= 0xA60 and < 0xA80: Combiners.Factor0[(method - 0xA60) / 4] = p; return;
            case >= 0xA80 and < 0xAA0: Combiners.Factor1[(method - 0xA80) / 4] = p; return;
            case >= 0xAA0 and < 0xAC0: Combiners.AlphaOcw[(method - 0xAA0) / 4] = p; return;
            case >= 0xAC0 and < 0xAE0: Combiners.ColorIcw[(method - 0xAC0) / 4] = p; return;
            case >= 0x1E40 and < 0x1E60: Combiners.ColorOcw[(method - 0x1E40) / 4] = p; return;
            case 0x1E60: Combiners.Control = p; return;
            case >= 0xB00 and < 0xB80: // SET_TRANSFORM_PROGRAM
                {
                    var slot = (int)((method - 0xB00) / 4);
                    if (_programLoad < VertexProgram.Slots) Program.Program[_programLoad, slot % 4] = p;
                    if (slot % 4 == 3) _programLoad++;
                    return;
                }
            case >= 0xB80 and < 0xC00: // SET_TRANSFORM_CONSTANT
                {
                    var slot = (int)((method - 0xB80) / 4);
                    if (_constantLoad < VertexProgram.Constants)
                    {
                        ref var c = ref Program.Constant[_constantLoad];
                        var f = BitConverter.UInt32BitsToSingle(p);
                        switch (slot % 4) { case 0: c.X = f; break; case 1: c.Y = f; break; case 2: c.Z = f; break; default: c.W = f; break; }
                    }
                    if (slot % 4 == 3) _constantLoad++;
                    return;
                }
            case 0x1E9C: _programLoad = (int)p; return;   // SET_TRANSFORM_PROGRAM_LOAD
            case 0x1EA4: _constantLoad = (int)p; return;  // SET_TRANSFORM_CONSTANT_LOAD
            case 0x1D94: Clear(p); return;                // CLEAR_SURFACE
            case 0x17FC: BeginEnd(p); return;             // SET_BEGIN_END
            case 0x1810:                                  // DRAW_ARRAYS
                {
                    var start = p & 0xFF_FFFF;
                    var count = (p >> 24) + 1;
                    for (var i = 0u; i < count; i++) _vertices.Add(FetchVertex(start + i));
                    return;
                }
            case 0x1800: _vertices.Add(FetchVertex(p & 0xFFFF)); _vertices.Add(FetchVertex(p >> 16)); return; // ARRAY_ELEMENT16
            case 0x1808: _vertices.Add(FetchVertex(p)); return;                                              // ARRAY_ELEMENT32
            case 0x1818: InlineWord(p); return;                                                              // INLINE_ARRAY
            case >= 0x1A00 and < 0x1B00: // SET_VERTEX_DATA4F_M
                {
                    var attr = (int)((method - 0x1A00) / 16);
                    var comp = (int)((method - 0x1A00) / 4 % 4);
                    var f = BitConverter.UInt32BitsToSingle(p);
                    ref var a = ref _currentAttribute[attr];
                    switch (comp) { case 0: a.X = f; break; case 1: a.Y = f; break; case 2: a.Z = f; break; default: a.W = f; break; }
                    if (attr == 0 && comp == 3) _vertices.Add((Vector4[])_currentAttribute.Clone());
                    return;
                }
            case >= 0x1940 and < 0x1980: // SET_VERTEX_DATA4UB
                {
                    var attr = (int)((method - 0x1940) / 4);
                    _currentAttribute[attr] = new Vector4(p & 0xFF, (p >> 8) & 0xFF, (p >> 16) & 0xFF, p >> 24) / 255f;
                    if (attr == 0) _vertices.Add((Vector4[])_currentAttribute.Clone());
                    return;
                }
            case >= 0x1500 and < 0x150C: SetComponent(0, (method - 0x1500) / 4, p, emitOn: 2, w1: true); return;      // SET_VERTEX3F
            case >= 0x1518 and < 0x1528: SetComponent(0, (method - 0x1518) / 4, p, emitOn: 3, w1: false); return;     // SET_VERTEX4F
            case >= 0x1530 and < 0x153C: SetComponent(2, (method - 0x1530) / 4, p, emitOn: -1, w1: false); return;    // SET_NORMAL3F
            case >= 0x1590 and < 0x1598: SetComponent(9, (method - 0x1590) / 4, p, emitOn: -1, w1: false); return;    // SET_TEXCOORD0_2F
            case >= 0x15A0 and < 0x15B0: SetComponent(9, (method - 0x15A0) / 4, p, emitOn: -1, w1: false); return;    // SET_TEXCOORD0_4F
            case >= 0x1550 and < 0x1560: // SET_DIFFUSE_COLOR4F -> attribute 3
                {
                    var f = BitConverter.UInt32BitsToSingle(p);
                    ref var a = ref _currentAttribute[3];
                    switch ((method - 0x1550) / 4) { case 0: a.X = f; break; case 1: a.Y = f; break; case 2: a.Z = f; break; default: a.W = f; break; }
                    return;
                }
        }
    }

    private void SetComponent(int attr, uint comp, uint p, int emitOn, bool w1)
    {
        var f = BitConverter.UInt32BitsToSingle(p);
        ref var a = ref _currentAttribute[attr];
        switch (comp) { case 0: a.X = f; break; case 1: a.Y = f; break; case 2: a.Z = f; break; default: a.W = f; break; }
        if (comp == (uint)emitOn)
        {
            if (w1) a.W = 1;
            _vertices.Add((Vector4[])_currentAttribute.Clone());
        }
    }

    // ------------------------------------------------------------------ surfaces

    private DmaObject Dma(uint method) => DmaObject.Load(gpu, State(method));

    private readonly record struct Surface(uint Address, uint DmaOffset, uint Limit, int Pitch, int Bpp, uint Format, bool Present, int SwizzleWidth = 0, int SwizzleHeight = 0)
    {
        public bool Swizzled => SwizzleWidth > 0;

        public uint Offset(int x, int y) => Swizzled
            ? Textures.SwizzleOffset(x, y, SwizzleWidth, SwizzleHeight) * (uint)Bpp
            : (uint)((y * Pitch) + (x * Bpp));
    }

    /// <summary>Swizzled render targets: power-of-two size from SET_SURFACE_FORMAT width/height (log2).</summary>
    private (int W, int H) SwizzleSize() => ((State(0x208) >> 8) & 0xF) == 2
        ? (1 << (int)((State(0x208) >> 16) & 0xFF), 1 << (int)((State(0x208) >> 24) & 0xFF))
        : (0, 0);

    private Surface ColorSurface()
    {
        var fmt = State(0x208) & 0xF;
        var bpp = fmt switch { 1 or 2 or 3 => 2, 4 or 5 or 6 or 7 or 8 => 4, 9 => 1, 0xA => 2, _ => 0 };
        var dma = Dma(0x194);
        var (sw, sh) = SwizzleSize();
        return new Surface(dma.Address + State(0x210), State(0x210), dma.Limit, (int)(State(0x20C) & 0xFFFF), bpp, fmt, bpp > 0 && State(0x194) != 0, sw, sh);
    }

    private Surface ZetaSurface()
    {
        var fmt = (State(0x208) >> 4) & 0xF;
        var bpp = fmt switch { 1 => 2, 2 => 4, _ => 0 };
        var dma = Dma(0x198);
        var (sw, sh) = SwizzleSize();
        return new Surface(dma.Address + State(0x214), State(0x214), dma.Limit, (int)(State(0x20C) >> 16), bpp, fmt, bpp > 0 && State(0x198) != 0, sw, sh);
    }

    private (int X0, int Y0, int X1, int Y1) ClipRect()
    {
        int x = (int)(State(0x200) & 0xFFFF), w = (int)(State(0x200) >> 16);
        int y = (int)(State(0x204) & 0xFFFF), h = (int)(State(0x204) >> 16);
        return (x, y, x + w, y + h);
    }

    private bool CheckSurfaceType()
    {
        var aa = (State(0x208) >> 12) & 0xF;
        if (aa > 2) { ReportOnce($"NV2A anti-aliasing mode {aa} is not defined - draw skipped"); return false; }
        return true;
    }

    private void Clear(uint flags)
    {
        var t = System.Diagnostics.Stopwatch.GetTimestamp();
        ClearCore(flags);
        ClearTime += System.Diagnostics.Stopwatch.GetElapsedTime(t);
    }

    private void ClearCore(uint flags)
    {
        Clears++;
        if (!CheckSurfaceType()) return;
        var (cx0, cy0, cx1, cy1) = ClipRect();
        var (sx, sy) = AntiAliasScale(); // clear rectangles are in pixels; supersampled targets hold sxxsy samples each
        int x0 = Math.Max((int)(State(0x1D98) & 0xFFFF), cx0) * sx, x1 = ((Math.Min((int)(State(0x1D98) >> 16), cx1 - 1) + 1) * sx) - 1;
        int y0 = Math.Max((int)(State(0x1D9C) & 0xFFFF), cy0) * sy, y1 = ((Math.Min((int)(State(0x1D9C) >> 16), cy1 - 1) + 1) * sy) - 1;
        var color = ColorSurface();
        if (gpu.Backend is { OwnsMemory: true } owner)
        {
            owner.Clear(new GpuClear(ToGpu(color), ToGpu(ZetaSurface()), x0, y0, x1, y1, flags, State(0x1D90), State(0x1D8C), cx1, cy1, sx, sy));
            return;
        }
        if ((flags & 0xF0) != 0 && color.Present)
        {
            var value = State(0x1D90);
            uint mask = 0;
            if (color.Bpp == 4) mask = ((flags & 0x10) != 0 ? 0x00FF_0000u : 0) | ((flags & 0x20) != 0 ? 0x0000_FF00u : 0) | ((flags & 0x40) != 0 ? 0x0000_00FFu : 0) | ((flags & 0x80) != 0 ? 0xFF00_0000u : 0);
            else mask = 0xFFFF;
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++) WritePixel(color, x, y, value, mask);
        }
        var zeta = ZetaSurface();
        if ((flags & 3) != 0 && zeta.Present)
        {
            var value = State(0x1D8C);
            var mask = zeta.Bpp == 2 ? 0xFFFFu : ((flags & 1) != 0 ? 0xFFFF_FF00u : 0) | ((flags & 2) != 0 ? 0xFFu : 0);
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++) WritePixel(zeta, x, y, value, mask);
        }
        gpu.Backend?.Clear(new GpuClear(ToGpu(color), ToGpu(zeta), x0, y0, x1, y1, flags, State(0x1D90), State(0x1D8C), cx1, cy1, sx, sy));
    }

    // ------------------------------------------------------------------ host GPU backend

    private static GpuSurface ToGpu(Surface s) => new(s.Address, s.Pitch, s.Format, s.Bpp, s.Present, s.Swizzled, s.SwizzleWidth, s.SwizzleHeight);

    /// <summary>
    /// Sends the draw to the host-GPU backend: the same triangles the CPU rasteriser draws (same assembly order, same
    /// culling, same front/back colour choice). Points become one-pixel quads, lines one-pixel-wide quads.
    /// </summary>
    private void EmitDraw(IRenderBackend backend, Vector4[][] outs)
    {
        var verts = new List<float>(outs.Length * 3 * GpuDraw.FloatsPerVertex);
        var twoSided = State(0x17C4) != 0;
        void Tri(Vector4[] a, Vector4[] b, Vector4[] c)
        {
            Vector4 q0 = a[VertexProgram.OPos], q1 = b[VertexProgram.OPos], q2 = c[VertexProgram.OPos];
            var area = ((q1.X - q0.X) * (double)(q2.Y - q0.Y)) - ((q2.X - q0.X) * (double)(q1.Y - q0.Y));
            if (area == 0 || double.IsNaN(area)) return;
            var clockwise = area > 0;
            var front = State(0x3A0) == 0x900 ? clockwise : !clockwise;
            if (State(0x308) != 0)
            {
                var cull = State(0x39C);
                if (cull == 0x408 || (cull == 0x404 && front) || (cull == 0x405 && !front)) return;
            }
            if (backend.OwnsMemory) TrianglesDrawn++;
            var color = !front && twoSided ? VertexProgram.OB0 : VertexProgram.OD0;
            var spec = color == VertexProgram.OB0 ? VertexProgram.OB1 : VertexProgram.OD1;
            GpuGeometry.Append(verts, a, color, spec);
            GpuGeometry.Append(verts, b, color, spec);
            GpuGeometry.Append(verts, c, color, spec);
        }
        void Quad(Vector4[] v, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            GpuGeometry.Append(verts, v, VertexProgram.OD0, VertexProgram.OD1, p0);
            GpuGeometry.Append(verts, v, VertexProgram.OD0, VertexProgram.OD1, p1);
            GpuGeometry.Append(verts, v, VertexProgram.OD0, VertexProgram.OD1, p2);
            GpuGeometry.Append(verts, v, VertexProgram.OD0, VertexProgram.OD1, p0);
            GpuGeometry.Append(verts, v, VertexProgram.OD0, VertexProgram.OD1, p2);
            GpuGeometry.Append(verts, v, VertexProgram.OD0, VertexProgram.OD1, p3);
        }
        void Pt(Vector4[] v)
        {
            var p = v[VertexProgram.OPos];
            // the pixel containing the point, every anti-aliasing sample of it (as the CPU renderer covers it)
            float x = MathF.Floor(p.X + 0.5f), y = MathF.Floor(p.Y + 0.5f);
            Quad(v, new(x, y), new(x + 1, y), new(x + 1, y + 1), new(x, y + 1));
        }
        void Ln(Vector4[] a, Vector4[] b)
        {
            Vector4 p = a[VertexProgram.OPos], q = b[VertexProgram.OPos];
            var xMajor = MathF.Abs(q.X - p.X) >= MathF.Abs(q.Y - p.Y);
            var (aaX, aaY) = AntiAliasScale();
            var h = xMajor ? new Vector2(0, 0.5f / aaY) : new Vector2(0.5f / aaX, 0); // one sample wide
            Vector2 s = new(p.X, p.Y), e = new(q.X, q.Y);
            // attributes are taken from the first vertex (flat), an approximation of the interpolated CPU line
            Quad(a, s - h, e - h, e + h, s + h);
        }
        var n = outs.Length;
        switch (_primitive)
        {
            case 1: for (var i = 0; i < n; i++) Pt(outs[i]); break;
            case 2: for (var i = 0; i + 1 < n; i += 2) Ln(outs[i], outs[i + 1]); break;
            case 3: for (var i = 0; i + 1 < n; i++) Ln(outs[i], outs[i + 1]); if (n > 2) Ln(outs[n - 1], outs[0]); break;
            case 4: for (var i = 0; i + 1 < n; i++) Ln(outs[i], outs[i + 1]); break;
            case 5: for (var i = 0; i + 2 < n; i += 3) Tri(outs[i], outs[i + 1], outs[i + 2]); break;
            case 6: for (var i = 0; i + 2 < n; i++) { if ((i & 1) == 0) Tri(outs[i], outs[i + 1], outs[i + 2]); else Tri(outs[i + 1], outs[i], outs[i + 2]); } break;
            case 7 or 10: for (var i = 1; i + 1 < n; i++) Tri(outs[0], outs[i], outs[i + 1]); break;
            case 8: for (var i = 0; i + 3 < n; i += 4) { Tri(outs[i], outs[i + 1], outs[i + 2]); Tri(outs[i], outs[i + 2], outs[i + 3]); } break;
            case 9: for (var i = 0; i + 3 < n; i += 2) { Tri(outs[i], outs[i + 1], outs[i + 3]); Tri(outs[i], outs[i + 3], outs[i + 2]); } break;
            default: return;
        }
        if (verts.Count == 0) return;
        var (sx, sy) = AntiAliasScale();
        var (cx0, cy0, cx1, cy1) = ClipRect();
        var color = ColorSurface();
        var zeta = ZetaSurface();
        var ops = FragmentOps.FromState(State);
        var words = new uint[51];
        Combiners.ColorIcw.CopyTo(words, 0);
        Combiners.ColorOcw.CopyTo(words, 8);
        Combiners.AlphaIcw.CopyTo(words, 16);
        Combiners.AlphaOcw.CopyTo(words, 24);
        Combiners.Factor0.CopyTo(words, 32);
        Combiners.Factor1.CopyTo(words, 40);
        words[48] = Combiners.Control;
        words[49] = Combiners.FinalCw0;
        words[50] = Combiners.FinalCw1;
        var fog = State(0x2A8);
        backend.Draw(new GpuDraw
        {
            Color = ToGpu(color),
            Zeta = ToGpu(zeta),
            Width = cx1,
            Height = cy1,
            AaX = sx,
            AaY = sy,
            ClipX0 = cx0 * sx, ClipY0 = cy0 * sy, ClipX1 = cx1 * sx, ClipY1 = cy1 * sy,
            Vertices = verts.ToArray(),
            VertexCount = verts.Count / GpuDraw.FloatsPerVertex,
            CombinerWords = words,
            Ops = ops,
            DepthTest = State(0x30C) != 0 && zeta.Present,
            DepthWrite = State(0x35C) != 0 && zeta.Present,
            DepthFunc = State(0x354),
            StencilActive = zeta.Present && zeta.Bpp == 4 && ops.StencilTest,
            ColorMask = State(0x358),
            FogColor = new Vector3(fog & 0xFF, (fog >> 8) & 0xFF, (fog >> 16) & 0xFF) / 255f,
            StageModes = (int[])_stageMode.Clone(),
            Textures = (TextureUnit?[])_textures.Clone(),
        });
    }

    private void WritePixel(Surface s, int x, int y, uint value, uint mask)
    {
        var offset = s.Offset(x, y);
        if (s.DmaOffset + offset + (uint)s.Bpp - 1 > s.Limit) { ReportOnce("NV2A render target access beyond its DMA limit - clipped"); return; }
        var a = s.Address + offset;
        if (s.Bpp == 4) gpu.Memory.Write32(a, (gpu.Memory.Read32(a) & ~mask) | (value & mask));
        else if (s.Bpp == 2) gpu.Memory.Write16(a, (ushort)((gpu.Memory.Read16(a) & ~mask) | (value & mask)));
        else gpu.Memory.Write8(a, (byte)value);
    }

    private uint ReadPixel(Surface s, int x, int y)
    {
        var a = s.Address + s.Offset(x, y);
        return s.Bpp == 4 ? gpu.Memory.Read32(a) : s.Bpp == 2 ? gpu.Memory.Read16(a) : gpu.Memory.Read8(a);
    }

    // ------------------------------------------------------------------ vertices

    private Vector4[] FetchVertex(uint index)
    {
        var v = (Vector4[])_currentAttribute.Clone();
        for (var i = 0; i < 16; i++)
        {
            var fmt = State(0x1760 + (uint)(i * 4));
            var size = (int)((fmt >> 4) & 0xF);
            if (size == 0) continue;
            var type = fmt & 0xF;
            var stride = fmt >> 8;
            var offset = State(0x1720 + (uint)(i * 4));
            var dma = Dma((offset & 0x8000_0000) != 0 ? 0x1A0u : 0x19Cu);
            var a = dma.Address + (offset & 0x7FFF_FFFF) + (index * stride);
            v[i] = ReadAttribute(a, type, size, out var _);
        }
        return v;
    }

    private Vector4 ReadAttribute(uint a, uint type, int size, out int bytes)
    {
        var m = gpu.Memory;
        var r = new Vector4(0, 0, 0, 1);
        switch (type)
        {
            case 2: // F
                for (var c = 0; c < Math.Min(size, 4); c++) r[c] = BitConverter.UInt32BitsToSingle(m.Read32(a + (uint)(c * 4)));
                bytes = size * 4;
                break;
            case 0: // UB_D3D: bytes B,G,R,A
                {
                    var w = m.Read32(a);
                    r = new Vector4((w >> 16) & 0xFF, (w >> 8) & 0xFF, w & 0xFF, w >> 24) / 255f;
                    bytes = 4;
                    break;
                }
            case 4: // UB_OGL: bytes R,G,B,A
                for (var c = 0; c < Math.Min(size, 4); c++) r[c] = m.Read8(a + (uint)c) / 255f;
                bytes = size;
                break;
            case 1: // S1: normalised shorts
                for (var c = 0; c < Math.Min(size, 4); c++) r[c] = Math.Max(-1f, (short)m.Read16(a + (uint)(c * 2)) / 32767f);
                bytes = size * 2;
                break;
            case 5: // S32K: shorts as integers
                for (var c = 0; c < Math.Min(size, 4); c++) r[c] = (short)m.Read16(a + (uint)(c * 2));
                bytes = size * 2;
                break;
            default:
                ReportOnce($"NV2A vertex attribute type {type} not implemented (attribute read as 0,0,0,1)");
                bytes = 4;
                break;
        }
        return r;
    }

    private List<uint> _inline = [];

    private void InlineWord(uint word)
    {
        _inline.Add(word);
        var needed = 0;
        for (var i = 0; i < 16; i++)
        {
            var fmt = State(0x1760 + (uint)(i * 4));
            var size = (int)((fmt >> 4) & 0xF);
            if (size == 0) continue;
            needed += (fmt & 0xF) switch { 2 => size, 1 or 5 => (size + 1) / 2, _ => 1 };
        }
        if (needed == 0 || _inline.Count < needed) return;
        var v = (Vector4[])_currentAttribute.Clone();
        var k = 0;
        for (var i = 0; i < 16; i++)
        {
            var fmt = State(0x1760 + (uint)(i * 4));
            var size = (int)((fmt >> 4) & 0xF);
            if (size == 0) continue;
            switch (fmt & 0xF)
            {
                case 2:
                    var f = new Vector4(0, 0, 0, 1);
                    for (var c = 0; c < size; c++) f[c] = BitConverter.UInt32BitsToSingle(_inline[k++]);
                    v[i] = f;
                    break;
                case 0:
                    var w = _inline[k++];
                    v[i] = new Vector4((w >> 16) & 0xFF, (w >> 8) & 0xFF, w & 0xFF, w >> 24) / 255f;
                    break;
                default:
                    ReportOnce($"NV2A inline vertex attribute type {fmt & 0xF} not implemented");
                    k += (size + 1) / 2;
                    break;
            }
        }
        _vertices.Add(v);
        _inline = [];
    }

    // ------------------------------------------------------------------ primitives

    private void BeginEnd(uint op)
    {
        if (op != 0)
        {
            _primitive = (int)op;
            _vertices.Clear();
            _inline.Clear();
            return;
        }
        if (_vertices.Count == 0) return;
        Draw();
        _vertices.Clear();
    }

    private void Draw()
    {
        if (!CheckSurfaceType()) return;
        var fixedFunction = (State(0x1E94) & 3) == 0;
        if (!fixedFunction && (State(0x1E94) & 3) != 2)
        {
            ReportOnce($"NV2A transform execution mode {State(0x1E94) & 3} not implemented - primitives skipped");
            return;
        }
        if (State(0x310) != 0) ReportOnce("NV2A dithering not modelled - colours are rounded, not dithered");
        if (State(0x304) != 0 && State(0x350) is 0xF005 or 0xF006) ReportOnce("NV2A signed blend equations not implemented - FUNC_ADD used");
        if ((State(0x290) & 0x1_0000) != 0) ReportOnce("NV2A W-buffering (Z perspective) not implemented - depth uses oPos.z");
        _textures = BuildTextures();

        var start = (int)State(0x1EA0);
        var cwe = State(0x1E98) != 0;
        var t = System.Diagnostics.Stopwatch.GetTimestamp();
        var outs = new Vector4[_vertices.Count][];
        for (var i = 0; i < outs.Length; i++)
            outs[i] = fixedFunction ? FixedFunction.Execute(_vertices[i], State, ReportOnce) : Program.Execute(_vertices[i], start, cwe, ReportOnce);
        // fog factor from the fog distance (oFog.x), for both pipelines; 1 when fog is disabled
        var fogEnabled = State(0x2A4) != 0;
        foreach (var o in outs)
            o[VertexProgram.OFog].X = fogEnabled ? FixedFunction.FogFactor(o[VertexProgram.OFog].X, State(0x29C), StateF(0x9C0), StateF(0x9C4)) : 1;
        VertexTime += System.Diagnostics.Stopwatch.GetElapsedTime(t);
        t = System.Diagnostics.Stopwatch.GetTimestamp();
        if (gpu.Backend is { OwnsMemory: true } owner) EmitDraw(owner, outs); // the host GPU rasterises; no CPU pixels
        else
        {
            Assemble(outs);
            if (gpu.Backend is { } mirror) EmitDraw(mirror, outs);
        }
        RasterTime += System.Diagnostics.Stopwatch.GetElapsedTime(t);
    }

    private TextureUnit?[] _textures = new TextureUnit?[4];
    private readonly int[] _stageMode = new int[4];

    /// <summary>Decodes the texture shader program and the four texture units for this draw.</summary>
    private TextureUnit?[] BuildTextures()
    {
        var units = new TextureUnit?[4];
        var program = State(0x1E70);
        for (var i = 0; i < 4; i++)
        {
            var mode = (int)((program >> (5 * i)) & 0x1F);
            _stageMode[i] = mode;
            if (mode == 0) continue;
            if (mode is not (1 or 4))
            {
                ReportOnce($"NV2A texture shader mode {mode} on stage {i} not implemented (3D/cube/bump/dot-product) - stage reads 0");
                _stageMode[i] = 0;
                continue;
            }
            if (mode == 4) continue; // PASS_THROUGH: the texture coordinate itself is the colour
            uint b = 0x1B00 + (uint)(i * 0x40);
            var control0 = State(b + 0x0C);
            if ((control0 & (1u << 30)) == 0) { _stageMode[i] = 0; continue; } // texture disabled
            var fmt = State(b + 0x04);
            var color = (fmt >> 8) & 0xFF;
            if (!Textures.IsSupported(color))
            {
                ReportOnce($"NV2A texture format 0x{color:X2} not implemented - stage {i} reads 0");
                _stageMode[i] = 0;
                continue;
            }
            if ((fmt & 4) != 0 || ((fmt >> 4) & 0xF) == 3) ReportOnce("NV2A cube/3D textures not implemented - sampled as 2D");
            if (((fmt >> 16) & 0xF) > 1 && ((State(b + 0x14) >> 16) & 0xFF) >= 3) ReportOnce("NV2A mipmapping not implemented - level 0 is sampled");
            if ((control0 & 3) != 0) ReportOnce("NV2A texture colour key not implemented - ignored");
            if ((State(b + 0x14) & 0xF000_0000) != 0) ReportOnce("NV2A signed texture channels not implemented - read as unsigned");
            var swizzled = Textures.IsSwizzled(color);
            var compressed = Textures.IsCompressed(color);
            var dma = Dma((fmt & 3) == 2 ? 0x188u : 0x184u);
            var rect = State(b + 0x1C);
            var filter = State(b + 0x14);
            units[i] = new TextureUnit
            {
                Stage = i,
                Enabled = true,
                Address = dma.Address + State(b),
                Format = color,
                Swizzled = swizzled,
                Compressed = compressed,
                Normalized = swizzled || compressed,
                Width = swizzled || compressed ? 1 << (int)((fmt >> 20) & 0xF) : (int)(rect >> 16),
                Height = swizzled || compressed ? 1 << (int)((fmt >> 24) & 0xF) : (int)(rect & 0xFFFF),
                Pitch = (int)(State(b + 0x10) >> 16),
                AddressU = (int)(State(b + 0x08) & 0xF),
                AddressV = (int)((State(b + 0x08) >> 8) & 0xF),
                MinFilter = (int)((filter >> 16) & 0xFF),
                MagFilter = (int)((filter >> 24) & 0xF),
                Border = Combiners.Unpack(State(b + 0x24)),
                AlphaKill = (control0 & 4) != 0,
            };
            if (units[i]!.Width <= 0 || units[i]!.Height <= 0) { units[i] = null; _stageMode[i] = 0; }
        }
        return units;
    }

    private Vector4 TextureStage(int i, Vector4 tc)
    {
        switch (_stageMode[i])
        {
            case 4: return Vector4.Clamp(tc, Vector4.Zero, Vector4.One);
            case 1 when _textures[i] is { } unit:
                {
                    var q = tc.W == 0 ? 1 : tc.W; // 2D projective: (s/q, t/q)
                    return Textures.Sample(gpu.Memory, unit, tc.X / q, tc.Y / q);
                }
            default: return Vector4.Zero;
        }
    }

    private void Assemble(Vector4[][] outs)
    {
        void Tri(int a, int b, int c) => Triangle(outs[a], outs[b], outs[c]);
        void Line(int a, int b) => LineSegment(outs[a], outs[b]);
        var n = outs.Length;
        switch (_primitive)
        {
            case 1: for (var i = 0; i < n; i++) Point(outs[i]); break;                                   // POINTS
            case 2: for (var i = 0; i + 1 < n; i += 2) Line(i, i + 1); break;                            // LINES
            case 3: for (var i = 0; i + 1 < n; i++) Line(i, i + 1); if (n > 2) Line(n - 1, 0); break;    // LINE_LOOP
            case 4: for (var i = 0; i + 1 < n; i++) Line(i, i + 1); break;                               // LINE_STRIP
            case 5: for (var i = 0; i + 2 < n; i += 3) Tri(i, i + 1, i + 2); break;                    // TRIANGLES
            case 6: for (var i = 0; i + 2 < n; i++) { if ((i & 1) == 0) Tri(i, i + 1, i + 2); else Tri(i + 1, i, i + 2); } break; // STRIP
            case 7 or 10: for (var i = 1; i + 1 < n; i++) Tri(0, i, i + 1); break;                      // FAN, POLYGON
            case 8: for (var i = 0; i + 3 < n; i += 4) { Tri(i, i + 1, i + 2); Tri(i, i + 2, i + 3); } break; // QUADS
            case 9: for (var i = 0; i + 3 < n; i += 2) { Tri(i, i + 1, i + 3); Tri(i, i + 3, i + 2); } break; // QUAD_STRIP
            default: ReportOnce($"NV2A primitive type {_primitive} is not defined - skipped"); break;
        }
    }

    /// <summary>Everything a fragment needs that is constant for one primitive.</summary>
    private sealed class FragmentContext
    {
        public required Vector4[] V0, V1, V2;
        public required Vector4 P0, P1, P2;
        public float W0, W1, W2;
        public int ColorReg, SpecularReg;
        public Vector3 FogColor;
        public Surface Color, Zeta;
        public bool DepthTest, DepthWrite, HasStencil, ReadsDestination, AnyTexture, UseSpecular, UseFog;
        public uint DepthFunc, ColorMask;
        public double ZMax;
        public FragmentOps Ops;
        public int ScaleX = 1, ScaleY = 1;
        public int X0, Y0, X1, Y1; // clip rectangle in render-target (supersampled) pixels, exclusive upper bounds
    }

    /// <summary>Supersampling factors of the render target (SET_SURFACE_FORMAT anti-aliasing field).</summary>
    private (int X, int Y) AntiAliasScale() => ((State(0x208) >> 12) & 0xF) switch { 1 => (2, 1), 2 => (2, 2), _ => (1, 1) };

    private FragmentContext? Context(Vector4[] v0, Vector4[] v1, Vector4[] v2, bool front)
    {
        var (sx, sy) = AntiAliasScale();
        static Vector4 Scale(Vector4 p, int sx, int sy) => new(p.X * sx, p.Y * sy, p.Z, p.W);
        var color = ColorSurface();
        var zeta = ZetaSurface();
        var ops = FragmentOps.FromState(State);
        var (cx0, cy0, cx1, cy1) = ClipRect();
        var colorReg = !front && State(0x17C4) != 0 ? VertexProgram.OB0 : VertexProgram.OD0;
        var fogColorWord = State(0x2A8);
        Combiners.Prepare();
        return new FragmentContext
        {
            V0 = v0, V1 = v1, V2 = v2,
            P0 = Scale(v0[VertexProgram.OPos], sx, sy), P1 = Scale(v1[VertexProgram.OPos], sx, sy), P2 = Scale(v2[VertexProgram.OPos], sx, sy),
            W0 = v0[VertexProgram.OPos].W == 0 ? 1 : v0[VertexProgram.OPos].W,
            W1 = v1[VertexProgram.OPos].W == 0 ? 1 : v1[VertexProgram.OPos].W,
            W2 = v2[VertexProgram.OPos].W == 0 ? 1 : v2[VertexProgram.OPos].W,
            ColorReg = colorReg,
            SpecularReg = colorReg == VertexProgram.OB0 ? VertexProgram.OB1 : VertexProgram.OD1,
            FogColor = new Vector3(fogColorWord & 0xFF, (fogColorWord >> 8) & 0xFF, (fogColorWord >> 16) & 0xFF) / 255f,
            Color = color,
            Zeta = zeta,
            DepthTest = State(0x30C) != 0 && zeta.Present,
            DepthWrite = State(0x35C) != 0 && zeta.Present,
            HasStencil = zeta.Present && zeta.Bpp == 4 && ops.StencilTest,
            ReadsDestination = ops.Blend || ops.LogicOp,
            AnyTexture = _stageMode.Any(m => m != 0),
            UseSpecular = Combiners.Reads(5),
            UseFog = Combiners.Reads(3),
            DepthFunc = State(0x354),
            ColorMask = State(0x358),
            ZMax = zeta.Bpp == 2 ? 65535.0 : 16777215.0,
            Ops = ops,
            ScaleX = sx,
            ScaleY = sy,
            X0 = cx0 * sx, Y0 = cy0 * sy, X1 = cx1 * sx, Y1 = cy1 * sy,
        };
    }

    /// <summary>
    /// Rasterises one triangle in screen space (window coordinates in oPos.xy, depth in oPos.z, w for perspective-correct
    /// interpolation). Sampling convention: pixel centres at integer coordinates (Direct3D 8), top-left fill rule,
    /// a modelling choice, not compared with hardware. Anti-aliased targets are rendered supersampled (2x1 or 2x2
    /// samples per pixel at integer positions of the scaled grid; the hardware's sample offsets are not modelled).
    /// </summary>
    private void Triangle(Vector4[] v0, Vector4[] v1, Vector4[] v2)
    {
        var q0 = v0[VertexProgram.OPos];
        var q1 = v1[VertexProgram.OPos];
        var q2 = v2[VertexProgram.OPos];
        double rawArea = ((q1.X - q0.X) * (double)(q2.Y - q0.Y)) - ((q2.X - q0.X) * (double)(q1.Y - q0.Y));
        if (rawArea == 0 || double.IsNaN(rawArea)) return;
        var clockwise = rawArea > 0; // y grows downwards
        var front = State(0x3A0) == 0x900 ? clockwise : !clockwise;
        if (State(0x308) != 0) // culling
        {
            var cull = State(0x39C);
            if (cull == 0x408 || (cull == 0x404 && front) || (cull == 0x405 && !front)) return;
        }
        if (Context(v0, v1, v2, front) is not { } ctx) return;
        TrianglesDrawn++;
        Vector4 p0 = ctx.P0, p1 = ctx.P1, p2 = ctx.P2;
        double area = ((p1.X - p0.X) * (double)(p2.Y - p0.Y)) - ((p2.X - p0.X) * (double)(p1.Y - p0.Y));

        var minX = Math.Max(ctx.X0, (int)Math.Ceiling(Math.Min(p0.X, Math.Min(p1.X, p2.X))));
        var maxX = Math.Min(ctx.X1 - 1, (int)Math.Floor(Math.Max(p0.X, Math.Max(p1.X, p2.X))));
        var minY = Math.Max(ctx.Y0, (int)Math.Ceiling(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y))));
        var maxY = Math.Min(ctx.Y1 - 1, (int)Math.Floor(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))));
        if (minX > maxX || minY > maxY) return;
        var sign = area > 0 ? 1 : -1;

        // Top-left rule (y grows downwards). Clockwise traversal (sign > 0): a top edge runs to the right, left edges run
        // upwards. Counter-clockwise: a top edge runs to the left, left edges run downwards.
        static bool TopLeft(Vector4 a, Vector4 b, int sign) => sign > 0 ? (a.Y == b.Y && b.X > a.X) || b.Y < a.Y : (a.Y == b.Y && b.X < a.X) || b.Y > a.Y;
        bool tl0 = TopLeft(p1, p2, sign), tl1 = TopLeft(p2, p0, sign), tl2 = TopLeft(p0, p1, sign);

        // Edge functions E(x,y) = (b.x-a.x)(y-a.y) - (b.y-a.y)(x-a.x), stepped incrementally along a row (exact in double).
        double a0 = -(p2.Y - p1.Y) * sign, b0 = (p2.X - p1.X) * sign;
        double a1 = -(p0.Y - p2.Y) * sign, b1 = (p0.X - p2.X) * sign;
        double a2 = -(p1.Y - p0.Y) * sign, b2 = (p1.X - p0.X) * sign;
        double c0 = (-(p2.X - p1.X) * p1.Y + (p2.Y - p1.Y) * p1.X) * sign;
        double c1 = (-(p0.X - p2.X) * p2.Y + (p0.Y - p2.Y) * p2.X) * sign;
        double c2 = (-(p1.X - p0.X) * p0.Y + (p1.Y - p0.Y) * p0.X) * sign;
        var invArea = 1.0 / (area * sign);

        for (var y = minY; y <= maxY; y++)
        {
            double e0 = (a0 * minX) + (b0 * y) + c0, e1 = (a1 * minX) + (b1 * y) + c1, e2 = (a2 * minX) + (b2 * y) + c2;
            var colorRow = Row(ctx.Color, y, maxX);
            var zetaRow = Row(ctx.Zeta, y, maxX);
            for (var x = minX; x <= maxX; x++, e0 += a0, e1 += a1, e2 += a2)
            {
                if (e0 < 0 || e1 < 0 || e2 < 0) continue;
                if ((e0 == 0 && !tl0) || (e1 == 0 && !tl1) || (e2 == 0 && !tl2)) continue;
                var l0 = e0 * invArea;
                var l1 = e1 * invArea;
                Fragment(ctx, x, y, l0, l1, 1 - l0 - l1, colorRow, zetaRow);
            }
        }
    }

    /// <summary>
    /// A point covers the pixel containing its position (size 1; point size and point sprites are not modelled).
    /// With an anti-aliased target, every sample of that pixel is covered.
    /// </summary>
    private void Point(Vector4[] v)
    {
        if (Context(v, v, v, front: true) is not { } ctx) return;
        PointsDrawn++;
        var px = (int)MathF.Floor(v[VertexProgram.OPos].X + 0.5f);
        var py = (int)MathF.Floor(v[VertexProgram.OPos].Y + 0.5f);
        for (var sy = 0; sy < ctx.ScaleY; sy++)
            for (var sx = 0; sx < ctx.ScaleX; sx++)
            {
                int x = (px * ctx.ScaleX) + sx, y = (py * ctx.ScaleY) + sy;
                if (x < ctx.X0 || y < ctx.Y0 || x >= ctx.X1 || y >= ctx.Y1) continue;
                Fragment(ctx, x, y, 1, 0, 0, Span<byte>.Empty, Span<byte>.Empty);
            }
    }

    /// <summary>
    /// One-pixel-wide line by a digital differential analyser along the major axis, the last pixel excluded (as in the
    /// diamond-exit rule for connected segments). Attributes interpolated perspective-correctly along the segment.
    /// This is an approximation of the hardware's line rasterisation, not compared with it.
    /// </summary>
    private void LineSegment(Vector4[] v0, Vector4[] v1)
    {
        if (Context(v0, v1, v1, front: true) is not { } ctx) return;
        LinesDrawn++;
        Vector4 a = ctx.P0, b = ctx.P1;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(dx), MathF.Abs(dy)));
        if (steps == 0) return;
        var lastX = int.MinValue;
        var lastY = int.MinValue;
        for (var i = 0; i < steps; i++)
        {
            var t = i / (double)steps;
            var x = (int)Math.Floor(a.X + (dx * t) + 0.5);
            var y = (int)Math.Floor(a.Y + (dy * t) + 0.5);
            if (x == lastX && y == lastY) continue;
            lastX = x;
            lastY = y;
            if (x < ctx.X0 || y < ctx.Y0 || x >= ctx.X1 || y >= ctx.Y1) continue;
            Fragment(ctx, x, y, 1 - t, t, 0, Span<byte>.Empty, Span<byte>.Empty);
        }
    }

    /// <summary>Direct access to one render-target row (pitch layout only), or empty to use the per-pixel path.</summary>
    private Span<byte> Row(Surface s, int y, int maxX)
    {
        if (!s.Present || s.Swizzled) return Span<byte>.Empty;
        var length = (maxX + 1) * s.Bpp;
        if (s.DmaOffset + (uint)((y * s.Pitch) + length) - 1 > s.Limit) return Span<byte>.Empty;
        return gpu.Memory.TryGetWritableRam(s.Address + (uint)(y * s.Pitch), length, out var row) ? row : Span<byte>.Empty;
    }

    /// <summary>Shades one fragment with barycentric weights (l0, l1, l2) and runs the per-fragment operations.</summary>
    private void Fragment(FragmentContext c, int x, int y, double l0, double l1, double l2, Span<byte> colorRow, Span<byte> zetaRow)
    {
        var zeta = c.Zeta;
        var bppZ = zeta.Bpp;
        var z = (l0 * c.P0.Z) + (l1 * c.P1.Z) + (l2 * c.P2.Z);
        var depth = (uint)Math.Clamp(Math.Round(z), 0, c.ZMax);
        uint storedZeta = 0;
        if (c.DepthTest || c.HasStencil)
            storedZeta = zetaRow.IsEmpty ? ReadPixel(zeta, x, y)
                : bppZ == 4 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(zetaRow[(x * 4)..])
                : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(zetaRow[(x * 2)..]);
        var depthPass = !c.DepthTest || DepthPasses(c.DepthFunc, depth, bppZ == 2 ? storedZeta : storedZeta >> 8);
        if (!depthPass && !c.HasStencil) return; // no side effects possible: early rejection

        // perspective-correct attribute interpolation
        float q0 = (float)(l0 / c.W0), q1 = (float)(l1 / c.W1), q2 = (float)(l2 / c.W2);
        var inv = 1f / (q0 + q1 + q2);
        q0 *= inv; q1 *= inv; q2 *= inv;
        Vector4[] v0 = c.V0, v1 = c.V1, v2 = c.V2;
        // a0 + q1·(a1−a0) + q2·(a2−a0): exact for attributes that are constant over the primitive
        Vector4 Lerp(int r) => v0[r] + ((v1[r] - v0[r]) * q1) + ((v2[r] - v0[r]) * q2);
        var inputs = new CombinerInputs { Diffuse = Vector4.Clamp(Lerp(c.ColorReg), Vector4.Zero, Vector4.One) };
        if (c.UseSpecular) inputs.Specular = Vector4.Clamp(Lerp(c.SpecularReg), Vector4.Zero, Vector4.One);
        if (c.UseFog) inputs.Fog = new Vector4(c.FogColor, Math.Clamp(Lerp(VertexProgram.OFog).X, 0, 1));
        if (c.AnyTexture)
        {
            var killed = false;
            for (var t = 0; t < 4; t++)
            {
                if (_stageMode[t] == 0) continue;
                var r = VertexProgram.OT0 + t;
                var sample = TextureStage(t, Lerp(r));
                if (_textures[t] is { AlphaKill: true } && sample.W == 0) killed = true;
                switch (t) { case 0: inputs.T0 = sample; break; case 1: inputs.T1 = sample; break; case 2: inputs.T2 = sample; break; default: inputs.T3 = sample; break; }
            }
            if (killed) return;
        }
        var rgba = Combiners.Evaluate(inputs);
        var ops = c.Ops;

        // alpha test (8-bit alpha against the 8-bit reference)
        if (ops.AlphaTest && !DepthPasses(ops.AlphaFunc, (uint)Math.Clamp(MathF.Round(rgba.W * 255), 0, 255), ops.AlphaRef)) return;

        // stencil test and update (Z24S8 only: the low byte of the zeta word)
        if (c.HasStencil)
        {
            var stencil = storedZeta & 0xFF;
            var stencilPass = DepthPasses(ops.StencilFunc, ops.StencilRef & ops.StencilFuncMask, stencil & ops.StencilFuncMask);
            var op = !stencilPass ? ops.StencilFail : !depthPass ? ops.StencilZFail : ops.StencilZPass;
            var updated = FragmentOps.StencilOp(op, stencil, ops.StencilRef);
            if (ops.StencilWrite && updated != stencil)
            {
                storedZeta = (storedZeta & 0xFFFF_FF00u) | (((updated & ops.StencilWriteMask) | (stencil & ~ops.StencilWriteMask)) & 0xFF);
                WriteZetaWord(zeta, zetaRow, x, y, storedZeta);
            }
            if (!stencilPass || !depthPass) return;
        }

        if (c.DepthWrite)
        {
            var word = bppZ == 2 ? depth : (storedZeta & 0xFFu) | (depth << 8);
            if (bppZ == 4) WriteZetaWord(zeta, zetaRow, x, y, word);
            else WritePixel(zeta, x, y, word, 0xFFFF);
        }

        var color = c.Color;
        if (!color.Present) return;
        uint destination = 0;
        if (c.ReadsDestination)
            destination = !colorRow.IsEmpty && color.Bpp == 4 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(colorRow[(x * 4)..]) : ReadPixel(color, x, y);
        if (ops.Blend && !ops.LogicOp) rgba = ops.BlendColor(rgba, DecodeColor(color, destination));
        var (value, mask) = EncodeColor(color, rgba, c.ColorMask);
        if (ops.LogicOp) value = FragmentOps.Logic(ops.LogicOpCode, value, destination);
        if (!colorRow.IsEmpty && color.Bpp == 4)
        {
            var px = colorRow[(x * 4)..];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(px, (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(px) & ~mask) | (value & mask));
        }
        else WritePixel(color, x, y, value, mask);
        PixelsWritten++;
    }

    private void WriteZetaWord(Surface zeta, Span<byte> row, int x, int y, uint word)
    {
        if (!row.IsEmpty) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(row[(x * 4)..], word);
        else WritePixel(zeta, x, y, word, 0xFFFF_FFFF);
    }

    /// <summary>Decodes a render-target value to RGBA (alpha 1 for formats without alpha).</summary>
    private static Vector4 DecodeColor(Surface s, uint v) => s.Format switch
    {
        3 => new Vector4(((v >> 11) & 31) / 31f, ((v >> 5) & 63) / 63f, (v & 31) / 31f, 1),
        1 or 2 => new Vector4(((v >> 10) & 31) / 31f, ((v >> 5) & 31) / 31f, (v & 31) / 31f, 1),
        6 or 7 => new Vector4(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, ((v >> 24) & 0x7F) / 127f),
        8 => new Vector4(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, (v >> 24) / 255f),
        _ => new Vector4(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, 1),
    };

    private static bool DepthPasses(uint func, uint z, uint stored) => func switch
    {
        0x200 => false,
        0x201 => z < stored,
        0x202 => z == stored,
        0x203 => z <= stored,
        0x204 => z > stored,
        0x205 => z != stored,
        0x206 => z >= stored,
        _ => true,
    };

    private static (uint Value, uint Mask) EncodeColor(Surface s, Vector4 c, uint colorMask)
    {
        static uint B(float v) => (uint)Math.Clamp(MathF.Round(v * 255), 0, 255);
        if (s.Bpp == 4)
        {
            var alpha = s.Format switch { 4 => 0u, 5 => 0xFFu, 6 or 7 => B(c.W) & 0x7F, _ => B(c.W) };
            var value = (alpha << 24) | (B(c.X) << 16) | (B(c.Y) << 8) | B(c.Z);
            var mask = ((colorMask & 0x0100_0000) != 0 || s.Format is 4 or 5 ? 0xFF00_0000u : 0) | ((colorMask & 0x0001_0000) != 0 ? 0x00FF_0000u : 0)
                 | ((colorMask & 0x0000_0100) != 0 ? 0x0000_FF00u : 0) | ((colorMask & 0x0000_0001) != 0 ? 0x0000_00FFu : 0);
            return (value, mask);
        }
        if (s.Format == 3) return (((B(c.X) >> 3) << 11) | ((B(c.Y) >> 2) << 5) | (B(c.Z) >> 3), 0xFFFF);
        return (((B(c.X) >> 3) << 10) | ((B(c.Y) >> 3) << 5) | (B(c.Z) >> 3) | (s.Format == 2 ? 0x8000u : 0), 0xFFFF);
    }
}
