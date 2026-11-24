using System.Buffers.Binary;
using System.Text;
using XboxPc.Emulation.OriginalXbox;

namespace XboxPc.Emulation.Tests.OriginalXbox;

/// <summary>
/// Builds XBE files from scratch following the public format description. These are synthetic test executables written
/// by this project, no Microsoft or third-party title data is used.
/// </summary>
public sealed class XbeBuilder
{
    public const uint Base = 0x10000;
    public const uint TextVa = 0x11000;
    public const uint DataVa = 0x11400;
    public const uint ThunkVa = 0x11800;

    public List<byte> Code { get; } = [];
    public string Data { get; set; } = "";
    public List<uint> Imports { get; } = [];
    public XbeKeyKind Kind { get; set; } = XbeKeyKind.Retail;
    public string Title { get; set; } = "XPC Synthetic Test";

    public byte[] Build()
    {
        var file = new byte[0x2000];
        var s = file.AsSpan();
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(o), v);
        W32(0x000, 0x48454258);
        W32(0x104, Base);
        W32(0x108, 0x1000);               // size of headers
        W32(0x10C, 0x2000);               // size of image
        W32(0x118, Base + 0x200);         // certificate
        W32(0x11C, 1);                    // sections
        W32(0x120, Base + 0x300);         // section headers
        W32(0x128, TextVa ^ (Kind == XbeKeyKind.Retail ? XbeImage.EntryXorRetail : XbeImage.EntryXorDebug));
        W32(0x130, 0x10000);              // stack commit
        W32(0x158, ThunkVa ^ (Kind == XbeKeyKind.Retail ? XbeImage.ThunkXorRetail : XbeImage.ThunkXorDebug));
        W32(0x160, 1);                    // library versions
        W32(0x164, Base + 0x500);

        W32(0x200, 0x1D0);                // certificate size
        W32(0x208, 0x58500001);           // title id (synthetic)
        Encoding.Unicode.GetBytes(Title).CopyTo(s[0x20C..]);

        // section header: flags (writable|preload|executable), va, vsize, raw, rawsize, name addr
        W32(0x300, 0x7); W32(0x304, TextVa); W32(0x308, 0x1000); W32(0x30C, 0x1000); W32(0x310, 0x1000); W32(0x314, Base + 0x400);
        Encoding.ASCII.GetBytes(".text\0").CopyTo(s[0x400..]);
        Encoding.ASCII.GetBytes("XPCTEST\0").CopyTo(s[0x500..]);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x508..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x50A..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x50C..], 1);

        Code.ToArray().CopyTo(s[0x1000..]);
        Encoding.ASCII.GetBytes(Data + "\0").CopyTo(s[(int)(0x1000 + DataVa - TextVa)..]);
        for (var i = 0; i < Imports.Count; i++) W32((int)(0x1000 + ThunkVa - TextVa) + (i * 4), 0x8000_0000 | Imports[i]);
        return file;
    }

    /// <summary>call dword [thunk slot]</summary>
    public XbeBuilder CallImport(int slot)
    {
        var a = ThunkVa + (uint)(slot * 4);
        Code.AddRange([0xFF, 0x15, (byte)a, (byte)(a >> 8), (byte)(a >> 16), (byte)(a >> 24)]);
        return this;
    }

    public XbeBuilder Emit(params byte[] bytes) { Code.AddRange(bytes); return this; }

    public XbeBuilder PushImm32(uint v) => Emit(0x68, (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24));
}

public class XbeParserTests
{
    [Theory]
    [InlineData(XbeKeyKind.Retail)]
    [InlineData(XbeKeyKind.Debug)]
    public void Parses_headers_sections_certificate_and_decodes_entry(XbeKeyKind kind)
    {
        var b = new XbeBuilder { Kind = kind };
        b.Imports.Add(8);
        b.Emit(0xF4);
        var xbe = XbeImage.Parse(b.Build());
        Assert.Equal(kind, xbe.KeyKind);
        Assert.Equal(XbeBuilder.Base, xbe.BaseAddress);
        Assert.Equal(XbeBuilder.TextVa, xbe.EntryPoint);
        Assert.Equal(XbeBuilder.ThunkVa, xbe.KernelThunkAddress);
        Assert.Equal("XPC Synthetic Test", xbe.TitleName);
        Assert.Equal(0x58500001u, xbe.TitleId);
        var section = Assert.Single(xbe.Sections);
        Assert.Equal(".text", section.Name);
        Assert.True(section.IsExecutable);
        Assert.Equal(new XbeLibrary("XPCTEST", 1, 0, 1), Assert.Single(xbe.Libraries));
    }

    private static byte[] Valid() => new XbeBuilder().Emit(0xF4).Build();

    private static void Corrupt(byte[] file, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(offset), value);

    [Fact]
    public void Rejects_bad_magic() { var f = Valid(); f[0] = (byte)'M'; Assert.Throws<XbeFormatException>(() => XbeImage.Parse(f)); }

    [Fact]
    public void Rejects_truncated_file() => Assert.Throws<XbeFormatException>(() => XbeImage.Parse(Valid().AsSpan(0, 0x100)));

    [Fact]
    public void Rejects_headers_larger_than_file() { var f = Valid(); Corrupt(f, 0x108, 0x10_0000); Assert.Throws<XbeFormatException>(() => XbeImage.Parse(f)); }

    [Fact]
    public void Rejects_section_raw_data_outside_file() { var f = Valid(); Corrupt(f, 0x30C, 0x1F00); Assert.Throws<XbeFormatException>(() => XbeImage.Parse(f)); }

    [Fact]
    public void Rejects_section_outside_image() { var f = Valid(); Corrupt(f, 0x304, 0x7000_0000); Assert.Throws<XbeFormatException>(() => XbeImage.Parse(f)); }

    [Fact]
    public void Rejects_section_table_pointer_outside_headers() { var f = Valid(); Corrupt(f, 0x120, 0xFFFF_FFF0); Assert.Throws<XbeFormatException>(() => XbeImage.Parse(f)); }

    [Fact]
    public void Rejects_absurd_section_count() { var f = Valid(); Corrupt(f, 0x11C, 100_000); Assert.Throws<XbeFormatException>(() => XbeImage.Parse(f)); }

    [Fact]
    public void Rejects_entry_point_that_decodes_nowhere() { var f = Valid(); Corrupt(f, 0x128, 0x1234_5678); Assert.Throws<XbeFormatException>(() => XbeImage.Parse(f)); }

    [Fact]
    public void Random_corruption_never_crashes_the_parser()
    {
        var rng = new Random(1234);
        var original = Valid();
        for (var i = 0; i < 3000; i++)
        {
            var f = (byte[])original.Clone();
            for (var k = 0; k < 4; k++) f[rng.Next(0, 0x600)] = (byte)rng.Next(256);
            try { XbeImage.Parse(f); }
            catch (XbeFormatException) { }
        }
    }
}

public class MachineTests
{
    private const uint ImportDbgPrint = 8, ImportHalReturnToFirmware = 49, ImportIoDismountVolumeByName = 91;

    private static OriginalXboxMachine Load(XbeBuilder b)
    {
        var file = b.Build();
        var machine = new OriginalXboxMachine();
        machine.Load(XbeImage.Parse(file), file);
        return machine;
    }

    [Fact]
    public void Synthetic_xbe_runs_prints_through_dbgprint_and_exits()
    {
        var b = new XbeBuilder { Data = "Hello from emulated x86: %d + %d = %d [%s]\n" };
        b.Imports.AddRange([ImportDbgPrint, ImportHalReturnToFirmware]);
        // compute 19+23 in x86, then DbgPrint(fmt, 19, 23, eax, "ok") ; HalReturnToFirmware(0)
        b.Emit(0xB8, 19, 0, 0, 0)               // mov eax,19
         .Emit(0x83, 0xC0, 23)                  // add eax,23
         .PushImm32(XbeBuilder.DataVa + 0x80)   // "ok" string pointer (placed below)
         .Emit(0x50)                            // push eax
         .Emit(0x6A, 23)                        // push 23
         .Emit(0x6A, 19)                        // push 19
         .PushImm32(XbeBuilder.DataVa)          // push fmt
         .CallImport(0)                         // call [DbgPrint]
         .Emit(0x83, 0xC4, 20)                  // add esp,20 (cdecl)
         .Emit(0x6A, 0x00)                      // push 0
         .CallImport(1)                         // call [HalReturnToFirmware]
         .Emit(0xF4);
        var file = b.Build();
        Encoding.ASCII.GetBytes("ok\0").CopyTo(file, 0x1000 + (int)(XbeBuilder.DataVa - XbeBuilder.TextVa) + 0x80);
        var machine = new OriginalXboxMachine();
        machine.Load(XbeImage.Parse(file), file);

        var result = machine.Run(10_000);

        Assert.Equal(MachineStop.TitleExited, result.Stop);
        Assert.Equal("Hello from emulated x86: 19 + 23 = 42 [ok]\n", machine.Kernel.DebugOutput);
        Assert.Equal([8, 49], machine.ImportedOrdinals);
        Assert.Equal(11, result.Instructions); // guest x86 instructions; HLE kernel calls are not guest instructions
        Assert.Single(machine.Threads);
    }

    [Fact]
    public void Unimplemented_kernel_export_stops_with_its_ordinal()
    {
        var b = new XbeBuilder();
        b.Imports.Add(ImportIoDismountVolumeByName);
        b.CallImport(0).Emit(0xF4);
        var result = Load(b).Run(100);
        Assert.Equal(MachineStop.KernelExportUnimplemented, result.Stop);
        Assert.Equal(91, result.KernelOrdinal);
        Assert.Contains("#91 IoDismountVolumeByName", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_a_kernel_data_export_is_reported()
    {
        var b = new XbeBuilder();
        b.Imports.Add(323); // XboxHDKey: key material is never provided, so access must stop with a report
        var slot = XbeBuilder.ThunkVa;
        b.Emit(0xA1, (byte)slot, (byte)(slot >> 8), (byte)(slot >> 16), (byte)(slot >> 24)) // mov eax,[thunk]
         .Emit(0x8B, 0x00)                                                               // mov eax,[eax]
         .Emit(0xF4);
        var result = Load(b).Run(100);
        Assert.Equal(MachineStop.KernelDataExportAccessed, result.Stop);
        Assert.Equal(323, result.KernelOrdinal);
    }

    [Fact]
    public void Unmodelled_gpu_register_access_is_reported_not_hidden()
    {
        var b = new XbeBuilder();
        b.Emit(0xA1, 0x00, 0x40, 0x00, 0xFD).Emit(0xCC); // mov eax,[0xFD004000] (PMEDIA: not modelled) ; int3
        var machine = Load(b);
        machine.Run(100);
        Assert.Equal(0u, machine.Cpu[XboxPc.Emulation.X86.X86Cpu.EAX]);
        Assert.Contains(machine.UnmodelledAccesses, u => u.Contains("NV2A read of unmodelled register 0x004000", StringComparison.Ordinal));
    }

    [Fact]
    public void Fs_points_to_kpcr_with_current_thread_and_stack_bounds()
    {
        var b = new XbeBuilder();
        b.Emit(0x64, 0xA1, 0x28, 0, 0, 0)        // mov eax, fs:[0x28]  (KPRCB.CurrentThread)
         .Emit(0x64, 0x8B, 0x0D, 0x04, 0, 0, 0) // mov ecx, fs:[0x04]  (NT_TIB.StackBase)
         .Emit(0xCC);                           // int3: stop here
        var machine = Load(b);
        var result = machine.Run(100);
        Assert.Equal(MachineStop.CpuException, result.Stop);
        var thread = Assert.Single(machine.Threads);
        Assert.Equal(thread.EThread, machine.Cpu[XboxPc.Emulation.X86.X86Cpu.EAX]);
        Assert.Equal(thread.StackBase, machine.Cpu[XboxPc.Emulation.X86.X86Cpu.ECX]);
        Assert.Equal(thread.StackBase, machine.Bus.Read32(thread.EThread + GuestLayout.KThreadStackBase));
    }

    [Fact]
    public void Kernel_view_mirror_aliases_title_memory()
    {
        var b = new XbeBuilder();
        b.Emit(0xF4);
        var machine = Load(b);
        Assert.Equal(machine.Bus.Read32(XbeBuilder.TextVa), machine.Bus.Read32(OriginalXboxMachine.KernelRamMirror + XbeBuilder.TextVa));
    }

    [Fact]
    public void Unterminated_or_non_ordinal_thunk_is_rejected()
    {
        var b = new XbeBuilder();
        b.Emit(0xF4);
        var file = b.Build();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x1000 + (int)(XbeBuilder.ThunkVa - XbeBuilder.TextVa)), 0x0001_2345); // not an ordinal
        Assert.Throws<XbeFormatException>(() => new OriginalXboxMachine().Load(XbeImage.Parse(file), file));
    }

    [Theory]
    [InlineData("%d|%u|%x|%X|%08x|%5d|%c|%%", "-1|4294967295|ffffffff|FFFFFFFF|ffffffff|   -1|A|%")]
    public void Printf_subset(string format, string expected)
    {
        var bus = new XboxPc.Emulation.Core.PhysicalBus();
        uint[] args = [0xFFFF_FFFF, 0xFFFF_FFFF, 0xFFFF_FFFF, 0xFFFF_FFFF, 0xFFFF_FFFF, 0xFFFF_FFFF, 'A'];
        Assert.Equal(expected, KernelHle.FormatPrintf(format, i => args[i], bus));
    }
}
