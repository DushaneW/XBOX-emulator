namespace XboxPc.Emulation.OriginalXbox;

/// <summary>
/// Page-granular (4 KB) allocator over the emulated 64 MB of physical RAM, used by the HLE kernel for contiguous
/// memory, virtual memory, thread stacks and kernel structures. First-fit, honours alignment and address ranges.
/// No paging is modelled: virtual = physical for the title view, and the kernel view at 0x80000000 aliases it.
/// </summary>
public sealed class GuestMemory
{
    public const uint PageSize = 0x1000;

    private readonly SortedDictionary<uint, (uint Size, string Tag)> _allocations = [];
    private readonly uint _floor;
    private readonly uint _ceiling;

    public GuestMemory(uint floor, uint ceiling)
    {
        _floor = Align(floor, PageSize);
        _ceiling = ceiling & ~(PageSize - 1);
    }

    public IReadOnlyDictionary<uint, (uint Size, string Tag)> Allocations => _allocations;

    public uint BytesInUse => (uint)_allocations.Values.Sum(a => (long)a.Size);

    public static uint Align(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);

    /// <summary>Returns the base address, or null when no block satisfies the request.</summary>
    public uint? Allocate(uint size, string tag, uint alignment = PageSize, uint lowest = 0, uint highest = uint.MaxValue)
    {
        if (size == 0) return null;
        alignment = Math.Max(PageSize, alignment);
        if ((alignment & (alignment - 1)) != 0) return null;
        size = Align(size, PageSize);
        var candidate = Align(Math.Max(_floor, lowest), alignment);
        var top = Math.Min(_ceiling, highest == uint.MaxValue ? _ceiling : highest + 1);
        foreach (var (start, block) in _allocations)
        {
            if ((ulong)candidate + size <= start) break;
            if (start + block.Size > candidate) candidate = Align(start + block.Size, alignment);
        }
        if ((ulong)candidate + size > top) return null;
        _allocations.Add(candidate, (size, tag));
        return candidate;
    }

    /// <summary>Marks an existing range (e.g. the loaded XBE image) as used.</summary>
    public void Reserve(uint start, uint size, string tag)
    {
        var s = start & ~(PageSize - 1);
        _allocations.Add(s, (Align(start + size, PageSize) - s, tag));
    }

    public uint? SizeOf(uint address) => _allocations.TryGetValue(address, out var a) ? a.Size : null;

    public bool Free(uint address) => _allocations.Remove(address);

    /// <summary>The allocation that contains <paramref name="address"/>, if any.</summary>
    public (uint Start, uint Size)? Containing(uint address)
    {
        foreach (var (start, block) in _allocations)
        {
            if (start > address) break;
            if (address < start + block.Size) return (start, block.Size);
        }
        return null;
    }
}
