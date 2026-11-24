namespace XboxPc.Emulation.Gpu.Nv2a;

/// <summary>A DMA object (context DMA) read from instance memory.</summary>
public readonly record struct DmaObject(uint Class, uint Address, uint Limit)
{
    /// <summary>Layout as the hardware reads it: flags (class, address adjust in bits 20-31), limit, frame address.</summary>
    public static DmaObject Load(Nv2aDevice gpu, uint instance)
    {
        var o = instance << 4;
        var flags = gpu.RaminRead32(o);
        var limit = gpu.RaminRead32(o + 4);
        var frame = gpu.RaminRead32(o + 8);
        var address = ((frame & 0xFFFF_F000) | (flags >> 20)) & 0x07FF_FFFF;
        return new DmaObject(flags & 0xFFF, address, limit);
    }
}

/// <summary>
/// PFIFO: the command FIFO. Modelled: the DMA pusher (fetches commands from the channel's push buffer through its
/// DMA object: increasing / non-increasing methods, old and new jumps, call/return), RAMHT object lookup (hash as
/// the hardware computes it), the puller (SET_OBJECT binding, handle-to-instance translation for methods
/// 0x180-0x1FC, dispatch to PGRAPH) and the USER area (DMA_PUT/DMA_GET/REF per channel). The pusher runs
/// synchronously when DMA_PUT is written and resumes when PGRAPH releases a stall (interrupt acknowledged).
/// Not modelled: PIO mode, CACHE0, multiple channels running concurrently, semaphores, the software engine.
/// </summary>
public sealed class Pfifo(Nv2aDevice gpu)
{
    public const uint Intr0 = 0x002100, IntrEn0 = 0x002140, Ramht = 0x002210, Ramfc = 0x002214, RunoutStatus = 0x002400;
    public const uint Caches = 0x002500, Mode = 0x002504, Dma = 0x002508;
    public const uint Cache1Push0 = 0x003200, Cache1Push1 = 0x003204, Cache1Status = 0x003214, Cache1DmaPush = 0x003220;
    public const uint Cache1DmaFetch = 0x003224, Cache1DmaState = 0x003228, Cache1DmaInstance = 0x00322C, Cache1DmaCtl = 0x003230;
    public const uint Cache1DmaPut = 0x003240, Cache1DmaGet = 0x003244, Cache1Ref = 0x003248, Cache1DmaSubroutine = 0x00324C;
    public const uint Cache1Pull0 = 0x003250, Cache1Engine = 0x003280;
    public const uint IntrDmaPusher = 0x0000_1000, IntrCacheError = 0x0000_0001;

    /// <summary>Safety bound on words processed per pusher run (a looping push buffer is reported, not hung on).</summary>
    public const int MaxWordsPerRun = 4_000_000;

    private readonly Dictionary<uint, uint> _regs = [];
    private bool _running;

    public uint Interrupt { get; private set; }
    public uint InterruptEnable { get; private set; }
    public long WordsFetched { get; private set; }
    public long MethodsDispatched { get; private set; }

    private uint Reg(uint r) => _regs.GetValueOrDefault(r);

    public int CurrentChannel => (int)(Reg(Cache1Push1) & 0x1F);

    internal uint ReadRegister(uint offset) => offset switch
    {
        Intr0 => Interrupt,
        IntrEn0 => InterruptEnable,
        RunoutStatus => 0x10,   // runout FIFO empty
        Cache1Status => 0x10,   // CACHE1 empty: the puller consumes every method immediately
        _ => Reg(offset),
    };

    internal void WriteRegister(uint offset, uint value)
    {
        switch (offset)
        {
            case Intr0: Interrupt &= ~value; break;
            case IntrEn0: InterruptEnable = value; break;
            default: _regs[offset] = value; break;
        }
        if (offset is Cache1DmaPush or Cache1Push0 or Cache1Pull0 or Cache1DmaPut) Run();
    }

    internal uint ReadUser(int channel, uint offset)
    {
        if (channel != CurrentChannel || (Reg(Mode) & (1u << channel)) == 0)
        {
            gpu.Report($"NV2A USER read of channel {channel} offset 0x{offset:X} while channel {CurrentChannel} is loaded - returned 0");
            return 0;
        }
        return offset switch
        {
            0x40 => Reg(Cache1DmaPut),
            0x44 => Reg(Cache1DmaGet),
            0x48 => Reg(Cache1Ref),
            _ => Unknown(),
        };

        uint Unknown()
        {
            gpu.Report($"NV2A USER read of unmodelled offset 0x{offset:X}");
            return 0;
        }
    }

    internal void WriteUser(int channel, uint offset, uint value)
    {
        if (channel != CurrentChannel || (Reg(Mode) & (1u << channel)) == 0 || offset != 0x40)
        {
            gpu.Report($"NV2A USER write channel {channel} offset 0x{offset:X} = 0x{value:X} ignored (only DMA_PUT of the loaded DMA channel is modelled)");
            return;
        }
        _regs[Cache1DmaPut] = value;
        Run();
    }

    // ------------------------------------------------------------------ DMA pusher

    private bool PusherEnabled =>
        (Reg(Cache1DmaPush) & 1) != 0 && (Reg(Cache1Push0) & 1) != 0 && (Reg(Cache1Pull0) & 1) != 0 &&
        (Reg(Mode) & (1u << CurrentChannel)) != 0;

    /// <summary>Processes the push buffer from DMA_GET to DMA_PUT, stopping early when PGRAPH stalls.</summary>
    public void Run()
    {
        if (_running || !PusherEnabled) return;
        _running = true;
        try { Pump(); }
        finally { _running = false; }
        gpu.UpdateIrq();
    }

    private void Pump()
    {
        var dma = DmaObject.Load(gpu, Reg(Cache1DmaInstance) & 0xFFFF);
        var state = Reg(Cache1DmaState);
        var nonIncreasing = (state & 1) != 0;
        var method = state & 0x1FFC;
        var subchannel = (int)((state >> 13) & 7);
        var count = (state >> 18) & 0x7FF;
        var get = Reg(Cache1DmaGet);
        var put = Reg(Cache1DmaPut);
        var subroutine = Reg(Cache1DmaSubroutine);
        var channel = CurrentChannel;

        for (var n = 0; get != put; n++)
        {
            if (n >= MaxWordsPerRun)
            {
                gpu.Report($"NV2A DMA pusher stopped after {MaxWordsPerRun} words without reaching DMA_PUT (push buffer loops?) at GET 0x{get:X}");
                break;
            }
            if (get > dma.Limit)
            {
                Error($"DMA_GET 0x{get:X} beyond the push buffer DMA limit 0x{dma.Limit:X}");
                break;
            }
            var word = gpu.Memory.Read32(dma.Address + get);
            if (count > 0)
            {
                if (!Puller(channel, subchannel, method, word)) break; // stalled: the word stays at GET
                WordsFetched++;
                get += 4;
                if (!nonIncreasing) method += 4;
                count--;
                continue;
            }
            WordsFetched++;
            get += 4;
            if ((word & 0xE000_0003) == 0x2000_0000) get = word & 0x1FFF_FFFC;                  // old jump
            else if ((word & 3) == 1) get = word & 0xFFFF_FFFC;                                 // jump
            else if ((word & 3) == 2)                                                           // call
            {
                if ((subroutine & 1) != 0) { Error("CALL while a subroutine is active"); break; }
                subroutine = get | 1;
                get = word & 0xFFFF_FFFC;
            }
            else if (word == 0x0002_0000)                                                       // return
            {
                if ((subroutine & 1) == 0) { Error("RETURN without an active subroutine"); break; }
                get = subroutine & 0xFFFF_FFFC;
                subroutine = 0;
            }
            else if ((word & 0xE003_0003) is 0 or 0x4000_0000)                                  // method header
            {
                nonIncreasing = (word & 0x4000_0000) != 0;
                method = word & 0x1FFC;
                subchannel = (int)((word >> 13) & 7);
                count = (word >> 18) & 0x7FF;
            }
            else
            {
                Error($"reserved command 0x{word:X8} at GET 0x{get - 4:X}");
                break;
            }
        }

        _regs[Cache1DmaGet] = get;
        _regs[Cache1DmaSubroutine] = subroutine;
        _regs[Cache1DmaState] = (nonIncreasing ? 1u : 0) | method | ((uint)subchannel << 13) | (count << 18);
    }

    private void Error(string what)
    {
        Interrupt |= IntrDmaPusher;
        _regs[Cache1DmaPush] = Reg(Cache1DmaPush) & ~1u; // pusher suspended until software intervenes
        gpu.Report($"NV2A DMA pusher error: {what}");
    }

    // ------------------------------------------------------------------ RAMHT and puller

    public readonly record struct RamhtEntry(uint Handle, uint Instance, int Engine, int Channel);

    /// <summary>Looks a handle up in the hash table exactly where the hardware would.</summary>
    public RamhtEntry? LookupHandle(uint handle, int channel)
    {
        var ramht = Reg(Ramht);
        var size = 1u << (int)(((ramht >> 16) & 3) + 12);
        var bits = System.Numerics.BitOperations.TrailingZeroCount(size) - 1;
        uint hash = 0;
        for (var h = handle; h != 0; h >>= bits) hash ^= h & ((1u << bits) - 1);
        hash ^= (uint)channel << (bits - 4);
        var address = ((ramht & 0x1F0) << 8) + (hash * 8);
        var entryHandle = gpu.RaminRead32(address);
        var context = gpu.RaminRead32(address + 4);
        if (entryHandle != handle || (context & 0x8000_0000) == 0) return null;
        return new RamhtEntry(handle, (context & 0xFFFF) << 4, (int)((context >> 16) & 3), (int)((context >> 24) & 0x1F));
    }

    private bool Puller(int channel, int subchannel, uint method, uint parameter)
    {
        if (method == 0)
        {
            if (LookupHandle(parameter, channel) is not { } e)
            {
                Interrupt |= IntrCacheError;
                gpu.Report($"NV2A RAMHT lookup failed for object handle 0x{parameter:X} (SET_OBJECT on subchannel {subchannel})");
                return true;
            }
            var engines = Reg(Cache1Engine);
            _regs[Cache1Engine] = (engines & ~(3u << (subchannel * 4))) | ((uint)e.Engine << (subchannel * 4));
            if (e.Engine != 1)
            {
                gpu.Report($"NV2A object 0x{parameter:X} bound to the software engine on subchannel {subchannel} - software methods are not modelled");
                return true;
            }
            if (!gpu.Pgraph.SetObject(channel, subchannel, e.Instance >> 4)) return false;
            MethodsDispatched++;
            return true;
        }
        if (((Reg(Cache1Engine) >> (subchannel * 4)) & 3) != 1)
        {
            gpu.Report($"NV2A method 0x{method:X4} on subchannel {subchannel} without a graphics object - ignored");
            return true;
        }
        if (method is >= 0x180 and < 0x200 && parameter != 0)
        {
            if (LookupHandle(parameter, channel) is { } e) parameter = e.Instance >> 4;
            else gpu.Report($"NV2A RAMHT lookup failed for context handle 0x{parameter:X} (method 0x{method:X})");
        }
        if (!gpu.Pgraph.Method(channel, subchannel, method, parameter)) return false;
        MethodsDispatched++;
        return true;
    }
}
