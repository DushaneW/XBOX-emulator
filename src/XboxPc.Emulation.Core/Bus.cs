using System.Buffers.Binary;

namespace XboxPc.Emulation.Core;

public enum AccessKind
{
    Read,
    Write,
    Fetch,
}

/// <summary>Raised when the guest touches an address with no device behind it (or a device that refuses).</summary>
public sealed class BusFaultException : Exception
{
    public BusFaultException(uint address, AccessKind kind, int size, string? region = null, string? detail = null)
        : base($"Bus fault: {kind} of {size} byte(s) at 0x{address:X8}" + (region is null ? " (unmapped)" : $" in {region}") + (detail is null ? "" : $": {detail}"))
    {
        Address = address;
        Kind = kind;
        Size = size;
        Region = region;
        Detail = detail;
    }

    public BusFaultException() : this(0, AccessKind.Read, 0) { }

    public BusFaultException(string message) : base(message) { }

    public BusFaultException(string message, Exception inner) : base(message, inner) { }

    public uint Address { get; }
    public AccessKind Kind { get; }
    public int Size { get; }
    public string? Region { get; }
    public string? Detail { get; }
}

/// <summary>A memory-mapped device. Offsets are relative to the start of its mapping.</summary>
public interface IMmioDevice
{
    string Name { get; }
    uint Read(uint offset, int size, AccessKind kind);
    void Write(uint offset, int size, uint value);
}

/// <summary>
/// 32-bit physical address space. Dispatch is by 64 KB page; RAM accesses take a direct span fast path,
/// everything else goes to <see cref="IMmioDevice"/>. Little-endian, unaligned accesses allowed (as on x86).
/// </summary>
/// <summary>Told when watched RAM is accessed through the bus (see <see cref="PhysicalBus.WatchRam"/>).</summary>
public interface IRamWatcher
{
    /// <summary>Called before the access. <paramref name="ramOffset"/> is the offset in the watched backing array.</summary>
    void OnWatchedAccess(uint ramOffset, int size, bool write);
}

public sealed class PhysicalBus
{
    /// <summary>Watch granularity: 4 KB of the watched backing array.</summary>
    public const int WatchShift = 12;

    /// <summary>Watch states: none, every access (the data lives elsewhere, e.g. on the host GPU), writes only (a copy elsewhere must be told).</summary>
    public const byte WatchNone = 0, WatchAll = 1, WatchWrites = 2;

    private byte[]? _watchedRam;
    private byte[]? _watchFlags;
    private IRamWatcher? _watcher;
    private bool _inWatch;

    public const int PageShift = 16;
    public const uint PageSize = 1u << PageShift;
    private const int PageCount = 1 << (32 - PageShift);

    private readonly Region?[] _pages = new Region?[PageCount];
    private readonly List<Region> _regions = [];

    private sealed record Region(string Name, uint Base, uint Size, byte[]? Ram, int RamOffset, bool ReadOnly, IMmioDevice? Device);

    public IReadOnlyList<(string Name, uint Base, uint Size, bool IsRam)> Map =>
        _regions.Select(r => (r.Name, r.Base, r.Size, r.Ram is not null)).ToList();

    /// <summary>Maps <paramref name="backing"/> (or a slice of it) at <paramref name="baseAddress"/>. The same array may be mapped more than once (mirrors).</summary>
    public void MapRam(string name, uint baseAddress, byte[] backing, uint size = 0, int backingOffset = 0, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(backing);
        if (size == 0) size = (uint)backing.Length;
        if (backingOffset < 0 || (long)backingOffset + size > backing.Length) throw new ArgumentOutOfRangeException(nameof(size), "Mapping exceeds the backing array.");
        Add(new Region(name, baseAddress, size, backing, backingOffset, readOnly, null));
    }

    public void MapDevice(uint baseAddress, uint size, IMmioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Add(new Region(device.Name, baseAddress, size, null, 0, false, device));
    }

    private void Add(Region region)
    {
        if (region.Size == 0 || (region.Base & (PageSize - 1)) != 0 || (region.Size & (PageSize - 1)) != 0)
            throw new ArgumentException($"Region '{region.Name}' must be non-empty and aligned to {PageSize:N0} bytes.");
        if ((ulong)region.Base + region.Size > 0x1_0000_0000UL) throw new ArgumentException($"Region '{region.Name}' exceeds 4 GB.");
        var first = (int)(region.Base >> PageShift);
        var count = (int)(region.Size >> PageShift);
        for (var i = first; i < first + count; i++)
            if (_pages[i] is { } existing) throw new ArgumentException($"Region '{region.Name}' overlaps '{existing.Name}'.");
        for (var i = first; i < first + count; i++) _pages[i] = region;
        _regions.Add(region);
    }

    public bool IsMapped(uint address) => _pages[address >> PageShift] is not null;

    /// <summary>
    /// Starts watching the RAM mapped at <paramref name="address"/> (its whole backing array, through every mirror):
    /// accesses through the bus to pages marked with <see cref="SetWatch"/> call <paramref name="watcher"/> first.
    /// Device fast paths (<see cref="TryGetWritableRam"/>) are not watched. One watcher per bus.
    /// </summary>
    public void WatchRam(uint address, IRamWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        var r = _pages[address >> PageShift] ?? throw new ArgumentException("Address is not mapped.", nameof(address));
        _watchedRam = r.Ram ?? throw new ArgumentException("Address is not RAM.", nameof(address));
        _watchFlags = new byte[(_watchedRam.Length >> WatchShift) + 1];
        _watcher = watcher;
    }

    /// <summary>Stops watching (all flags cleared).</summary>
    public void Unwatch()
    {
        _watchedRam = null;
        _watchFlags = null;
        _watcher = null;
    }

    /// <summary>Offset of <paramref name="address"/> in the watched backing array, or false when it is not watched RAM.</summary>
    public bool TryWatchedOffset(uint address, out uint ramOffset)
    {
        ramOffset = 0;
        var r = _pages[address >> PageShift];
        if (r?.Ram is null || !ReferenceEquals(r.Ram, _watchedRam)) return false;
        ramOffset = (uint)r.RamOffset + (address - r.Base);
        return true;
    }

    /// <summary>Marks [ramOffset, ramOffset+length) of the watched array with a watch state.</summary>
    public void SetWatch(uint ramOffset, uint length, byte state)
    {
        if (_watchFlags is null || length == 0) return;
        var first = (int)(ramOffset >> WatchShift);
        var last = (int)Math.Min((long)_watchFlags.Length - 1, ((long)ramOffset + length - 1) >> WatchShift);
        for (var i = first; i <= last; i++) _watchFlags[i] = state;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void Check(byte[] ram, int index, int size, bool write)
    {
        if (!ReferenceEquals(ram, _watchedRam) || _inWatch) return;
        var flags = _watchFlags!;
        var f = flags[index >> WatchShift];
        var g = flags[Math.Min(flags.Length - 1, (index + size - 1) >> WatchShift)]; // an access may straddle two pages
        if (g == WatchAll || (f == WatchNone && g != WatchNone)) f = g;
        if (f == WatchNone || (f == WatchWrites && !write)) return;
        _inWatch = true;
        try { _watcher!.OnWatchedAccess((uint)index, size, write); }
        finally { _inWatch = false; }
    }

    public string? RegionName(uint address) => _pages[address >> PageShift]?.Name;

    public byte Read8(uint address, AccessKind kind = AccessKind.Read)
    {
        var r = _pages[address >> PageShift] ?? throw new BusFaultException(address, kind, 1);
        if (r.Ram is not { } ram) return (byte)DevRead(r, address, 1, kind);
        var i = r.RamOffset + (int)(address - r.Base);
        Check(ram, i, 1, write: false);
        return ram[i];
    }

    public ushort Read16(uint address, AccessKind kind = AccessKind.Read)
    {
        if (TryRamSpan(address, 2, kind, write: false, out var span)) return BinaryPrimitives.ReadUInt16LittleEndian(span);
        if (SameDevice(address, 2) is { } r) return (ushort)DevRead(r, address, 2, kind);
        return (ushort)(Read8(address, kind) | (Read8(address + 1, kind) << 8));
    }

    public uint Read32(uint address, AccessKind kind = AccessKind.Read)
    {
        if (TryRamSpan(address, 4, kind, write: false, out var span)) return BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (SameDevice(address, 4) is { } r) return DevRead(r, address, 4, kind);
        return Read16(address, kind) | ((uint)Read16(address + 2, kind) << 16);
    }

    public void Write8(uint address, byte value)
    {
        var r = _pages[address >> PageShift] ?? throw new BusFaultException(address, AccessKind.Write, 1);
        if (r.Ram is { } ram)
        {
            if (r.ReadOnly) throw new BusFaultException(address, AccessKind.Write, 1, r.Name, "read-only");
            var i = r.RamOffset + (int)(address - r.Base);
            Check(ram, i, 1, write: true);
            ram[i] = value;
        }
        else DevWrite(r, address, 1, value);
    }

    public void Write16(uint address, ushort value)
    {
        if (TryRamSpan(address, 2, AccessKind.Write, write: true, out var span)) { BinaryPrimitives.WriteUInt16LittleEndian(span, value); return; }
        if (SameDevice(address, 2) is { } r) { DevWrite(r, address, 2, value); return; }
        EnsureMapped(address + 1, 2);
        Write8(address, (byte)value);
        Write8(address + 1, (byte)(value >> 8));
    }

    public void Write32(uint address, uint value)
    {
        if (TryRamSpan(address, 4, AccessKind.Write, write: true, out var span)) { BinaryPrimitives.WriteUInt32LittleEndian(span, value); return; }
        if (SameDevice(address, 4) is { } r) { DevWrite(r, address, 4, value); return; }
        EnsureMapped(address + 3, 4);
        Write16(address, (ushort)value);
        Write16(address + 2, (ushort)(value >> 16));
    }

    /// <summary>Bulk copy into the guest (loader use). Goes through the normal access path.</summary>
    public void WriteBytes(uint address, ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++) Write8(unchecked(address + (uint)i), data[i]);
    }

    public byte[] ReadBytes(uint address, int count)
    {
        var result = new byte[count];
        for (var i = 0; i < count; i++) result[i] = Read8(unchecked(address + (uint)i));
        return result;
    }

    /// <summary>Reads a NUL-terminated 8-bit string (bounded).</summary>
    public string ReadCString(uint address, int maxLength = 4096)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < maxLength; i++)
        {
            var b = Read8(unchecked(address + (uint)i));
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Direct access to <paramref name="length"/> bytes of writable RAM (device DMA fast path). Returns false when the
    /// range is not entirely inside one writable RAM region; callers then use the normal access methods.
    /// </summary>
    public bool TryGetWritableRam(uint address, int length, out Span<byte> span)
    {
        span = default;
        if (length <= 0 || _pages[address >> PageShift] is not { Ram: { } ram, ReadOnly: false } r) return false;
        var offset = address - r.Base;
        if (offset + (ulong)length > r.Size) return false;
        span = ram.AsSpan(r.RamOffset + (int)offset, length);
        return true;
    }

    private bool TryRamSpan(uint address, int size, AccessKind kind, bool write, out Span<byte> span)
    {
        span = default;
        var r = _pages[address >> PageShift];
        if (r is null)
        {
            // Unmapped: fault on the first byte that is unmapped.
            throw new BusFaultException(address, kind, size);
        }
        if (r.Ram is null) return false;
        var offset = address - r.Base;
        if (offset + (uint)size > r.Size) return false; // crosses region end: fall back to byte path
        if (write && r.ReadOnly) throw new BusFaultException(address, AccessKind.Write, size, r.Name, "read-only");
        var index = r.RamOffset + (int)offset;
        Check(r.Ram, index, size, write);
        span = r.Ram.AsSpan(index, size);
        return true;
    }

    /// <summary>A write that straddles into unmapped space must fault before any byte is written (x86 semantics).</summary>
    private void EnsureMapped(uint lastByte, int size)
    {
        if (_pages[lastByte >> PageShift] is null) throw new BusFaultException(lastByte, AccessKind.Write, size);
    }

    private static uint DevRead(Region r, uint address, int size, AccessKind kind)
    {
        try { return r.Device!.Read(address - r.Base, size, kind); }
        catch (BusFaultException e) when (e.Address != address) { throw new BusFaultException(address, e.Kind, size, r.Name, e.Detail); }
    }

    private static void DevWrite(Region r, uint address, int size, uint value)
    {
        try { r.Device!.Write(address - r.Base, size, value); }
        catch (BusFaultException e) when (e.Address != address) { throw new BusFaultException(address, AccessKind.Write, size, r.Name, e.Detail); }
    }

    private Region? SameDevice(uint address, int size)
    {
        var r = _pages[address >> PageShift];
        if (r?.Device is null) return null;
        return address - r.Base + (uint)size <= r.Size ? r : null;
    }
}

/// <summary>x86 I/O port space (64K ports). Unclaimed ports read as all-ones, like an open PC bus, and are counted.</summary>
public sealed class IoPortBus
{
    private readonly Dictionary<ushort, (Func<int, uint> Read, Action<int, uint> Write, string Name)> _ports = [];

    public long UnclaimedAccesses { get; private set; }
    public ushort? LastUnclaimedPort { get; private set; }

    public void Register(ushort port, string name, Func<int, uint> read, Action<int, uint> write) => _ports[port] = (read, write, name);

    public uint In(ushort port, int size)
    {
        if (_ports.TryGetValue(port, out var p)) return p.Read(size);
        UnclaimedAccesses++;
        LastUnclaimedPort = port;
        return size switch { 1 => 0xFFu, 2 => 0xFFFFu, _ => 0xFFFF_FFFFu };
    }

    public void Out(ushort port, int size, uint value)
    {
        if (_ports.TryGetValue(port, out var p)) { p.Write(size, value); return; }
        UnclaimedAccesses++;
        LastUnclaimedPort = port;
    }
}

/// <summary>An MMIO range that always faults with a descriptive message (for not-yet-emulated hardware).</summary>
public sealed class FaultingDevice(string name, string reason) : IMmioDevice
{
    public string Name { get; } = name;

    public uint Read(uint offset, int size, AccessKind kind) => throw new BusFaultException(offset, kind, size, Name, reason);

    public void Write(uint offset, int size, uint value) => throw new BusFaultException(offset, AccessKind.Write, size, Name, reason);
}
