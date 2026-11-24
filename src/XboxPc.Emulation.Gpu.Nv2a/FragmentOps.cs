using System.Numerics;

namespace XboxPc.Emulation.Gpu.Nv2a;

/// <summary>
/// Per-fragment operations after the combiners, in OpenGL order (the Kelvin methods use GL enumerants):
/// alpha test (SET_ALPHA_TEST_ENABLE/FUNC/REF) -> stencil test (SET_STENCIL_TEST_ENABLE/FUNC/FUNC_REF/FUNC_MASK) ->
/// depth test -> stencil update (SET_STENCIL_OP_FAIL/ZFAIL/ZPASS, write mask SET_STENCIL_MASK, writes enabled by
/// SET_CONTROL0 bit 0) -> blending (SET_BLEND_ENABLE/FUNC_SFACTOR/DFACTOR/EQUATION/COLOR) or logic op
/// (SET_LOGIC_OP_ENABLE/LOGIC_OP, which replaces blending as in GL) -> colour mask.
/// Not modelled (reported): the signed blend equations, dithering.
/// </summary>
public readonly record struct FragmentOps(
    bool AlphaTest, uint AlphaFunc, uint AlphaRef,
    bool StencilTest, uint StencilFunc, uint StencilRef, uint StencilFuncMask, uint StencilFail, uint StencilZFail, uint StencilZPass,
    uint StencilWriteMask, bool StencilWrite,
    bool Blend, uint SrcFactor, uint DstFactor, uint Equation, Vector4 Constant,
    bool LogicOp, uint LogicOpCode)
{
    public static FragmentOps FromState(Func<uint, uint> state) => new(
        state(0x300) != 0, state(0x33C), state(0x340) & 0xFF,
        state(0x32C) != 0, state(0x364), state(0x368) & 0xFF, state(0x36C) & 0xFF, state(0x370), state(0x374), state(0x378),
        state(0x360) & 0xFF, (state(0x290) & 1) != 0,
        state(0x304) != 0, state(0x344), state(0x348), state(0x350), Combiners.Unpack(state(0x34C)),
        state(0x17BC) != 0, state(0x17C0));

    /// <summary>GL stencil operations (KEEP 0x1E00, ZERO 0, REPLACE 0x1E01, INCR_SAT 0x1E02, DECR_SAT 0x1E03, INVERT 0x150A, INCR_WRAP 0x8507, DECR_WRAP 0x8508).</summary>
    public static uint StencilOp(uint op, uint stencil, uint reference) => op switch
    {
        0 => 0,
        0x1E01 => reference,
        0x1E02 => Math.Min(stencil + 1, 0xFF),
        0x1E03 => stencil == 0 ? 0 : stencil - 1,
        0x150A => ~stencil & 0xFF,
        0x8507 => (stencil + 1) & 0xFF,
        0x8508 => (stencil - 1) & 0xFF,
        _ => stencil, // KEEP
    };

    private static Vector4 Factor(uint f, Vector4 src, Vector4 dst, Vector4 constant) => f switch
    {
        0 => Vector4.Zero,
        1 => Vector4.One,
        0x300 => src,
        0x301 => Vector4.One - src,
        0x302 => new Vector4(src.W),
        0x303 => new Vector4(1 - src.W),
        0x304 => new Vector4(dst.W),
        0x305 => new Vector4(1 - dst.W),
        0x306 => dst,
        0x307 => Vector4.One - dst,
        0x308 => new Vector4(MathF.Min(src.W, 1 - dst.W), MathF.Min(src.W, 1 - dst.W), MathF.Min(src.W, 1 - dst.W), 1),
        0x8001 => constant,
        0x8002 => Vector4.One - constant,
        0x8003 => new Vector4(constant.W),
        0x8004 => new Vector4(1 - constant.W),
        _ => Vector4.One,
    };

    /// <summary>Blends the combiner output with the destination colour (both RGBA in [0,1]).</summary>
    public Vector4 BlendColor(Vector4 src, Vector4 dst)
    {
        var s = src * Factor(SrcFactor, src, dst, Constant);
        var d = dst * Factor(DstFactor, src, dst, Constant);
        var r = Equation switch
        {
            0x800A => s - d,
            0x800B => d - s,
            0x8007 => Vector4.Min(src, dst),
            0x8008 => Vector4.Max(src, dst),
            _ => s + d, // FUNC_ADD (the signed variants 0xF005/0xF006 are reported by the renderer)
        };
        return Vector4.Clamp(r, Vector4.Zero, Vector4.One);
    }

    /// <summary>GL logic operations (CLEAR 0x1500 ... SET 0x150F) on the packed render-target value.</summary>
    public static uint Logic(uint op, uint s, uint d) => op switch
    {
        0x1500 => 0,
        0x1501 => s & d,
        0x1502 => s & ~d,
        0x1503 => s,
        0x1504 => ~s & d,
        0x1505 => d,
        0x1506 => s ^ d,
        0x1507 => s | d,
        0x1508 => ~(s | d),
        0x1509 => ~(s ^ d),
        0x150A => ~d,
        0x150B => s | ~d,
        0x150C => ~s,
        0x150D => ~s | d,
        0x150E => ~(s & d),
        0x150F => 0xFFFF_FFFF,
        _ => s,
    };
}
