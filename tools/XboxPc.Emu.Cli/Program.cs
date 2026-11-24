using System.Diagnostics;
using System.Globalization;
using XboxPc.Emulation.Core;
using XboxPc.Emulation.OriginalXbox;
using XboxPc.Emulation.X86;

// xpcemu, headless runner for the XBOX PC emulation engine.
// Only use executables you are legally entitled to run (e.g. homebrew you built with the open-source nxdk).

var inv = CultureInfo.InvariantCulture;
if (args.Length == 0) return Usage();

try
{
    switch (args[0])
    {
        case "xbe-info" when args.Length >= 2: return XbeInfo(args[1]);
        case "run-xbe" when args.Length >= 2: return RunXbe(args[1], args[2..]);
        case "run-raw" when args.Length >= 3: return RunRaw(args[1], uint.Parse(args[2].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, inv), Max(args, 3));
        case "bench": return Bench(Max(args, 1, 200_000_000));
        case "profile": return Profile(args.Length > 1 ? args[1] : null);
        default: return Usage();
    }
}
catch (Exception e) when (e is XbeFormatException or IOException or UnauthorizedAccessException or FormatException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

static long Max(string[] a, int i, long fallback = 100_000_000) =>
    a.Length > i ? long.Parse(a[i], CultureInfo.InvariantCulture) : fallback;

static int Usage()
{
    Console.WriteLine($$"""
        xpcemu, XBOX PC emulation engine (Original Xbox track, milestone M3)

          xpcemu xbe-info <file.xbe>                 parse and print XBE headers, sections, libraries, kernel imports
          xpcemu run-xbe  <file.xbe> [maxInstr] [--trace] [--hdd <dir>] [--screenshot <out.bmp>] [--wav <out.wav>] [--keys W,Space]
                          execute with HLE kernel. D: = the XBE's directory (read-only); --hdd maps
                          \Device\Harddisk0\Partition1 (E:) to a writable host directory; --keys holds keyboard keys
                          (default profile, Keyboard and Mouse mode); --wav records the AC97 PCM output.
          xpcemu run-raw  <file.bin> <baseHex> [max] execute flat IA-32 code at base (64 MB RAM at 0)
          xpcemu bench    [instructions]             measure interpreter throughput
          xpcemu profile  [probe.xbe]                cost breakdown experiments (+ steady-state run of a real XBE)

        Honest status: Pentium III CPU (integer, x87, MMX, SSE1), {{KernelHle.ImplementedOrdinals.Count}} of {{KernelExports.ByOrdinal.Count}} kernel exports (HLE),
        NV2A scan-out + PFIFO/PGRAPH Kelvin subset (CPU renderer, no textures), AC97 PCM/S-PDIF DMA (no APU), OHCI USB + XID controller,
        sandboxed disc/hard-disk file system. Commercial titles are not expected to run: they need textures, fixed-function
        T&L and the rest of the NV2A, the APU and many more kernel exports. Every gap stops execution or is reported with a precise reason. See docs/engine/.
        """);
    return 1;
}

static int XbeInfo(string path)
{
    var file = File.ReadAllBytes(path);
    var xbe = XbeImage.Parse(file);
    Console.WriteLine($"Title        : {xbe.TitleName} (ID {xbe.TitleId:X8})");
    Console.WriteLine($"Key kind     : {xbe.KeyKind}");
    Console.WriteLine($"Base / image : 0x{xbe.BaseAddress:X8} / 0x{xbe.SizeOfImage:X} bytes");
    Console.WriteLine($"Entry point  : 0x{xbe.EntryPoint:X8}");
    Console.WriteLine($"Kernel thunk : 0x{xbe.KernelThunkAddress:X8}");
    Console.WriteLine("Sections:");
    foreach (var s in xbe.Sections)
        Console.WriteLine($"  {s.Name,-12} va 0x{s.VirtualAddress:X8} vsize 0x{s.VirtualSize:X6} raw 0x{s.RawAddress:X6}+0x{s.RawSize:X6} {(s.IsExecutable ? "X" : "-")}{(s.IsWritable ? "W" : "-")}");
    Console.WriteLine("Libraries:");
    foreach (var l in xbe.Libraries) Console.WriteLine($"  {l.Name,-8} {l.Major}.{l.Minor}.{l.Build}");
    var machine = new OriginalXboxMachine();
    machine.Load(xbe, file);
    var implemented = machine.ImportedOrdinals.Count(o => KernelHle.ImplementedOrdinals.Contains(o));
    Console.WriteLine($"Kernel imports: {machine.ImportedOrdinals.Count} ({implemented} implemented by HLE)");
    Console.WriteLine("  " + string.Join(", ", machine.ImportedOrdinals.Select(o => KernelHle.ImplementedOrdinals.Contains(o) ? $"#{o} {KernelHle.NameOf(o)}" : $"#{o}")));
    return 0;
}

static string? Option(string[] opts, string name) => Array.IndexOf(opts, name) is var i and >= 0 && i + 1 < opts.Length ? opts[i + 1] : null;

static int RunXbe(string path, string[] opts)
{
    var max = long.TryParse(opts.FirstOrDefault(o => !o.StartsWith("--", StringComparison.Ordinal) && !opts.Any(x => x.StartsWith("--", StringComparison.Ordinal) && Option(opts, x) == o)), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 200_000_000;
    var trace = opts.Contains("--trace");
    var file = File.ReadAllBytes(path);
    var machine = new OriginalXboxMachine { ImageFileName = Path.GetFileName(path) };
    machine.MountDefaults(Path.GetDirectoryName(Path.GetFullPath(path))!, Option(opts, "--hdd") is { } hdd ? Path.GetFullPath(hdd) : null);
    machine.Load(XbeImage.Parse(file), file);
    var wav = Option(opts, "--wav") is { } wavPath ? new WavCapture() : null;
    if (wav is not null) machine.Audio.Output = wav;
    if (Option(opts, "--keys") is { } keys)
    {
        // Headless input: keyboard keys held for the whole run, mapped through the default profile (keyboard+mouse mode).
        machine.Input.Profile = XboxPc.Emulation.Input.InputProfile.CreateDefault() with { Mode = XboxPc.Emulation.Input.InputMode.KeyboardMouse };
        machine.Input.OnFocusChanged(true);
        var frame = new XboxPc.Emulation.Input.HostInputFrame();
        foreach (var k in keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) frame.Keys.Add(k);
        machine.Input.Update(frame, 1.0 / 60, 0);
    }
    if (opts.Contains("--diag"))
    {
        // Progress report every 1M instructions: wall time, virtual time, interrupts, USB frames, EIP.
        var sw = Stopwatch.StartNew();
        for (var slice = 0; slice < max / 1_000_000; slice++)
        {
            var rr = machine.Run(1_000_000);
            Console.WriteLine($"diag {slice}: wall {sw.ElapsedMilliseconds} ms, cycles {machine.Cycles:N0}, idle {machine.IdleCyclesSkipped:N0}, irq {machine.InterruptsDelivered}, dpc {machine.DpcsExecuted}, usb frames {machine.Usb.Frames}, eip 0x{machine.Cpu.Eip:X8}, stop {rr.Stop}");
            if (rr.Stop != MachineStop.BudgetExhausted) break;
        }
        return 0;
    }
    var r = machine.Run(max);
    if (wav is not null && Option(opts, "--wav") is { } wavOut)
    {
        wav.Save(wavOut);
        Console.WriteLine($"Audio        : {wavOut} ({wav.Frames} stereo frames, {wav.Frames * 1000.0 / XboxPc.Emulation.Audio.Ac97Controller.SampleRate:F1} ms at 48 kHz)");
    }
    if (Option(opts, "--screenshot") is { } shot)
    {
        if (machine.Gpu.ScanOut() is { } frame) { Bmp.Write(shot, frame.Width, frame.Height, frame.Bgra); Console.WriteLine($"Screenshot   : {shot} ({frame.Width}x{frame.Height}, vblank {frame.VBlank})"); }
        else Console.WriteLine("Screenshot   : no display mode was set by the title");
    }
    Console.WriteLine($"Audio (AC97) : PCM out {machine.Audio.PcmOut.BuffersCompleted} buffers, {machine.Audio.PcmOut.SamplesConsumed / 2} frames; S/PDIF {machine.Audio.Spdif.BuffersCompleted} buffers; {machine.Audio.InterruptsRaised} interrupts");
    Console.WriteLine($"USB          : {machine.Usb.Frames} frames, {machine.Usb.TransfersCompleted} transfers; ports {string.Join(" ", Enumerable.Range(0, 4).Select(i => machine.Usb.IsAttached(i) ? $"{i + 1}:XID(addr {machine.Gamepads[i].Address}, {(machine.Gamepads[i].Configured ? "configured" : "not configured")}, {machine.Gamepads[i].ReportsSent} reports, rumble {machine.Gamepads[i].LastRumble.Left:X4}/{machine.Gamepads[i].LastRumble.Right:X4})" : $"{i + 1}:-"))}");
    var kelvin = machine.Gpu.Pgraph.Kelvin;
    if (machine.Gpu.Pfifo.MethodsDispatched > 0)
        Console.WriteLine($"NV2A 3D      : {machine.Gpu.Pfifo.WordsFetched:N0} push-buffer words, {machine.Gpu.Pfifo.MethodsDispatched:N0} methods; {kelvin.Clears:N0} clears, {kelvin.TrianglesDrawn:N0} triangles, {kelvin.LinesDrawn:N0} lines, {kelvin.PointsDrawn:N0} points, {kelvin.PixelsWritten:N0} pixels; 2D {machine.Gpu.Pgraph.TwoD.Blits:N0} blits, {machine.Gpu.Pgraph.TwoD.MemoryCopies:N0} memory copies; host time clear {kelvin.ClearTime.TotalMilliseconds:F0} ms, vertex {kelvin.VertexTime.TotalMilliseconds:F0} ms, raster {kelvin.RasterTime.TotalMilliseconds:F0} ms");
    Console.WriteLine($"Video        : {(machine.Gpu.Width > 0 ? $"{machine.Gpu.Width}x{machine.Gpu.Height} {machine.Gpu.Format} fb=0x{machine.Gpu.FramebufferStart:X8}" : "no mode set")}; {machine.Gpu.VBlankCount} vblanks; {machine.InterruptsDelivered} interrupts, {machine.DpcsExecuted} DPCs delivered");
    foreach (var a in machine.FileActivity.Take(50)) Console.WriteLine($"File         : {a}");
    if (machine.Kernel.DebugOutput.Length > 0) Console.WriteLine("--- DbgPrint output ---\n" + machine.Kernel.DebugOutput + "\n-----------------------");
    Console.WriteLine($"Stopped      : {r.Stop}");
    Console.WriteLine($"Detail       : {r.Detail}");
    Console.WriteLine($"Instructions : {r.Instructions:N0} in {r.Elapsed.TotalMilliseconds:F1} ms ({r.Mips:F1} MIPS)");
    Console.WriteLine($"Kernel calls : {machine.Kernel.Calls} ({string.Join(", ", machine.Kernel.CallCounts.OrderBy(k => k.Key).Select(k => $"#{k.Key} {KernelHle.NameOf(k.Key)}x{k.Value}"))})");
    Console.WriteLine($"Threads      : {string.Join(", ", machine.Threads.Select(t => $"{t.Id}:{t.State}"))}");
    Console.WriteLine($"Virtual time : {machine.Cycles:N0} cycles ({machine.Cycles / (double)OriginalXboxMachine.CpuHz * 1000:F3} ms at 733.33 MHz, {machine.IdleCyclesSkipped:N0} idle cycles skipped)");
    foreach (var u in machine.UnmodelledAccesses.Distinct()) Console.WriteLine($"Unmodelled   : {u}");
    if (trace)
    {
        Console.WriteLine("--- kernel call trace (cycle, thread, export, arguments -> result) ---");
        foreach (var c in machine.Kernel.Trace)
            Console.WriteLine($"{c.Cycle,10} T{c.Thread} #{c.Ordinal,-3} {c.Name,-30} ({string.Join(", ", c.Args.Select(a => $"0x{a:X}"))}) -> {(c.Result is { } res ? $"0x{res:X}" : "(no return / blocked / stop)")}");
    }
    return r.Stop == MachineStop.TitleExited ? 0 : 3;
}

static int RunRaw(string path, uint baseAddress, long max)
{
    var bus = new PhysicalBus();
    bus.MapRam("RAM", 0, new byte[OriginalXboxMachine.RamSize]);
    bus.WriteBytes(baseAddress, File.ReadAllBytes(path));
    var cpu = new X86Cpu(bus) { Eip = baseAddress };
    cpu[X86Cpu.ESP] = OriginalXboxMachine.DefaultStackTop;
    var sw = Stopwatch.StartNew();
    var r = cpu.Run(max);
    sw.Stop();
    Console.WriteLine($"Stopped: {r.Reason} {r.Error?.Message}");
    Console.WriteLine($"EAX={cpu[0]:X8} ECX={cpu[1]:X8} EDX={cpu[2]:X8} EBX={cpu[3]:X8} ESP={cpu[4]:X8} EBP={cpu[5]:X8} ESI={cpu[6]:X8} EDI={cpu[7]:X8} EIP={cpu.Eip:X8} EFLAGS={cpu.Eflags:X8}");
    Console.WriteLine($"{r.Executed:N0} instructions in {sw.Elapsed.TotalMilliseconds:F1} ms ({r.Executed / sw.Elapsed.TotalSeconds / 1e6:F1} MIPS)");
    return r.Reason == StopReason.Halted ? 0 : 3;
}

static int Bench(long instructions)
{
    // Mixed integer workload: memory load/store, ALU, compare-and-branch.  esi = buffer, ecx = counter
    byte[] code =
    [
        0xBE, 0x00, 0x00, 0x20, 0x00,       // mov esi, 0x200000
        0x31, 0xC0,                         // xor eax, eax
        0xB9, 0xFF, 0xFF, 0xFF, 0x7F,       // mov ecx, 0x7FFFFFFF
        // loop:
        0x8B, 0x16,                         // mov edx, [esi]
        0x01, 0xD0,                         // add eax, edx
        0xC1, 0xC0, 0x03,                   // rol eax, 3
        0x89, 0x06,                         // mov [esi], eax
        0x83, 0xC6, 0x04,                   // add esi, 4
        0x81, 0xE6, 0xFF, 0xFF, 0x2F, 0x00, // and esi, 0x2FFFFF
        0x49,                               // dec ecx
        0x75, 0xEB,                         // jnz loop
        0xF4,
    ];
    var bus = new PhysicalBus();
    bus.MapRam("RAM", 0, new byte[OriginalXboxMachine.RamSize]);
    bus.WriteBytes(0x10000, code);
    var cpu = new X86Cpu(bus) { Eip = 0x10000 };
    cpu[X86Cpu.ESP] = OriginalXboxMachine.DefaultStackTop;
    cpu.Run(100_000); // warm-up (JIT tiering)
    var sw = Stopwatch.StartNew();
    var r = cpu.Run(instructions);
    sw.Stop();
    var mips = r.Executed / sw.Elapsed.TotalSeconds / 1e6;
    Console.WriteLine($"Executed {r.Executed:N0} IA-32 instructions in {sw.Elapsed.TotalSeconds:F2} s = {mips:F1} MIPS (interpreter, single thread).");
    Console.WriteLine($"Reference: Original Xbox CPU clock is 733 MHz; a Pentium III retires roughly 0.5-1 instructions/cycle on integer code,");
    Console.WriteLine($"so real hardware executes very roughly 400-700 MIPS. Ratio here: {mips / 550 * 100:F0}% of a ~550 MIPS midpoint (estimate, not a measurement of the console).");
    return 0;
}

static int Profile(string? xbePath)
{
    const long N = 100_000_000;
    double Measure(string name, byte[] loopBody, Action<X86Cpu, PhysicalBus>? setup = null)
    {
        // loop: <body> ; dec ecx ; jnz loop
        var code = loopBody.Concat(new byte[] { 0x49, 0x75, (byte)(-(loopBody.Length + 3)) }).Concat(new byte[] { 0xF4 }).ToArray();
        var bus = new PhysicalBus();
        bus.MapRam("RAM", 0, new byte[OriginalXboxMachine.RamSize]);
        bus.WriteBytes(0x10000, code);
        var cpu = new X86Cpu(bus) { Eip = 0x10000 };
        cpu[X86Cpu.ESP] = OriginalXboxMachine.DefaultStackTop;
        cpu[X86Cpu.ESI] = 0x200000;
        cpu[X86Cpu.ECX] = 0x7FFF_FFFF;
        setup?.Invoke(cpu, bus);
        cpu.Run(200_000); // warm-up / tiered JIT
        var sw = Stopwatch.StartNew();
        var r = cpu.Run(N);
        sw.Stop();
        var ns = sw.Elapsed.TotalMilliseconds * 1e6 / r.Executed;
        Console.WriteLine($"  {name,-44} {r.Executed / sw.Elapsed.TotalSeconds / 1e6,7:F1} MIPS  {ns,6:F1} ns/instr");
        return ns;
    }

    Console.WriteLine($"Interpreter cost experiments ({N:N0} instructions each, Release, single thread):");
    var nop = Measure("nop x1 per loop (+dec/jnz)", [0x90]);
    var nop8 = Measure("nop x8 per loop (+dec/jnz)", [0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90]);
    // per-iteration time: 3·nop1 vs 10·nop8 -> slope gives the pure cost of one NOP
    var pureNop = ((10 * nop8) - (3 * nop)) / 7;
    var loopOverhead = (3 * nop) - pureNop;
    Console.WriteLine($"  => pure NOP (fetch+decode+dispatch+retire): {pureNop:F1} ns; dec+jnz pair: {loopOverhead:F1} ns");
    Measure("add eax,ebx (register ALU + flags)", [0x01, 0xD8]);
    Measure("mov eax,[esi] (ModR/M + RAM load)", [0x8B, 0x06]);
    Measure("mov [esi+8],eax (ModR/M+disp8 + RAM store)", [0x89, 0x46, 0x08]);
    Measure("imul eax,ebx (0F two-byte opcode)", [0x0F, 0xAF, 0xC3]);
    Measure("fld st0; fstp st0 (x87 stack)", [0xD9, 0xC0, 0xDD, 0xD8]);
    Measure("addss xmm0,xmm1 (SSE scalar)", [0xF3, 0x0F, 0x58, 0xC1]);
    Measure("paddw mm0,mm1 (MMX lanes)", [0x0F, 0xFD, 0xC1]);

    // Bus fast path in isolation
    {
        var bus = new PhysicalBus();
        bus.MapRam("RAM", 0, new byte[OriginalXboxMachine.RamSize]);
        var sw = Stopwatch.StartNew();
        uint acc = 0;
        for (uint i = 0; i < N; i++) acc += bus.Read8(0x10000 + (i & 0xFFF), AccessKind.Fetch);
        sw.Stop();
        Console.WriteLine($"  {"PhysicalBus.Read8 alone",-44} {N / sw.Elapsed.TotalSeconds / 1e6,7:F1} M/s   {sw.Elapsed.TotalMilliseconds * 1e6 / N,6:F1} ns/call (checksum {acc})");
    }

    if (xbePath is not null)
    {
        var file = File.ReadAllBytes(xbePath);
        var times = new List<double>();
        long instr = 0;
        for (var i = 0; i < 15; i++)
        {
            var m = new OriginalXboxMachine();
            m.Load(XbeImage.Parse(file), file);
            var r = m.Run(100_000_000);
            if (i >= 5) times.Add(r.Elapsed.TotalMilliseconds); // discard warm-up runs
            instr = r.Instructions;
        }
        times.Sort();
        var median = times[times.Count / 2];
        Console.WriteLine($"Real XBE ({Path.GetFileName(xbePath)}): {instr:N0} instructions, median {median:F1} ms over 10 runs = {instr / median / 1000:F1} MIPS (includes machine creation per run: no)");
    }
    return 0;
}

static class Bmp
{
    /// <summary>Writes a 32-bit top-down BMP (no external imaging dependency).</summary>
    public static void Write(string path, int width, int height, uint[] bgra)
    {
        using var f = File.Create(path);
        using var w = new BinaryWriter(f);
        var size = 54 + (bgra.Length * 4);
        w.Write((byte)'B'); w.Write((byte)'M'); w.Write(size); w.Write(0); w.Write(54);
        w.Write(40); w.Write(width); w.Write(-height); w.Write((short)1); w.Write((short)32); w.Write(0); w.Write(bgra.Length * 4); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        foreach (var p in bgra) w.Write(p);
    }
}

/// <summary>Collects the emulated PCM output and writes it as a 16-bit stereo 48 kHz WAV file.</summary>
sealed class WavCapture : XboxPc.Emulation.Audio.IAudioSink
{
    private readonly List<short> _samples = [];

    public long Frames => _samples.Count / 2;

    public void Write(ReadOnlySpan<short> interleavedStereo) => _samples.AddRange(interleavedStereo);

    public void Save(string path)
    {
        using var w = new BinaryWriter(File.Create(path));
        var data = _samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(XboxPc.Emulation.Audio.Ac97Controller.SampleRate);
        w.Write(XboxPc.Emulation.Audio.Ac97Controller.SampleRate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        foreach (var s in _samples) w.Write(s);
    }
}
