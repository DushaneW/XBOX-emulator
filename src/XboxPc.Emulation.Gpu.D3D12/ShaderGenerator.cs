using System.Globalization;
using System.Text;
using XboxPc.Emulation.Gpu.Nv2a;

namespace XboxPc.Emulation.Gpu.D3D12;

/// <summary>
/// What a pixel shader depends on, apart from per-draw constants. Two draws with equal keys share a shader.
/// </summary>
public sealed record PixelShaderKey(string Program)
{
    public static PixelShaderKey From(GpuDraw d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var sb = new StringBuilder();
        var w = d.CombinerWords;
        // stage and final words; the colour constants (factor0/1, words 32-47) are shader constants, not code
        for (var i = 0; i < 32; i++) sb.Append(w[i].ToString("X8", CultureInfo.InvariantCulture));
        sb.Append(':').Append(w[48].ToString("X8", CultureInfo.InvariantCulture)).Append(w[49].ToString("X8", CultureInfo.InvariantCulture)).Append(w[50].ToString("X8", CultureInfo.InvariantCulture));
        for (var t = 0; t < 4; t++)
        {
            var unit = d.Textures[t];
            sb.Append(':').Append(d.StageModes[t]).Append(unit is null ? "-" : $"{(unit.Normalized ? 'n' : 'r')}{(unit.AlphaKill ? 'k' : '_')}");
        }
        sb.Append(":a").Append(d.Ops.AlphaTest ? d.Ops.AlphaFunc.ToString("X", CultureInfo.InvariantCulture) : "off");
        if (d.Ops.LogicOp) sb.Append(":logic");
        else if (d.Ops.Blend) sb.Append(":blend");
        return new PixelShaderKey(sb.ToString());
    }
}

/// <summary>
/// Generates HLSL for the NV2A pipeline. The pixel shader is a line-by-line translation of
/// <see cref="Combiners.Evaluate"/> (register combiners: up to 8 general stages and the final combiner), the
/// texture stage modes (2D texture, pass-through, alpha kill) and the alpha test of <c>KelvinRenderer.Fragment</c>,
/// so the GPU computes the same values as the CPU reference renderer.
/// </summary>
public static class ShaderGenerator
{
    /// <summary>Constant buffer layout shared by both shaders (register b0); must match <see cref="D3D12Backend"/>.</summary>
    public const string ConstantBuffer = """
        cbuffer Draw : register(b0)
        {
            float4 Scale;        // xy: anti-aliasing samples per pixel; z: 1/zmax; w: host pixels per sample (resolution scale)
            float4 TargetSize;   // xy: render-target size in host pixels
            float4 StageC0[8];
            float4 StageC1[8];
            float4 FinalC0;
            float4 FinalC1;
            float4 FogColor;     // rgb
            float4 AlphaRef;     // x: reference 0-255
            float4 TexSize[4];   // xy: texel size of rectangle textures
            float4 TexUv[4];     // xy: coordinate scale (texture inside a larger render target); z: 1 = alpha reads 1
        };
        """;

    public const string VertexShader = ConstantBuffer + """

        struct VSIn
        {
            float4 Pos : POSITION; float4 D : COLOR0; float4 S : COLOR1; float4 F : TEXCOORD4;
            float4 T0 : TEXCOORD0; float4 T1 : TEXCOORD1; float4 T2 : TEXCOORD2; float4 T3 : TEXCOORD3;
        };
        struct PSIn
        {
            float4 Pos : SV_Position; float4 D : COLOR0; float4 S : COLOR1; float4 F : TEXCOORD4;
            float4 T0 : TEXCOORD0; float4 T1 : TEXCOORD1; float4 T2 : TEXCOORD2; float4 T3 : TEXCOORD3;
        };
        PSIn main(VSIn i)
        {
            PSIn o;
            // Guest window coordinates put sample centres on integers of the (anti-aliased) sample grid, as the CPU
            // renderer does (Direct3D 8 convention); host pixel centres are at +0.5.
            float2 host = (i.Pos.xy * Scale.xy + 0.5) * Scale.w;
            float2 ndc = float2(host.x / TargetSize.x * 2 - 1, 1 - host.y / TargetSize.y * 2);
            float w = i.Pos.w;
            o.Pos = float4(ndc * w, saturate(i.Pos.z * Scale.z) * w, w);
            o.D = i.D; o.S = i.S; o.F = i.F; o.T0 = i.T0; o.T1 = i.T1; o.T2 = i.T2; o.T3 = i.T3;
            return o;
        }
        """;

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Input mapping (NV_register_combiners) applied to an expression.</summary>
    private static string Map(string x, int map) => map switch
    {
        0 => $"max(0, {x})",
        1 => $"(1 - saturate({x}))",
        2 => $"(2 * max(0, {x}) - 1)",
        3 => $"(-2 * max(0, {x}) + 1)",
        4 => $"(max(0, {x}) - 0.5)",
        5 => $"(-max(0, {x}) + 0.5)",
        6 => x,
        _ => $"(-{x})",
    };

    /// <summary>Output scale/bias followed by the [-1, 1] clamp.</summary>
    private static string Scale(string x, int op) => op switch
    {
        1 => $"clamp({x} - 0.5, -1, 1)",
        2 => $"clamp({x} * 2, -1, 1)",
        3 => $"clamp(({x} - 0.5) * 2, -1, 1)",
        4 => $"clamp({x} * 4, -1, 1)",
        6 => $"clamp({x} / 2, -1, 1)",
        _ => $"clamp({x}, -1, 1)",
    };

    private static string Rgb(uint w, int shift)
    {
        var reg = (int)((w >> shift) & 0xF);
        var alpha = ((w >> (shift + 4)) & 1) != 0;
        var map = (int)((w >> (shift + 5)) & 7);
        return Map(alpha ? $"R[{reg}].www" : $"R[{reg}].xyz", map);
    }

    private static string Alpha(uint w, int shift)
    {
        var reg = (int)((w >> shift) & 0xF);
        var alpha = ((w >> (shift + 4)) & 1) != 0;
        var map = (int)((w >> (shift + 5)) & 7);
        return Map(alpha ? $"R[{reg}].w" : $"R[{reg}].z", map);
    }

    private static string Final(uint w, int shift)
    {
        var reg = (int)((w >> shift) & 0xF);
        var alpha = ((w >> (shift + 4)) & 1) != 0;
        var inverse = ((w >> (shift + 5)) & 1) != 0;
        var v = $"saturate({(alpha ? $"R[{reg}].www" : $"R[{reg}].xyz")})";
        return inverse ? $"(1 - {v})" : v;
    }

    /// <summary>Generates the pixel shader for one draw's combiner program, texture stages and alpha test.</summary>
    public static string PixelShader(GpuDraw d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var w = d.CombinerWords;
        uint control = w[48], final0 = w[49], final1 = w[50];
        var stages = (int)Math.Clamp(control & 0xFF, 0, 8);
        var muxMsb = ((control >> 8) & 0xF) != 0;
        var sb = new StringBuilder();
        sb.AppendLine(ConstantBuffer);
        sb.AppendLine("Texture2D Tex0 : register(t0); Texture2D Tex1 : register(t1); Texture2D Tex2 : register(t2); Texture2D Tex3 : register(t3);");
        sb.AppendLine("SamplerState Smp0 : register(s0); SamplerState Smp1 : register(s1); SamplerState Smp2 : register(s2); SamplerState Smp3 : register(s3);");
        // The CPU renderer stores round-half-to-even of v*255; host GPUs round ties differently. Without blending
        // the shader rounds itself, so the stored value is exact (blending rounds in fixed-function hardware).
        sb.AppendLine("float4 Rne(float4 v) { float4 x = saturate(v) * 255; float4 r = floor(x + 0.5); r -= (r - x == 0.5) * fmod(r, 2); return r; }");
        sb.AppendLine("struct PSIn { float4 Pos : SV_Position; float4 D : COLOR0; float4 S : COLOR1; float4 F : TEXCOORD4; float4 T0 : TEXCOORD0; float4 T1 : TEXCOORD1; float4 T2 : TEXCOORD2; float4 T3 : TEXCOORD3; };");
        // Logic operations work on integer render-target views: the shader then outputs the stored 8-bit values.
        sb.AppendLine(d.Ops.LogicOp ? "uint4 main(PSIn i) : SV_Target" : "float4 main(PSIn i) : SV_Target");
        sb.AppendLine("{");
        sb.AppendLine("    float4 R[16];");
        sb.AppendLine("    [unroll] for (int k = 0; k < 16; k++) R[k] = 0;");
        sb.AppendLine("    R[3] = float4(FogColor.rgb, saturate(i.F.x));");
        sb.AppendLine("    R[4] = saturate(i.D);");
        sb.AppendLine("    R[5] = saturate(i.S);");

        // texture stages
        string[] coords = ["i.T0", "i.T1", "i.T2", "i.T3"];
        var kills = new List<int>();
        for (var t = 0; t < 4; t++)
        {
            switch (d.StageModes[t])
            {
                case 4:
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    R[{8 + t}] = saturate({coords[t]});");
                    break;
                case 1 when d.Textures[t] is { } unit:
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {{ float q = {coords[t]}.w == 0 ? 1 : {coords[t]}.w; float2 uv = {coords[t]}.xy / q;");
                    if (!unit.Normalized) sb.AppendLine(CultureInfo.InvariantCulture, $"      uv /= TexSize[{t}].xy;");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      R[{8 + t}] = Tex{t}.SampleLevel(Smp{t}, uv * TexUv[{t}].xy, 0); R[{8 + t}].w = lerp(R[{8 + t}].w, 1, TexUv[{t}].z); }}");
                    if (unit.AlphaKill) kills.Add(t);
                    break;
            }
        }
        foreach (var t in kills) sb.AppendLine(CultureInfo.InvariantCulture, $"    if (R[{8 + t}].w == 0) discard;");
        sb.AppendLine("    R[12] = float4(0, 0, 0, R[8].w); // spare0.alpha starts as t0.alpha");

        for (var s = 0; s < stages; s++)
        {
            uint ci = w[s], co = w[8 + s], ai = w[16 + s], ao = w[24 + s];
            bool abDot = ((co >> 13) & 1) != 0, cdDot = ((co >> 12) & 1) != 0, mux = ((co >> 14) & 1) != 0;
            var op = (int)((co >> 15) & 7);
            int abDst = (int)((co >> 4) & 0xF), cdDst = (int)(co & 0xF), sumDst = (int)((co >> 8) & 0xF);
            bool abBlue = ((co >> 19) & 1) != 0, cdBlue = ((co >> 18) & 1) != 0;
            var aMux = ((ao >> 14) & 1) != 0;
            var aOp = (int)((ao >> 15) & 7);
            int aAbDst = (int)((ao >> 4) & 0xF), aCdDst = (int)(ao & 0xF), aSumDst = (int)((ao >> 8) & 0xF);

            sb.AppendLine(CultureInfo.InvariantCulture, $"    // stage {s}");
            sb.AppendLine("    {");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        R[1] = StageC0[{s}]; R[2] = StageC1[{s}];");
            sb.AppendLine(muxMsb ? "        bool mux = R[12].w >= 0.5;" : "        bool mux = (((int)round(R[12].w * 255)) & 1) != 0;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        float3 a = {Rgb(ci, 24)}, b = {Rgb(ci, 16)}, c = {Rgb(ci, 8)}, d = {Rgb(ci, 0)};");
            sb.AppendLine(abDot ? "        float3 ab = dot(a, b).xxx;" : "        float3 ab = a * b;");
            sb.AppendLine(cdDot ? "        float3 cd = dot(c, d).xxx;" : "        float3 cd = c * d;");
            sb.AppendLine(mux ? "        float3 sum = mux ? cd : ab;" : "        float3 sum = ab + cd;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        ab = {Scale("ab", op)}; cd = {Scale("cd", op)}; sum = {Scale("sum", op)};");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        float aa = {Alpha(ai, 24)}, ab2 = {Alpha(ai, 16)}, ac = {Alpha(ai, 8)}, ad = {Alpha(ai, 0)};");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        float aAB = {Scale("aa * ab2", aOp)};");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        float aCD = {Scale("ac * ad", aOp)};");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        float aSum = {Scale(aMux ? "(mux ? ac * ad : aa * ab2)" : "(aa * ab2 + ac * ad)", aOp)};");
            // writes happen after all inputs of the stage were read, in the CPU renderer's order
            if (cdDst != 0) sb.AppendLine(CultureInfo.InvariantCulture, $"        R[{cdDst}] = float4(cd, {(cdBlue ? "cd.z" : $"R[{cdDst}].w")});");
            if (abDst != 0) sb.AppendLine(CultureInfo.InvariantCulture, $"        R[{abDst}] = float4(ab, {(abBlue ? "ab.z" : $"R[{abDst}].w")});");
            if (sumDst != 0) sb.AppendLine(CultureInfo.InvariantCulture, $"        R[{sumDst}] = float4(sum, R[{sumDst}].w);");
            if (aCdDst != 0) sb.AppendLine(CultureInfo.InvariantCulture, $"        R[{aCdDst}].w = aCD;");
            if (aAbDst != 0) sb.AppendLine(CultureInfo.InvariantCulture, $"        R[{aAbDst}].w = aAB;");
            if (aSumDst != 0) sb.AppendLine(CultureInfo.InvariantCulture, $"        R[{aSumDst}].w = aSum;");
            sb.AppendLine("    }");
        }

        // final combiner
        var specularClamp = ((final1 >> 7) & 1) != 0;
        var gReg = (int)((final1 >> 8) & 0xF);
        var gAlpha = ((final1 >> 12) & 1) != 0;
        var gInverse = ((final1 >> 13) & 1) != 0;
        sb.AppendLine("    R[1] = FinalC0; R[2] = FinalC1;");
        sb.AppendLine("    float3 v1r0 = R[5].xyz + R[12].xyz;");
        if (specularClamp) sb.AppendLine("    v1r0 = saturate(v1r0);");
        sb.AppendLine("    R[14] = float4(v1r0, 0);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    R[15] = float4({Final(final1, 24)} * {Final(final1, 16)}, 0);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    float3 fa = {Final(final0, 24)}, fb = {Final(final0, 16)}, fc = {Final(final0, 8)}, fd = {Final(final0, 0)};");
        sb.AppendLine("    float3 rgb = saturate(fa * fb + (1 - fa) * fc + fd);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    float g = saturate(R[{gReg}].{(gAlpha ? "w" : "z")});");
        if (gInverse) sb.AppendLine("    g = 1 - g;");

        if (d.Ops.AlphaTest)
        {
            var cmp = d.Ops.AlphaFunc switch
            {
                0x200 => "false",
                0x201 => "a8 < AlphaRef.x",
                0x202 => "a8 == AlphaRef.x",
                0x203 => "a8 <= AlphaRef.x",
                0x204 => "a8 > AlphaRef.x",
                0x205 => "a8 != AlphaRef.x",
                0x206 => "a8 >= AlphaRef.x",
                _ => "true",
            };
            sb.AppendLine("    float a8 = clamp(round(g * 255), 0, 255);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    if (!({cmp})) discard;");
        }
        sb.AppendLine(d.Ops.LogicOp ? "    return (uint4)Rne(float4(rgb, g));"
            : d.Ops.Blend ? "    return float4(rgb, g);"
            : "    return Rne(float4(rgb, g)) / 255;");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Constant-buffer contents for one draw (floats, padded to a multiple of 64 floats).</summary>
    public static float[] Constants(GpuDraw d, float scale, int hostWidth, int hostHeight, double zMax, float[]? texUv = null)
    {
        ArgumentNullException.ThrowIfNull(d);
        var c = new float[128];
        c[0] = d.AaX;
        c[1] = d.AaY;
        c[2] = (float)(1 / zMax);
        c[3] = scale;
        c[4] = hostWidth;
        c[5] = hostHeight;
        var w = d.CombinerWords;
        var eachFactor0 = ((w[48] >> 12) & 0xF) != 0;
        var eachFactor1 = ((w[48] >> 16) & 0xF) != 0;
        for (var s = 0; s < 8; s++)
        {
            Put(c, 8 + (s * 4), Combiners.Unpack(w[32 + (eachFactor0 ? s : 0)]));
            Put(c, 40 + (s * 4), Combiners.Unpack(w[40 + (eachFactor1 ? s : 0)]));
        }
        Put(c, 72, Combiners.Unpack(w[32]));
        Put(c, 76, Combiners.Unpack(w[40]));
        c[80] = d.FogColor.X;
        c[81] = d.FogColor.Y;
        c[82] = d.FogColor.Z;
        c[84] = d.Ops.AlphaRef;
        for (var t = 0; t < 4; t++)
        {
            if (d.Textures[t] is not { } unit) continue;
            c[88 + (t * 4)] = unit.Width;
            c[89 + (t * 4)] = unit.Height;
        }
        for (var t = 0; t < 4; t++)
        {
            c[104 + (t * 4)] = texUv?[t * 4] ?? 1;
            c[105 + (t * 4)] = texUv?[(t * 4) + 1] ?? 1;
            c[106 + (t * 4)] = texUv?[(t * 4) + 2] ?? 0;
        }
        return c;
    }

    private static void Put(float[] c, int i, System.Numerics.Vector4 v)
    {
        c[i] = v.X;
        c[i + 1] = v.Y;
        c[i + 2] = v.Z;
        c[i + 3] = v.W;
    }

    internal static string Describe(float v) => F(v);
}
