namespace XboxPc.Emulation.Gpu.Nv2a;

/// <summary>
/// NV2A 2D object classes (method layouts from nxdk's nv_objects.h). State is kept per object instance, because a
/// blit refers to its surfaces object by handle.
/// <list type="bullet">
/// <item>0x62 NV10_CONTEXT_SURFACES_2D: source/destination DMA (0x184/0x188), FORMAT (0x300), PITCH src | dst&lt;&lt;16
/// (0x304), OFFSET_SRC (0x308), OFFSET_DST (0x30C).</item>
/// <item>0x9F image blit: SURFACE (0x19C, a 0x62 object), OPERATION (0x2FC), POINT_IN (0x300), POINT_OUT (0x304), SIZE
/// (0x308, triggers the copy). Operation SRCCOPY (3) is executed; rectangles may overlap (copied through a buffer).</item>
/// <item>0x39 NV_MEMORY_TO_MEMORY_FORMAT: OBJECT_IN/OUT DMA (0x184/0x188), OFFSET_IN/OUT (0x30C/0x310), PITCH_IN/OUT
/// (0x314/0x318), LINE_LENGTH_IN (0x31C), LINE_COUNT (0x320), FORMAT (0x324: byte increments, only 1/1 supported),
/// BUF_NOTIFY (0x328, triggers the copy).</item>
/// </list>
/// Not modelled (reported): blit operations other than SRCCOPY (ROP, beta blending, colour key), clip-rectangle
/// objects, notifiers, the GDI/text and pattern classes, swizzled-surface (0x52) destinations.
/// </summary>
public sealed class TwoDEngine(Nv2aDevice gpu)
{
    private readonly Dictionary<uint, uint[]> _objects = [];
    private readonly HashSet<string> _reported = [];

    public long Blits { get; private set; }
    public long MemoryCopies { get; private set; }
    public long BytesCopied { get; private set; }

    public static bool Handles(uint cls) => cls is 0x62 or 0x9F or 0x39 or 0x19 or 0x12 or 0x72 or 0x43 or 0x44;

    private void ReportOnce(string message)
    {
        if (_reported.Add(message)) gpu.Report(message);
    }

    private uint[] State(uint instance)
    {
        if (!_objects.TryGetValue(instance, out var s)) _objects[instance] = s = new uint[0x800];
        return s;
    }

    public uint Get(uint instance, uint method) => State(instance)[(method & 0x1FFC) >> 2];

    public void Method(uint cls, uint instance, uint method, uint p)
    {
        State(instance)[(method & 0x1FFC) >> 2] = p;
        if (method == 0x104 && p != 0) ReportOnce($"NV2A class 0x{cls:X2} notify requested - notifiers are not modelled");
        switch (cls)
        {
            case 0x9F when method == 0x308: Blit(instance); break;
            case 0x39 when method == 0x328: MemoryToMemory(instance); break;
            case 0x9F when method is 0x184 or 0x188 or 0x18C or 0x190 or 0x194 or 0x198 && p != 0:
                ReportOnce($"NV2A image blit context 0x{method:X3} (colour key/clip/pattern/rop/beta) set - not applied to blits");
                break;
            case 0x19 or 0x12 or 0x72 or 0x43 or 0x44 when method >= 0x300:
                ReportOnce($"NV2A 2D context class 0x{cls:X2} state stored - only used through blits, which do not apply it");
                break;
        }
    }

    public static int SurfaceBytesPerPixel(uint format) => format switch
    {
        1 => 1,
        2 or 3 or 4 or 5 => 2,
        >= 6 and <= 0xB => 4,
        _ => 0,
    };

    private void Blit(uint blit)
    {
        var operation = Get(blit, 0x2FC);
        if (operation != 3) ReportOnce($"NV2A image blit operation {operation} not implemented - SRCCOPY used");
        var surfaces = Get(blit, 0x19C);
        if (surfaces == 0) { ReportOnce("NV2A image blit without a surfaces object - skipped"); return; }
        var bpp = SurfaceBytesPerPixel(Get(surfaces, 0x300) & 0xFF);
        if (bpp == 0) { ReportOnce($"NV2A 2D surface format 0x{Get(surfaces, 0x300) & 0xFF:X} not implemented - blit skipped"); return; }
        var src = DmaObject.Load(gpu, Get(surfaces, 0x184));
        var dst = DmaObject.Load(gpu, Get(surfaces, 0x188));
        var pitch = Get(surfaces, 0x304);
        int srcPitch = (int)(pitch & 0xFFFF), dstPitch = (int)(pitch >> 16);
        uint srcOffset = Get(surfaces, 0x308), dstOffset = Get(surfaces, 0x30C);
        var pin = Get(blit, 0x300);
        var pout = Get(blit, 0x304);
        var size = Get(blit, 0x308);
        int inX = (int)(pin & 0xFFFF), inY = (int)(pin >> 16), outX = (int)(pout & 0xFFFF), outY = (int)(pout >> 16);
        int width = (int)(size & 0xFFFF), height = (int)(size >> 16);
        if (width == 0 || height == 0) return;

        if (gpu.Backend?.Blit(new GpuBlit(src.Address + srcOffset, srcPitch, dst.Address + dstOffset, dstPitch, inX, inY, outX, outY, width, height, bpp, Get(surfaces, 0x300) & 0xFF)) == true)
        {
            Blits++;
            BytesCopied += (long)width * height * bpp;
            return;
        }
        var rowBytes = width * bpp;
        var buffer = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var o = srcOffset + (uint)((inY + y) * srcPitch) + (uint)(inX * bpp);
            if (o + (uint)rowBytes - 1 > src.Limit) { ReportOnce("NV2A image blit source beyond its DMA limit - clipped"); return; }
            for (var i = 0; i < rowBytes; i++) buffer[(y * rowBytes) + i] = gpu.Memory.Read8(src.Address + o + (uint)i);
        }
        for (var y = 0; y < height; y++)
        {
            var o = dstOffset + (uint)((outY + y) * dstPitch) + (uint)(outX * bpp);
            if (o + (uint)rowBytes - 1 > dst.Limit) { ReportOnce("NV2A image blit destination beyond its DMA limit - clipped"); return; }
            if (gpu.Memory.TryGetWritableRam(dst.Address + o, rowBytes, out var row)) buffer.AsSpan(y * rowBytes, rowBytes).CopyTo(row);
            else for (var i = 0; i < rowBytes; i++) gpu.Memory.Write8(dst.Address + o + (uint)i, buffer[(y * rowBytes) + i]);
        }
        Blits++;
        BytesCopied += buffer.Length;
        gpu.Backend?.MemoryWritten(dst.Address + dstOffset + (uint)(outY * dstPitch), (uint)(height * dstPitch));
    }

    private void MemoryToMemory(uint m2mf)
    {
        var format = Get(m2mf, 0x324);
        if ((format & 0xFF) > 1 || ((format >> 8) & 0xFF) > 1) ReportOnce($"NV2A memory-to-memory format 0x{format:X} (byte increments) not implemented - increment 1 used");
        var src = DmaObject.Load(gpu, Get(m2mf, 0x184));
        var dst = DmaObject.Load(gpu, Get(m2mf, 0x188));
        uint inOffset = Get(m2mf, 0x30C), outOffset = Get(m2mf, 0x310);
        int inPitch = (int)Get(m2mf, 0x314), outPitch = (int)Get(m2mf, 0x318);
        int length = (int)Get(m2mf, 0x31C), lines = (int)Get(m2mf, 0x320);
        var line = new byte[length];
        for (var l = 0; l < lines; l++)
        {
            var si = inOffset + (uint)(l * inPitch);
            var di = outOffset + (uint)(l * outPitch);
            if (si + (uint)length - 1 > src.Limit || di + (uint)length - 1 > dst.Limit) { ReportOnce("NV2A memory-to-memory copy beyond a DMA limit - stopped"); return; }
            for (var i = 0; i < length; i++) line[i] = gpu.Memory.Read8(src.Address + si + (uint)i);
            for (var i = 0; i < length; i++) gpu.Memory.Write8(dst.Address + di + (uint)i, line[i]);
        }
        MemoryCopies++;
        BytesCopied += (long)length * lines;
        if (lines > 0) gpu.Backend?.MemoryWritten(dst.Address + outOffset, (uint)(((lines - 1) * Math.Abs(outPitch)) + length));
    }
}
