using System.Numerics;

namespace XboxPc.Emulation.Gpu.Nv2a;

/// <summary>A render target in guest memory as the Kelvin engine sees it.</summary>
public readonly record struct GpuSurface(uint Address, int Pitch, uint Format, int Bpp, bool Present, bool Swizzled, int SwizzleWidth, int SwizzleHeight);

/// <summary>A CLEAR_SURFACE: rectangle in render-target pixels (anti-aliasing samples included), inclusive bounds.</summary>
public sealed record GpuClear(GpuSurface Color, GpuSurface Zeta, int X0, int Y0, int X1, int Y1, uint Flags, uint ColorValue, uint ZetaValue, int Width, int Height, int AaX = 1, int AaY = 1);

/// <summary>A 2D-engine image copy (SRCCOPY) between two pitch surfaces; addresses are the surfaces' first bytes.</summary>
public sealed record GpuBlit(uint SrcAddress, int SrcPitch, uint DstAddress, int DstPitch, int InX, int InY, int OutX, int OutY, int Width, int Height, int Bpp, uint SurfaceFormat);

/// <summary>
/// One batch of triangles, ready to rasterise. The NV2A side has already run the vertex stage (fixed function or
/// vertex program), assembled the primitive type into triangles, culled them and picked the front or back colours;
/// points and lines arrive as small quads. Vertex layout (<see cref="FloatsPerVertex"/> floats): window position
/// (x, y in render-target pixels before anti-aliasing scale, z in depth-buffer units, w), diffuse, specular,
/// (fog factor, 0, 0, 0), then texture coordinates 0-3.
/// </summary>
public sealed class GpuDraw
{
    public const int FloatsPerVertex = 32;

    public required GpuSurface Color { get; init; }
    public required GpuSurface Zeta { get; init; }

    /// <summary>Render-target size in pixels (from the clip rectangle origin plus size; anti-aliasing not applied).</summary>
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Anti-aliasing supersampling factors of the target.</summary>
    public required int AaX { get; init; }
    public required int AaY { get; init; }

    /// <summary>Clip (scissor) rectangle in render-target pixels, exclusive upper bounds, anti-aliasing applied.</summary>
    public required int ClipX0 { get; init; }
    public required int ClipY0 { get; init; }
    public required int ClipX1 { get; init; }
    public required int ClipY1 { get; init; }

    public required float[] Vertices { get; init; }
    public required int VertexCount { get; init; }

    /// <summary>Register-combiner words: color ICW[8], color OCW[8], alpha ICW[8], alpha OCW[8], factor0[8], factor1[8], control, final CW0, final CW1.</summary>
    public required uint[] CombinerWords { get; init; }
    public required FragmentOps Ops { get; init; }
    public required bool DepthTest { get; init; }
    public required bool DepthWrite { get; init; }
    public required uint DepthFunc { get; init; }
    public required bool StencilActive { get; init; }
    public required uint ColorMask { get; init; }
    public required Vector3 FogColor { get; init; }

    /// <summary>Texture shader mode per stage: 0 off, 1 2D texture, 4 pass-through.</summary>
    public required int[] StageModes { get; init; }
    public required TextureUnit?[] Textures { get; init; }
}

/// <summary>
/// A host-GPU renderer that receives the Kelvin engine's work. Called on the emulation thread. The CPU reference
/// renderer remains authoritative for guest memory; a backend draws the same work (typically at a higher
/// resolution) and may supply the displayed picture through <see cref="TryScanOut"/>.
/// </summary>
public interface IRenderBackend
{
    /// <summary>
    /// True when the backend owns render-target memory: the CPU rasteriser is skipped, and the backend keeps guest
    /// memory coherent (it writes images back when the guest reads them, and picks up pixels the guest writes).
    /// False: the CPU renderer stays authoritative and the backend only mirrors its work.
    /// </summary>
    bool OwnsMemory { get; }

    /// <summary>Performs a 2D copy on the host GPU; returns false to let the 2D engine copy guest memory instead.</summary>
    bool Blit(GpuBlit blit);

    void Clear(GpuClear clear);

    void Draw(GpuDraw draw);

    /// <summary>Guest memory in [address, address+length) was written outside the 3D engine (2D blit, memory copy).</summary>
    void MemoryWritten(uint address, uint length);

    /// <summary>
    /// The picture for the framebuffer at <paramref name="address"/>, or null when the backend has no current image of
    /// it (the CPU scan-out is then used). Pixels are BGRA32, gamma not applied.
    /// </summary>
    Frame? TryScanOut(uint address, int width, int height, int pitch, long vblank);
}

/// <summary>Builds <see cref="GpuDraw"/> vertex streams from vertex-stage outputs.</summary>
public static class GpuGeometry
{
    /// <summary>Appends one vertex (vertex-stage outputs <paramref name="o"/>) with the chosen colour registers.</summary>
    public static void Append(List<float> dst, Vector4[] o, int colorReg, int specularReg, Vector2? position = null)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(o);
        var p = o[VertexProgram.OPos];
        if (position is { } xy) p = new Vector4(xy, p.Z, p.W);
        Add(dst, new Vector4(p.X, p.Y, p.Z, p.W == 0 ? 1 : p.W));
        Add(dst, Vector4.Clamp(o[colorReg], Vector4.Zero, Vector4.One));
        Add(dst, Vector4.Clamp(o[specularReg], Vector4.Zero, Vector4.One));
        Add(dst, new Vector4(Math.Clamp(o[VertexProgram.OFog].X, 0, 1), 0, 0, 0));
        for (var t = 0; t < 4; t++) Add(dst, o[VertexProgram.OT0 + t]);
    }

    private static void Add(List<float> dst, Vector4 v)
    {
        dst.Add(v.X);
        dst.Add(v.Y);
        dst.Add(v.Z);
        dst.Add(v.W);
    }
}
