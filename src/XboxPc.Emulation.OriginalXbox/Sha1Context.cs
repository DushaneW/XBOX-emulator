using System.Buffers.Binary;
using XboxPc.Emulation.Core;

namespace XboxPc.Emulation.OriginalXbox;

/// <summary>
/// SHA-1 (FIPS 180-4) whose entire state is serialised in the title-provided context buffer, so XcSHAInit/Update/Final
/// keep no hidden host-side state. The kernel's own context layout is opaque to titles; this project's layout
/// (within the 116 bytes nxdk allocates) is: H0..H4 (20 bytes), message length in bytes (8), pending block (64).
/// </summary>
public static class Sha1Context
{
    public const int Size = 92;

    public static void Init(PhysicalBus bus, uint ctx)
    {
        ArgumentNullException.ThrowIfNull(bus);
        uint[] h = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0];
        for (var i = 0; i < 5; i++) bus.Write32(ctx + (uint)(i * 4), h[i]);
        bus.Write32(ctx + 20, 0);
        bus.Write32(ctx + 24, 0);
        bus.WriteBytes(ctx + 28, new byte[64]);
    }

    public static void Update(PhysicalBus bus, uint ctx, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(bus);
        var h = ReadState(bus, ctx);
        var length = bus.Read32(ctx + 20) | ((ulong)bus.Read32(ctx + 24) << 32);
        var block = bus.ReadBytes(ctx + 28, 64);
        var used = (int)(length % 64);
        foreach (var b in data)
        {
            block[used++] = b;
            if (used == 64) { Compress(h, block); used = 0; }
        }
        length += (ulong)data.Length;
        WriteState(bus, ctx, h);
        bus.Write32(ctx + 20, (uint)length);
        bus.Write32(ctx + 24, (uint)(length >> 32));
        bus.WriteBytes(ctx + 28, block);
    }

    public static byte[] Final(PhysicalBus bus, uint ctx)
    {
        ArgumentNullException.ThrowIfNull(bus);
        var length = bus.Read32(ctx + 20) | ((ulong)bus.Read32(ctx + 24) << 32);
        var bits = length * 8;
        var padLength = (int)((length % 64) < 56 ? 56 - (length % 64) : 120 - (length % 64));
        var pad = new byte[padLength + 8];
        pad[0] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(pad.AsSpan(padLength), bits);
        Update(bus, ctx, pad);
        var h = ReadState(bus, ctx);
        var digest = new byte[20];
        for (var i = 0; i < 5; i++) BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4), h[i]);
        return digest;
    }

    private static uint[] ReadState(PhysicalBus bus, uint ctx) => Enumerable.Range(0, 5).Select(i => bus.Read32(ctx + (uint)(i * 4))).ToArray();

    private static void WriteState(PhysicalBus bus, uint ctx, uint[] h)
    {
        for (var i = 0; i < 5; i++) bus.Write32(ctx + (uint)(i * 4), h[i]);
    }

    private static void Compress(uint[] h, byte[] block)
    {
        var w = new uint[80];
        for (var i = 0; i < 16; i++) w[i] = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(i * 4));
        for (var i = 16; i < 80; i++) w[i] = uint.RotateLeft(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
        uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
        for (var i = 0; i < 80; i++)
        {
            var (f, k) = i switch
            {
                < 20 => ((b & c) | (~b & d), 0x5A827999u),
                < 40 => (b ^ c ^ d, 0x6ED9EBA1u),
                < 60 => ((b & c) | (b & d) | (c & d), 0x8F1BBCDCu),
                _ => (b ^ c ^ d, 0xCA62C1D6u),
            };
            var temp = uint.RotateLeft(a, 5) + f + e + k + w[i];
            e = d;
            d = c;
            c = uint.RotateLeft(b, 30);
            b = a;
            a = temp;
        }
        h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e;
    }
}
