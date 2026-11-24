using System.Security.Cryptography;
using XboxPc.Emulation.Audio;
using XboxPc.Emulation.Core;
using XboxPc.Emulation.OriginalXbox;

namespace XboxPc.Emulation.Tests.Audio;

public sealed class CaptureSink : IAudioSink
{
    public List<short> Samples { get; } = [];

    public void Write(ReadOnlySpan<short> interleavedStereo) => Samples.AddRange(interleavedStereo);
}

/// <summary>AC97 controller: register semantics in isolation, and the nxdk audio HAL end to end (xpc-audio-test).</summary>
public class Ac97Tests
{
    private sealed class Irq : IInterruptController
    {
        public int Count;

        public void RaiseIrq(int line) => Count++;
    }

    private sealed class Report : IHardwareReport
    {
        public List<string> Messages { get; } = [];

        public void Unmodelled(string message) => Messages.Add(message);
    }

    private const long Hz = 48_000_000; // 48 000 cycles per ms keeps the arithmetic obvious

    private static (Ac97Controller Ac97, PhysicalBus Bus, Irq Irq, Report Report, CaptureSink Sink) Create()
    {
        var bus = new PhysicalBus();
        bus.MapRam("RAM", 0, new byte[0x10_0000]);
        var irq = new Irq();
        var report = new Report();
        var sink = new CaptureSink();
        var ac97 = new Ac97Controller(bus, irq, 6, Hz, report) { Output = sink };
        return (ac97, bus, irq, report, sink);
    }

    /// <summary>Descriptor list at 0x1000; buffer i at 0x2000 + i*0x1000 holding <paramref name="words"/> samples of value i+1.</summary>
    private static void Describe(PhysicalBus bus, int index, int words, bool ioc)
    {
        var buf = 0x2000u + ((uint)index * 0x1000);
        for (var w = 0; w < words; w++) bus.Write16(buf + ((uint)w * 2), (ushort)(index + 1));
        bus.Write32(0x1000 + ((uint)index * 8), buf);
        bus.Write32(0x1000 + ((uint)index * 8) + 4, (uint)words | (ioc ? 0x8000_0000u : 0));
    }

    private static void Tick(Ac97Controller a, int ms)
    {
        for (var i = 0; i < ms; i++) a.OnEvent(a.NextEventCycle);
    }

    [Fact]
    public void Reset_state_and_codec_ready_after_cold_reset()
    {
        var (a, _, _, _, _) = Create();
        Assert.Equal(Ac97Controller.SrDch, a.Read(0x116, 2, AccessKind.Read));
        Assert.Equal(0u, a.Read(Ac97Controller.GlobalStatus, 4, AccessKind.Read) & 0x100);
        a.Write(Ac97Controller.GlobalControl, 4, 2);
        Assert.Equal(0x100u, a.Read(Ac97Controller.GlobalStatus, 4, AccessKind.Read) & 0x100);
    }

    [Fact]
    public void Plays_descriptors_in_order_raises_completion_interrupts_and_halts_at_last_valid()
    {
        var (a, bus, irq, _, sink) = Create();
        Describe(bus, 0, 96, ioc: true);  // exactly 1 ms
        Describe(bus, 1, 192, ioc: true); // 2 ms
        a.Write(0x110, 4, 0x1000);
        a.Write(0x115, 1, 1);
        a.Write(0x11B, 1, Ac97Controller.CrRpbm | Ac97Controller.CrIoce | Ac97Controller.CrLvbie);

        Tick(a, 1);
        Assert.Equal(96, sink.Samples.Count);
        Assert.All(sink.Samples, s => Assert.Equal(1, s));
        Assert.Equal(1, a.PcmOut.Civ);
        Assert.NotEqual(0, a.PcmOut.Sr & Ac97Controller.SrBcis);
        Assert.Equal(1, irq.Count);

        a.Write(0x116, 1, 0xFF); // ISR clears
        Assert.Equal(0, a.PcmOut.Sr & Ac97Controller.SrBcis);
        Tick(a, 3);
        Assert.Equal(96 + 192, sink.Samples.Count);
        Assert.All(sink.Samples.Skip(96), s => Assert.Equal(2, s));
        var sr = a.PcmOut.Sr;
        Assert.Equal(Ac97Controller.SrDch | Ac97Controller.SrCelv | Ac97Controller.SrLvbci | Ac97Controller.SrBcis, sr);
        Assert.Equal(2, irq.Count);
    }

    [Fact]
    public void Writing_lvi_restarts_a_halted_engine_at_the_next_descriptor()
    {
        var (a, bus, _, _, sink) = Create();
        Describe(bus, 0, 96, ioc: false);
        a.Write(0x110, 4, 0x1000);
        a.Write(0x11B, 1, Ac97Controller.CrRpbm);
        Tick(a, 2);
        Assert.NotEqual(0, a.PcmOut.Sr & Ac97Controller.SrDch);
        Describe(bus, 1, 96, ioc: false);
        a.Write(0x115, 1, 1);
        Assert.Equal(0, a.PcmOut.Sr & Ac97Controller.SrDch);
        Assert.Equal(1, a.PcmOut.Civ);
        Tick(a, 2);
        Assert.Equal(192, sink.Samples.Count);
        Assert.Equal(2, sink.Samples[^1]);
    }

    [Fact]
    public void Pause_stops_consumption_and_reset_clears_the_engine()
    {
        var (a, bus, _, _, sink) = Create();
        Describe(bus, 0, 960, ioc: false);
        a.Write(0x110, 4, 0x1000);
        a.Write(0x11B, 1, Ac97Controller.CrRpbm);
        Tick(a, 2);
        Assert.Equal(192, sink.Samples.Count);
        a.Write(0x11B, 1, 0); // pause
        Tick(a, 5);
        Assert.Equal(192, sink.Samples.Count);
        Assert.Equal(960 - 192, a.PcmOut.Picb);
        a.Write(0x11B, 1, Ac97Controller.CrRr);
        Assert.Equal((0u, 0, 0, Ac97Controller.SrDch), (a.PcmOut.Bdbar, a.PcmOut.Civ, a.PcmOut.Lvi, a.PcmOut.Sr));
    }

    [Fact]
    public void Codec_and_pcm_in_accesses_are_reported()
    {
        var (a, _, _, report, _) = Create();
        a.Read(0x7C, 2, AccessKind.Read);
        a.Write(0x10B, 1, Ac97Controller.CrRpbm);
        Assert.Contains(report.Messages, m => m.Contains("codec mixer", StringComparison.Ordinal));
        Assert.Contains(report.Messages, m => m.Contains("PCM-in", StringComparison.Ordinal));
    }

    [Fact]
    public void Nxdk_audio_hal_streams_every_sample_exactly_in_real_time()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "xpc-audio-test");
        var file = File.ReadAllBytes(Path.Combine(dir, "default.xbe"));
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "default.xbe.sha256")).Trim(), Convert.ToHexStringLower(SHA256.HashData(file)));
        var m = new OriginalXboxMachine();
        m.MountDefaults(dir, null);
        m.Load(XbeImage.Parse(file), file);
        var sink = new CaptureSink();
        m.Audio.Output = sink;

        var r = m.Run(400_000_000);
        Assert.Equal(MachineStop.TitleExited, r.Stop);
        Assert.Equal("XPC-AUDIO buffers=48 callbacks=48 elapsed_ms=1600\n", m.Kernel.DebugOutput);

        const int frames = 48 * 1600;
        Assert.Equal(frames * 2, sink.Samples.Count);
        for (var n = 0; n < frames; n++)
        {
            Assert.Equal(((n / 24) & 1) != 0 ? 8000 : -8000, sink.Samples[2 * n]);
            Assert.Equal(unchecked((short)(n * 7)), sink.Samples[(2 * n) + 1]);
        }
        Assert.Equal(48, m.Audio.PcmOut.BuffersCompleted);
        Assert.Equal(48, m.Audio.Spdif.BuffersCompleted); // S/PDIF consumes the same buffers in lockstep (required by nxdk's ISR)
    }
}
