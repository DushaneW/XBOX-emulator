using System.Runtime.InteropServices;
using XboxPc.Emulation.Core;
using XboxPc.Emulation.X86;

namespace XboxPc.Emulation.Tests.X86;

/// <summary>
/// Runs one instruction sequence natively on the host CPU and in the interpreter, against identical data buffers.
/// All memory operands use [rcx+disp8], which encodes identically in 64-bit mode (host) and as [ecx+disp8] in
/// 32-bit mode (interpreter), so the very same bytes are executed by both. The sequence must leave the x87 stack
/// empty and restore any control state it changed (Windows x64 ABI), and must end with RET (0xC3).
/// </summary>
public sealed unsafe class HostOracle : IDisposable
{
    private const uint GuestCode = 0x1000, GuestBuffer = 0x4000;
    private readonly byte* _code = (byte*)VirtualAlloc(IntPtr.Zero, 4096, 0x3000, 0x40);
    private readonly byte* _buffer = (byte*)NativeMemory.AlignedAlloc(256, 64);

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr address, nuint size, uint type);

    public static bool Available => RuntimeInformation.ProcessArchitecture == Architecture.X64 && OperatingSystem.IsWindows();

    public byte[] Native(byte[] code, byte[] buffer)
    {
        if (code[^1] != 0xC3) throw new ArgumentException("Sequence must end with RET.");
        for (var i = 0; i < code.Length; i++) _code[i] = code[i];
        for (var i = 0; i < 256; i++) _buffer[i] = i < buffer.Length ? buffer[i] : (byte)0;
        ((delegate* unmanaged<byte*, void>)_code)(_buffer);
        var result = new byte[256];
        for (var i = 0; i < 256; i++) result[i] = _buffer[i];
        return result;
    }

    public static byte[] Emulated(byte[] code, byte[] buffer, out X86Cpu cpu)
    {
        var bus = new PhysicalBus();
        bus.MapRam("ram", 0, new byte[PhysicalBus.PageSize]);
        bus.WriteBytes(GuestCode, code);
        var padded = new byte[256];
        buffer.CopyTo(padded, 0);
        bus.WriteBytes(GuestBuffer, padded);
        cpu = new X86Cpu(bus) { Eip = GuestCode };
        cpu[X86Cpu.ECX] = GuestBuffer;
        cpu[X86Cpu.ESP] = 0x8000;
        var end = GuestCode + (uint)code.Length - 1; // stop before RET
        for (var steps = 0; cpu.Eip != end; steps++)
        {
            if (steps > 64) throw new InvalidOperationException("Sequence did not reach its RET.");
            cpu.Step();
        }
        return bus.ReadBytes(GuestBuffer, 256);
    }

    public void Dispose()
    {
        VirtualFree((IntPtr)_code, 0, 0x8000);
        NativeMemory.AlignedFree(_buffer);
    }

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
