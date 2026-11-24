using System.Numerics;
using System.Runtime.CompilerServices;

namespace XboxPc.Emulation.Gpu.Nv2a;

/// <summary>Per-fragment inputs of the register combiners.</summary>
public record struct CombinerInputs(Vector4 Diffuse, Vector4 Specular, Vector4 Fog, Vector4 T0, Vector4 T1, Vector4 T2, Vector4 T3);

/// <summary>
/// NV2A register combiners (NV_register_combiners semantics, Kelvin method layout): up to 8 general stages, each with
/// an RGB and an alpha portion computing AB (product or dot), CD and AB+CD (or a mux on spare0.alpha), with input
/// mappings and output scale/bias, followed by the final combiner A·B + (1−A)·C + D (RGB) and G (alpha).
/// Register numbers: 0 zero, 1 constant 0, 2 constant 1, 3 fog, 4 v0 (diffuse), 5 v1 (specular), 8-11 t0-t3,
/// 12 r0 (spare0), 13 r1 (spare1), 14 v1+r0 sum (final only), 15 E·F product (final only).
/// The control words are decoded by <see cref="Prepare"/> (once per draw); <see cref="Evaluate"/> runs per fragment.
/// </summary>
public sealed class Combiners
{
    public uint[] ColorIcw { get; } = new uint[8];
    public uint[] ColorOcw { get; } = new uint[8];
    public uint[] AlphaIcw { get; } = new uint[8];
    public uint[] AlphaOcw { get; } = new uint[8];
    public uint[] Factor0 { get; } = new uint[8];
    public uint[] Factor1 { get; } = new uint[8];
    public uint Control { get; set; }
    public uint FinalCw0 { get; set; }
    public uint FinalCw1 { get; set; }

    public int Stages => (int)Math.Clamp(Control & 0xFF, 0, 8);

    private readonly record struct Input(int Reg, bool Alpha, int Map);

    private readonly record struct Stage(
        Input A, Input B, Input C, Input D, bool AbDot, bool CdDot, bool Mux, int Op, int AbDst, int CdDst, int SumDst, bool AbBlueToAlpha, bool CdBlueToAlpha,
        Input AA, Input AB, Input AC, Input AD, bool AMux, int AOp, int AAbDst, int ACdDst, int ASumDst, Vector4 C0, Vector4 C1);

    private readonly record struct FinalInput(int Reg, bool Alpha, bool Inverse);

    private readonly Vector4[] _reg = new Vector4[16]; // scratch register file (the renderer is single-threaded)
    private Stage[] _stages = [];
    private FinalInput _fa, _fb, _fc, _fd, _fe, _ff;
    private int _gReg;
    private bool _gAlpha, _gInverse, _specularClamp, _muxMsb;
    private Vector4 _finalC0, _finalC1;
    private bool _prepared;

    public static Vector4 Unpack(uint argb) => new(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, (argb >> 24) / 255f);

    /// <summary>True when any active stage or the final combiner reads register <paramref name="r"/>.</summary>
    public bool Reads(int r)
    {
        static bool In(uint w, int r) => ((w >> 24) & 0xF) == r || ((w >> 16) & 0xF) == r || ((w >> 8) & 0xF) == r || (w & 0xF) == r;
        for (var s = 0; s < Stages; s++)
            if (In(ColorIcw[s], r) || In(AlphaIcw[s], r)) return true;
        if (In(FinalCw0, r) || ((FinalCw1 >> 24) & 0xF) == r || ((FinalCw1 >> 16) & 0xF) == r || ((FinalCw1 >> 8) & 0xF) == r) return true;
        return r == 5 && Reads(14); // v1+r0 sum reads the specular colour
    }

    /// <summary>Decodes the current control words. Call after changing them and before evaluating fragments.</summary>
    public void Prepare()
    {
        static Input In(uint w, int shift) => new((int)((w >> shift) & 0xF), ((w >> (shift + 4)) & 1) != 0, (int)((w >> (shift + 5)) & 7));
        var eachFactor0 = ((Control >> 12) & 0xF) != 0;
        var eachFactor1 = ((Control >> 16) & 0xF) != 0;
        _muxMsb = ((Control >> 8) & 0xF) != 0;
        _stages = new Stage[Stages];
        for (var s = 0; s < _stages.Length; s++)
        {
            uint ci = ColorIcw[s], co = ColorOcw[s], ai = AlphaIcw[s], ao = AlphaOcw[s];
            _stages[s] = new Stage(
                In(ci, 24), In(ci, 16), In(ci, 8), In(ci, 0),
                ((co >> 13) & 1) != 0, ((co >> 12) & 1) != 0, ((co >> 14) & 1) != 0, (int)((co >> 15) & 7),
                (int)((co >> 4) & 0xF), (int)(co & 0xF), (int)((co >> 8) & 0xF), ((co >> 19) & 1) != 0, ((co >> 18) & 1) != 0,
                In(ai, 24), In(ai, 16), In(ai, 8), In(ai, 0), ((ao >> 14) & 1) != 0, (int)((ao >> 15) & 7),
                (int)((ao >> 4) & 0xF), (int)(ao & 0xF), (int)((ao >> 8) & 0xF),
                Unpack(Factor0[eachFactor0 ? s : 0]), Unpack(Factor1[eachFactor1 ? s : 0]));
        }
        static FinalInput Fin(uint w, int shift) => new((int)((w >> shift) & 0xF), ((w >> (shift + 4)) & 1) != 0, ((w >> (shift + 5)) & 1) != 0);
        _fa = Fin(FinalCw0, 24); _fb = Fin(FinalCw0, 16); _fc = Fin(FinalCw0, 8); _fd = Fin(FinalCw0, 0);
        _fe = Fin(FinalCw1, 24); _ff = Fin(FinalCw1, 16);
        _gReg = (int)((FinalCw1 >> 8) & 0xF);
        _gAlpha = ((FinalCw1 >> 12) & 1) != 0;
        _gInverse = ((FinalCw1 >> 13) & 1) != 0;
        _specularClamp = ((FinalCw1 >> 7) & 1) != 0;
        _finalC0 = Unpack(Factor0[0]);
        _finalC1 = Unpack(Factor1[0]);
        _prepared = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Map(float x, int map) => map switch
    {
        0 => MathF.Max(0, x),
        1 => 1 - Math.Clamp(x, 0, 1),
        2 => (2 * MathF.Max(0, x)) - 1,
        3 => (-2 * MathF.Max(0, x)) + 1,
        4 => MathF.Max(0, x) - 0.5f,
        5 => -MathF.Max(0, x) + 0.5f,
        6 => x,
        _ => -x,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Scale(float x, int op) => Math.Clamp(op switch
    {
        1 => x - 0.5f,
        2 => x * 2,
        3 => (x - 0.5f) * 2,
        4 => x * 4,
        6 => x / 2,
        _ => x,
    }, -1, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 Rgb(Vector4[] reg, Input i)
    {
        var src = reg[i.Reg];
        var v = i.Alpha ? new Vector3(src.W) : new Vector3(src.X, src.Y, src.Z);
        return i.Map == 6 ? v : new Vector3(Map(v.X, i.Map), Map(v.Y, i.Map), Map(v.Z, i.Map));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Alpha(Vector4[] reg, Input i)
    {
        var src = reg[i.Reg];
        return Map(i.Alpha ? src.W : src.Z, i.Map);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 Scale3(Vector3 v, int op) => op == 0 ? Vector3.Clamp(v, -Vector3.One, Vector3.One) : new(Scale(v.X, op), Scale(v.Y, op), Scale(v.Z, op));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 Final(Vector4[] reg, FinalInput i)
    {
        var src = reg[i.Reg];
        var v = Vector3.Clamp(i.Alpha ? new Vector3(src.W) : new Vector3(src.X, src.Y, src.Z), Vector3.Zero, Vector3.One);
        return i.Inverse ? Vector3.One - v : v;
    }

    /// <summary>Evaluates the combiners for one fragment and returns RGBA in [0,1].</summary>
    public Vector4 Evaluate(in CombinerInputs input)
    {
        if (!_prepared) Prepare();
        var reg = _reg;
        reg[0] = default; reg[6] = default; reg[7] = default; reg[13] = default; reg[14] = default; reg[15] = default;
        reg[3] = input.Fog;
        reg[4] = input.Diffuse;
        reg[5] = input.Specular;
        reg[8] = input.T0; reg[9] = input.T1; reg[10] = input.T2; reg[11] = input.T3;
        reg[12] = new Vector4(0, 0, 0, input.T0.W); // spare0.alpha starts as t0.alpha

        foreach (ref readonly var st in _stages.AsSpan())
        {
            reg[1] = st.C0;
            reg[2] = st.C1;
            var mux = _muxMsb ? reg[12].W >= 0.5f : (((int)MathF.Round(reg[12].W * 255)) & 1) != 0;

            Vector3 a = Rgb(reg, st.A), b = Rgb(reg, st.B), c = Rgb(reg, st.C), d = Rgb(reg, st.D);
            var ab = st.AbDot ? new Vector3(Vector3.Dot(a, b)) : a * b;
            var cd = st.CdDot ? new Vector3(Vector3.Dot(c, d)) : c * d;
            var sum = st.Mux ? (mux ? cd : ab) : ab + cd;
            ab = Scale3(ab, st.Op); cd = Scale3(cd, st.Op); sum = Scale3(sum, st.Op);

            float aa = Alpha(reg, st.AA), ab2 = Alpha(reg, st.AB), ac = Alpha(reg, st.AC), ad = Alpha(reg, st.AD);
            var aAB = Scale(aa * ab2, st.AOp);
            var aCD = Scale(ac * ad, st.AOp);
            var aSum = Scale(st.AMux ? (mux ? ac * ad : aa * ab2) : (aa * ab2) + (ac * ad), st.AOp);

            // writes happen after all inputs of the stage were read
            if (st.CdDst != 0) reg[st.CdDst] = new Vector4(cd, st.CdBlueToAlpha ? cd.Z : reg[st.CdDst].W);
            if (st.AbDst != 0) reg[st.AbDst] = new Vector4(ab, st.AbBlueToAlpha ? ab.Z : reg[st.AbDst].W);
            if (st.SumDst != 0) reg[st.SumDst] = new Vector4(sum, reg[st.SumDst].W);
            if (st.ACdDst != 0) reg[st.ACdDst].W = aCD;
            if (st.AAbDst != 0) reg[st.AAbDst].W = aAB;
            if (st.ASumDst != 0) reg[st.ASumDst].W = aSum;
        }

        // final combiner (unsigned inputs)
        reg[1] = _finalC0;
        reg[2] = _finalC1;
        var v1r0 = new Vector3(reg[5].X, reg[5].Y, reg[5].Z) + new Vector3(reg[12].X, reg[12].Y, reg[12].Z);
        if (_specularClamp) v1r0 = Vector3.Clamp(v1r0, Vector3.Zero, Vector3.One);
        reg[14] = new Vector4(v1r0, 0);
        reg[15] = new Vector4(Final(reg, _fe) * Final(reg, _ff), 0);
        Vector3 fa = Final(reg, _fa), fb = Final(reg, _fb), fc = Final(reg, _fc), fd = Final(reg, _fd);
        var rgb = Vector3.Clamp((fa * fb) + ((Vector3.One - fa) * fc) + fd, Vector3.Zero, Vector3.One);
        var g = Math.Clamp(_gAlpha ? reg[_gReg].W : reg[_gReg].Z, 0, 1);
        if (_gInverse) g = 1 - g;
        return new Vector4(rgb, g);
    }
}
