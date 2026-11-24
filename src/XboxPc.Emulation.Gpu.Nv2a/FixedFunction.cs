using System.Numerics;

namespace XboxPc.Emulation.Gpu.Nv2a;

/// <summary>
/// Kelvin fixed-function transform and lighting (SET_TRANSFORM_EXECUTION_MODE = FIXED).
/// Semantics follow xemu's fixed-function shader generator (hw/xbox/nv2a/pgraph/glsl/vsh-ff.c, read 2026-11-23); they
/// are what the best public emulator does, not verified on hardware here. Method numbers are from nxdk's headers.
/// Attribute slots: v0 position, v1 weights, v2 normal, v3 diffuse, v4 specular, v5 fog, v7/v8 back diffuse/specular,
/// v9-v12 texture coordinates. Matrices: 16 words; out[j] = dot(v, words[4j..4j+3]) (xemu uploads the words as GLSL
/// columns and computes v * M). Order: skinning -> normal -> texgen -> texture matrix -> lighting -> specular merge ->
/// fog distance -> window position (÷ w, + viewport offset, snapped to 1/16 pixel).
/// </summary>
public static class FixedFunction
{
    /// <summary>xemu's power-on defaults for the specular curve parameters (L, M, N).</summary>
    public static readonly float[] DefaultSpecularParams = [0f, -0.494592f, 1.494592f];

    public static Vector4 Transform(Vector4 v, ReadOnlySpan<float> m) => new(
        Vector4.Dot(v, new Vector4(m[0], m[1], m[2], m[3])),
        Vector4.Dot(v, new Vector4(m[4], m[5], m[6], m[7])),
        Vector4.Dot(v, new Vector4(m[8], m[9], m[10], m[11])),
        Vector4.Dot(v, new Vector4(m[12], m[13], m[14], m[15])));

    /// <summary>Rational falloff used for both specular power and spot cones: (x + L) / (M·x + N), clamped.</summary>
    public static float Curve(float x, Vector3 lmn, float max)
    {
        if (x <= -lmn.X) return 0;
        var denominator = (lmn.Y * x) + lmn.Z;
        if (denominator == 0) return max;
        return denominator > 0 ? Math.Clamp((x + lmn.X) / denominator, 0, max) : 0;
    }

    public static float SpotFactor(float s, Vector3 falloff) => s <= 0 ? 0 : s >= 1 ? 1 : Curve(s, falloff, 1);

    /// <summary>Fog factor from a fog distance, per SET_FOG_MODE, with FOG_PARAMS words 0 and 1.</summary>
    public static float FogFactor(float d, uint mode, float p0, float p1)
    {
        var f = mode switch
        {
            0x2601 or 0x804 => p0 + (d * p1) - 1,
            0x800 or 0x802 => p0 + MathF.Pow(2, d * p1 * 16) - 1.5f,
            0x801 or 0x803 => p0 + MathF.Pow(2, -d * d * p1 * p1 * 32) - 1.5f,
            _ => 1,
        };
        return mode is 0x802 or 0x803 or 0x804 ? MathF.Abs(f) : f;
    }

    private static Vector3 Xyz(Vector4 v) => new(v.X, v.Y, v.Z);

    /// <summary>Runs the fixed-function pipeline for one vertex. <paramref name="state"/> reads Kelvin method values.</summary>
    public static Vector4[] Execute(Vector4[] v, Func<uint, uint> state, Action<string> report)
    {
        float F(uint method) => BitConverter.UInt32BitsToSingle(state(method));
        float[] Matrix(uint method)
        {
            var m = new float[16];
            for (var i = 0; i < 16; i++) m[i] = F(method + (uint)(i * 4));
            return m;
        }
        Vector3 Rgb(uint method) => new(F(method), F(method + 4), F(method + 8));
        Vector4 Vec4(uint method) => new(F(method), F(method + 4), F(method + 8), F(method + 12));

        var o = new Vector4[VertexProgram.Outputs];
        var position = v[0];
        var lighting = state(0x314) != 0;

        // ---- skinning: eye-space position and normal
        var skin = state(0x328);
        var n0 = new Vector4(Xyz(v[2]), 0);
        Vector4 eye;
        Vector3 normal;
        if (skin is 0 or > 6)
        {
            if (skin > 6) report($"NV2A skin mode {skin} is not defined - skinning off");
            eye = Transform(position, Matrix(0x480));
            normal = Xyz(Transform(n0, Matrix(0x580)));
        }
        else
        {
            var wt = v[1];
            float[] weights = skin switch
            {
                1 => [wt.X, 1 - wt.X],
                2 => [wt.X, wt.Y],
                3 => [wt.X, wt.Y, 1 - wt.X - wt.Y],
                4 => [wt.X, wt.Y, wt.Z],
                5 => [wt.X, wt.Y, wt.Z, 1 - wt.X - wt.Y - wt.Z],
                _ => [wt.X, wt.Y, wt.Z, wt.W],
            };
            eye = Vector4.Zero;
            normal = Vector3.Zero;
            for (var i = 0; i < weights.Length; i++)
            {
                eye += Transform(position, Matrix(0x480 + (uint)(i * 0x40))) * weights[i];
                normal += Xyz(Transform(n0, Matrix(0x580 + (uint)(i * 0x40)))) * weights[i];
            }
        }
        if (state(0x3A4) != 0 && normal != Vector3.Zero) normal = Vector3.Normalize(normal);
        var eyePos = Xyz(eye) / (eye.W == 0 ? 1 : eye.W);

        // ---- texture coordinates: generation, then the texture matrix
        var infiniteViewer = state(0x9CC) == 1;
        var u = infiniteViewer ? new Vector3(0, 0, 1) : eyePos == Vector3.Zero ? Vector3.Zero : Vector3.Normalize(eyePos);
        var reflection = u - (2 * Vector3.Dot(normal, u) * normal);
        var sphereScale = 1 / (2 * (reflection + new Vector3(0, 0, 1)).Length());
        for (var t = 0; t < 4; t++)
        {
            var tc = v[9 + t];
            for (var c = 0; c < 4; c++)
            {
                var mode = state(0x3C0 + (uint)(t * 0x10) + (uint)(c * 4));
                var plane = Vec4(0x840 + (uint)(t * 0x40) + (uint)(c * 0x10));
                float? value = mode switch
                {
                    0 => null,
                    0x2400 => Vector4.Dot(plane, eye),                                             // eye linear
                    0x2401 => Vector4.Dot(plane, position),                                        // object linear
                    0x2402 when c < 2 => (reflection[c] * sphereScale) + 0.5f,                     // sphere map (s, t)
                    0x8512 when c < 3 => reflection[c],                                            // reflection map
                    0x8511 when c < 3 => normal[c],                                                // normal map
                    _ => Invalid(mode, c),
                };
                if (value is { } g) tc[c] = g;
            }
            if (state(0x420 + (uint)(t * 4)) != 0) tc = Transform(tc, Matrix(0x6C0 + (uint)(t * 0x40)));
            o[VertexProgram.OT0 + t] = tc;
        }

        float? Invalid(uint mode, int c)
        {
            report($"NV2A texgen mode 0x{mode:X} is not valid for component {"STRQ"[c]} - vertex value used");
            return null;
        }

        // ---- lighting
        var diffuse = v[3];
        var specularIn = v[4];
        var specularEnable = state(0x3B8) != 0;
        var lightControl = state(0x294);
        var separateSpecular = (lightControl & 1) != 0;
        var localEye = (lightControl & 0x1_0000) != 0;
        var ignoreSpecularAlpha = (lightControl & 0x2_0000) == 0;
        if (state(0x17C4) != 0) report("NV2A two-sided lighting: back-side lighting is not computed (back colours are the vertex back colours; xemu does the same)");

        if (lighting)
        {
            var cm = state(0x298);
            int emissionSrc = (int)(cm & 3), ambientSrc = (int)((cm >> 2) & 3), diffuseSrc = (int)((cm >> 4) & 3), specularSrc = (int)((cm >> 6) & 3);
            Vector3 Src(int src) => src == 1 ? Xyz(diffuse) : Xyz(specularIn);
            var alpha = diffuseSrc switch { 0 => F(0x3B4), 2 => specularIn.W, _ => diffuse.W };
            var materialEmission = Rgb(0x3A8);
            Vector3 d0;
            if (emissionSrc == 0 || ambientSrc == 0)
            {
                d0 = Rgb(0xA10); // scene ambient: the API's ambientxmaterial + emission product
                if (emissionSrc != 0) d0 += Src(emissionSrc) * materialEmission;
                else if (ambientSrc != 0) d0 += Src(ambientSrc) * materialEmission;
            }
            else d0 = Src(emissionSrc) + (Src(ambientSrc) * materialEmission);

            var d1 = Vector3.Zero;
            var lmn = new Vector3(F(0x9E0), F(0x9E4), F(0x9E8));
            var vpEye = Vector3.Zero;
            if (localEye)
            {
                var e = Vec4(0xA50);
                var dv = (Xyz(e) / (e.W == 0 ? 1 : e.W)) - eyePos;
                vpEye = dv == Vector3.Zero ? Vector3.Zero : Vector3.Normalize(dv);
            }
            var mask = state(0x3BC);
            for (var i = 0; i < 8; i++)
            {
                var mode = (mask >> (2 * i)) & 3;
                if (mode == 0) continue;
                var b = 0x1000u + ((uint)i * 0x80);
                Vector3 toLight, half;
                var attenuation = 1f;
                if (mode == 1)
                {
                    var dir = Rgb(b + 0x34);
                    toLight = dir == Vector3.Zero ? dir : Vector3.Normalize(dir);
                    half = localEye ? SafeNormalize(toLight + vpEye) : Rgb(b + 0x28);
                }
                else
                {
                    var dv = Rgb(b + 0x5C) - eyePos;
                    var distance = dv.Length();
                    toLight = distance == 0 ? Vector3.Zero : dv / distance;
                    var att = Rgb(b + 0x68);
                    var denominator = att.X + (att.Y * distance) + (att.Z * distance * distance);
                    attenuation = distance > F(b + 0x24) ? 0 : denominator == 0 ? 1 : 1 / denominator;
                    if (mode == 3)
                    {
                        var spot = Vec4(b + 0x4C);
                        attenuation *= SpotFactor(Vector3.Dot(Xyz(spot), toLight) + spot.W, Rgb(b + 0x40));
                    }
                    half = SafeNormalize(toLight + vpEye);
                }
                var nDotL = MathF.Max(0, Vector3.Dot(normal, toLight));
                var lightAmbient = Rgb(b) * attenuation;
                var lightDiffuse = Rgb(b + 0x0C) * attenuation * nDotL;
                d0 += ambientSrc == 0 ? lightAmbient : Src(ambientSrc) * lightAmbient;
                d0 += diffuseSrc == 0 ? lightDiffuse : Src(diffuseSrc) * lightDiffuse;
                if (specularEnable)
                {
                    var nDotH = Vector3.Dot(normal, half);
                    var pf = nDotL <= 0 ? 0 : Curve(nDotH, lmn, 10000);
                    var lightSpecular = Rgb(b + 0x18) * attenuation * pf;
                    d1 += specularSrc == 0 ? lightSpecular : Src(specularSrc) * lightSpecular;
                }
            }
            o[VertexProgram.OD0] = new Vector4(d0, alpha);
            o[VertexProgram.OD1] = new Vector4(d1, specularIn.W);
        }
        else
        {
            o[VertexProgram.OD0] = diffuse;
            o[VertexProgram.OD1] = specularIn;
        }
        o[VertexProgram.OB0] = v[7];
        o[VertexProgram.OB1] = v[8];

        // ---- specular merge
        if (!specularEnable)
        {
            o[VertexProgram.OD1] = new Vector4(0, 0, 0, 1);
            o[VertexProgram.OB1] = new Vector4(0, 0, 0, 1);
        }
        else if (!separateSpecular)
        {
            if (lighting)
            {
                o[VertexProgram.OD0] += new Vector4(Xyz(o[VertexProgram.OD1]), 0);
                o[VertexProgram.OB0] += new Vector4(Xyz(o[VertexProgram.OB1]), 0);
            }
            o[VertexProgram.OD1] = specularIn;
            o[VertexProgram.OB1] = v[8];
        }
        if (ignoreSpecularAlpha)
        {
            o[VertexProgram.OD1].W = 1;
            o[VertexProgram.OB1].W = 1;
        }

        // ---- fog distance (the fog factor is applied by the renderer for both pipelines)
        var fogDistance = state(0x2A0) switch
        {
            0 => Math.Clamp(specularIn.W, 0, 1),                                          // specular alpha
            1 => Xyz(eye).Length(),                                                       // radial
            2 => Vector3.Dot(Xyz(Vec4(0x9D0)), Xyz(eye)) + F(0x9DC),                      // planar
            3 => MathF.Abs(Vector3.Dot(Xyz(Vec4(0x9D0)), Xyz(eye)) + F(0x9DC)),           // absolute planar
            _ => v[5].X,                                                                  // fog coordinate
        };
        o[VertexProgram.OFog] = new Vector4(fogDistance, 0, 0, 0);

        // ---- window position
        var p = Transform(skin is 0 or > 6 ? position : eye, Matrix(0x680));
        var w = ClampAwayFromZero(p.W);
        var x = (p.X / w) + F(0xA20);
        var y = (p.Y / w) + F(0xA24);
        o[VertexProgram.OPos] = new Vector4(MathF.Truncate(x * 16) / 16, MathF.Truncate(y * 16) / 16, p.Z / w, w);
        return o;
    }

    private static Vector3 SafeNormalize(Vector3 v) => v == Vector3.Zero ? v : Vector3.Normalize(v);

    private static float ClampAwayFromZero(float w)
    {
        var a = Math.Clamp(MathF.Abs(w), 5.42101e-20f, 1.884467e19f);
        return w < 0 ? -a : a;
    }
}
