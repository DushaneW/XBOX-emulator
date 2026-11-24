using System.Numerics;

namespace XboxPc.Emulation.Gpu.Nv2a;

/// <summary>
/// One texture unit as configured by the Kelvin texture methods (stage stride 0x40 from 0x1B00): OFFSET, FORMAT,
/// ADDRESS, CONTROL0, CONTROL1, FILTER, IMAGE_RECT, BORDER_COLOR. Decoded once per draw.
/// </summary>
public sealed record TextureUnit
{
    public int Stage { get; init; }
    public bool Enabled { get; init; }
    public uint Address { get; init; }
    public uint Format { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Pitch { get; init; }
    public bool Swizzled { get; init; }
    public bool Compressed { get; init; }

    /// <summary>Linear ("rect") textures are addressed in texels, swizzled and compressed ones in [0,1].</summary>
    public bool Normalized { get; init; }

    public int AddressU { get; init; }
    public int AddressV { get; init; }
    public int MinFilter { get; init; }
    public int MagFilter { get; init; }
    public Vector4 Border { get; init; }
    public bool AlphaKill { get; init; }
}

/// <summary>
/// Texture fetch and filtering for the CPU reference renderer. Formats: swizzled and linear Y8, AY8, A8, A8Y8,
/// A1R5G5B5, X1R5G5B5, A4R4G4B4, R5G6B5, A8R8G8B8, X8R8G8B8, A8B8G8R8, B8G8R8A8, R8G8B8A8, and DXT1/DXT3/DXT5.
/// Swizzled layout: texel address bits interleave x and y, starting with x, while both dimensions have bits left
/// (as the hardware and xemu's swizzle.c define it). Addressing: wrap, mirror, clamp to edge, border, clamp.
/// Filtering: nearest and bilinear on mip level 0. Not implemented (reported): mipmap selection, 3D and cube
/// textures, palettised (I8) textures, YUV and depth formats, anisotropy, colour keys, signed texture channels.
/// </summary>
public static class Textures
{
    /// <summary>Bytes per texel, 0 for block-compressed or unsupported formats.</summary>
    public static int BytesPerTexel(uint format) => format switch
    {
        0x00 or 0x01 or 0x13 or 0x19 or 0x1B or 0x1F => 1,
        0x02 or 0x03 or 0x04 or 0x05 or 0x10 or 0x11 or 0x1A or 0x1C or 0x1D or 0x20 => 2,
        0x06 or 0x07 or 0x12 or 0x1E or 0x3A or 0x3B or 0x3C or 0x3F or 0x40 or 0x41 => 4,
        _ => 0,
    };

    public static bool IsSwizzled(uint format) => format is <= 0x07 or 0x19 or 0x1A or 0x3A or 0x3B or 0x3C;

    public static bool IsCompressed(uint format) => format is 0x0C or 0x0E or 0x0F;

    public static bool IsSupported(uint format) => BytesPerTexel(format) > 0 || IsCompressed(format);

    /// <summary>Offset of texel (x, y) in a swizzled texture of the given power-of-two size.</summary>
    public static uint SwizzleOffset(int x, int y, int width, int height)
    {
        uint offset = 0, bit = 1, maskBit = 1;
        var done = false;
        while (!done)
        {
            done = true;
            if (bit < (uint)width) { if ((x & bit) != 0) offset |= maskBit; maskBit <<= 1; done = false; }
            if (bit < (uint)height) { if ((y & bit) != 0) offset |= maskBit; maskBit <<= 1; done = false; }
            bit <<= 1;
        }
        return offset;
    }

    private static float C(uint v, int bits) => v / (float)((1 << bits) - 1);

    /// <summary>Decodes one uncompressed texel value to RGBA in [0,1].</summary>
    public static Vector4 Decode(uint format, uint t) => format switch
    {
        0x00 or 0x13 => new Vector4(C(t & 0xFF, 8), C(t & 0xFF, 8), C(t & 0xFF, 8), 1),                     // Y8
        0x01 or 0x1B => new Vector4(C(t & 0xFF, 8)),                                                           // AY8
        0x19 or 0x1F => new Vector4(0, 0, 0, C(t & 0xFF, 8)),                                                  // A8
        0x1A or 0x20 => new Vector4(C(t & 0xFF, 8), C(t & 0xFF, 8), C(t & 0xFF, 8), C((t >> 8) & 0xFF, 8)),    // A8Y8
        0x02 or 0x10 => new Vector4(C((t >> 10) & 31, 5), C((t >> 5) & 31, 5), C(t & 31, 5), (t >> 15) & 1),   // A1R5G5B5
        0x03 or 0x1C => new Vector4(C((t >> 10) & 31, 5), C((t >> 5) & 31, 5), C(t & 31, 5), 1),              // X1R5G5B5
        0x04 or 0x1D => new Vector4(C((t >> 8) & 15, 4), C((t >> 4) & 15, 4), C(t & 15, 4), C((t >> 12) & 15, 4)), // A4R4G4B4
        0x05 or 0x11 => new Vector4(C((t >> 11) & 31, 5), C((t >> 5) & 63, 6), C(t & 31, 5), 1),               // R5G6B5
        0x06 or 0x12 => new Vector4(C((t >> 16) & 0xFF, 8), C((t >> 8) & 0xFF, 8), C(t & 0xFF, 8), C(t >> 24, 8)), // A8R8G8B8
        0x07 or 0x1E => new Vector4(C((t >> 16) & 0xFF, 8), C((t >> 8) & 0xFF, 8), C(t & 0xFF, 8), 1),         // X8R8G8B8
        0x3A or 0x3F => new Vector4(C(t & 0xFF, 8), C((t >> 8) & 0xFF, 8), C((t >> 16) & 0xFF, 8), C(t >> 24, 8)), // A8B8G8R8
        0x3B or 0x40 => new Vector4(C((t >> 8) & 0xFF, 8), C((t >> 16) & 0xFF, 8), C(t >> 24, 8), C(t & 0xFF, 8)), // B8G8R8A8
        0x3C or 0x41 => new Vector4(C(t >> 24, 8), C((t >> 16) & 0xFF, 8), C((t >> 8) & 0xFF, 8), C(t & 0xFF, 8)), // R8G8B8A8
        _ => new Vector4(0, 0, 0, 1),
    };

    /// <summary>Reads texel (x, y) (already wrapped into range) from guest memory.</summary>
    public static Vector4 Texel(Core.PhysicalBus memory, TextureUnit t, int x, int y)
    {
        if (t.Compressed) return DxtTexel(memory, t, x, y);
        var bpp = BytesPerTexel(t.Format);
        var offset = t.Swizzled ? SwizzleOffset(x, y, t.Width, t.Height) * (uint)bpp : (uint)((y * t.Pitch) + (x * bpp));
        var a = t.Address + offset;
        uint v = bpp switch { 1 => memory.Read8(a), 2 => memory.Read16(a), _ => memory.Read32(a) };
        return Decode(t.Format, v);
    }

    private static Vector4 Rgb565(uint c) => new(C((c >> 11) & 31, 5), C((c >> 5) & 63, 6), C(c & 31, 5), 1);

    private static Vector4 DxtTexel(Core.PhysicalBus memory, TextureUnit t, int x, int y)
    {
        var blockBytes = t.Format == 0x0C ? 8u : 16u;
        var blocksPerRow = (uint)Math.Max(1, (t.Width + 3) / 4);
        var block = t.Address + ((((uint)y / 4 * blocksPerRow) + ((uint)x / 4)) * blockBytes);
        var colorBlock = t.Format == 0x0C ? block : block + 8;
        uint c0 = memory.Read16(colorBlock), c1 = memory.Read16(colorBlock + 2);
        var indices = memory.Read32(colorBlock + 4);
        var i = (indices >> (2 * (((y & 3) * 4) + (x & 3)))) & 3;
        Vector4 col0 = Rgb565(c0), col1 = Rgb565(c1);
        Vector4 color;
        if (t.Format != 0x0C || c0 > c1)
            color = i switch { 0 => col0, 1 => col1, 2 => ((2 * col0) + col1) / 3, _ => (col0 + (2 * col1)) / 3 };
        else
            color = i switch { 0 => col0, 1 => col1, 2 => (col0 + col1) / 2, _ => new Vector4(0, 0, 0, 0) }; // 1-bit alpha: transparent black
        color.W = t.Format == 0x0C ? color.W : 1;
        var texelIndex = ((y & 3) * 4) + (x & 3);
        if (t.Format == 0x0E) // DXT3: explicit 4-bit alpha
        {
            var alphaBits = (ulong)memory.Read32(block) | ((ulong)memory.Read32(block + 4) << 32);
            color.W = ((alphaBits >> (4 * texelIndex)) & 0xF) / 15f;
        }
        else if (t.Format == 0x0F) // DXT5: interpolated alpha
        {
            uint a0 = memory.Read8(block), a1 = memory.Read8(block + 1);
            ulong bits = 0;
            for (var b = 0; b < 6; b++) bits |= (ulong)memory.Read8(block + 2 + (uint)b) << (8 * b);
            var code = (int)((bits >> (3 * texelIndex)) & 7);
            float alpha;
            if (code == 0) alpha = a0;
            else if (code == 1) alpha = a1;
            else if (a0 > a1) alpha = (((8 - code) * a0) + ((code - 1) * a1)) / 7f;
            else alpha = code == 6 ? 0 : code == 7 ? 255 : (((6 - code) * a0) + ((code - 1) * a1)) / 5f;
            color.W = alpha / 255f;
        }
        return color;
    }

    /// <summary>Applies an addressing mode to an integer texel coordinate; returns -1 for border.</summary>
    public static int Wrap(int i, int size, int mode)
    {
        switch (mode)
        {
            case 1: return ((i % size) + size) % size;                                 // wrap
            case 2:                                                                    // mirror
                {
                    var period = 2 * size;
                    var m = ((i % period) + period) % period;
                    return m < size ? m : period - 1 - m;
                }
            case 4: return i < 0 || i >= size ? -1 : i;                                // border
            default: return Math.Clamp(i, 0, size - 1);                                // clamp to edge / clamp
        }
    }

    /// <summary>Samples at texture coordinates (s, t). Nearest or bilinear on level 0.</summary>
    public static Vector4 Sample(Core.PhysicalBus memory, TextureUnit t, float s, float tc)
    {
        float u = t.Normalized ? s * t.Width : s;
        float v = t.Normalized ? tc * t.Height : tc;
        // magnification vs minification is not distinguished without derivatives: the MAG filter is used
        var linear = t.MagFilter == 2;
        if (!linear)
        {
            int x = Wrap((int)MathF.Floor(u), t.Width, t.AddressU), y = Wrap((int)MathF.Floor(v), t.Height, t.AddressV);
            return x < 0 || y < 0 ? t.Border : Texel(memory, t, x, y);
        }
        u -= 0.5f;
        v -= 0.5f;
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        float fx = u - x0, fy = v - y0;
        Vector4 Fetch(int x, int y)
        {
            int wx = Wrap(x, t.Width, t.AddressU), wy = Wrap(y, t.Height, t.AddressV);
            return wx < 0 || wy < 0 ? t.Border : Texel(memory, t, wx, wy);
        }
        var top = Vector4.Lerp(Fetch(x0, y0), Fetch(x0 + 1, y0), fx);
        var bottom = Vector4.Lerp(Fetch(x0, y0 + 1), Fetch(x0 + 1, y0 + 1), fx);
        return Vector4.Lerp(top, bottom, fy);
    }
}
