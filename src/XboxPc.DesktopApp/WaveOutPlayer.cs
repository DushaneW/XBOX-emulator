using System.Runtime.InteropServices;
using XboxPc.Emulation.Audio;

namespace XboxPc.DesktopApp;

/// <summary>
/// Plays the emulated AC97 PCM output (16-bit stereo, 48 kHz) through the Windows multimedia API (waveOut).
/// <see cref="Write"/> is called on the emulation thread; samples are grouped into 20 ms blocks and queued on a ring of
/// pre-allocated buffers. When every buffer is still queued (the emulator produced audio faster than real time) the
/// block is dropped and counted, never blocks the emulation thread. Underruns (emulator slower than real time) are
/// audible as gaps; <see cref="Dropped"/> and <see cref="BlocksPlayed"/> report what happened.
/// </summary>
public sealed class WaveOutPlayer : IAudioSink, IDisposable
{
    private const int BlockFrames = Ac97Controller.SampleRate / 50; // 20 ms
    private const int BlockBytes = BlockFrames * 4;
    private const int Blocks = 10;                                  // up to 200 ms queued

    private readonly object _lock = new();
    private readonly IntPtr _device;
    private readonly IntPtr[] _headers = new IntPtr[Blocks];
    private readonly IntPtr[] _data = new IntPtr[Blocks];
    private readonly bool[] _queued = new bool[Blocks];
    private readonly short[] _pending = new short[BlockFrames * 2];
    private int _pendingCount;
    private int _next;
    private bool _disposed;

    private WaveOutPlayer(IntPtr device)
    {
        _device = device;
        var headerSize = Marshal.SizeOf<NativeMethods.WaveHdr>();
        for (var i = 0; i < Blocks; i++)
        {
            _data[i] = Marshal.AllocHGlobal(BlockBytes);
            _headers[i] = Marshal.AllocHGlobal(headerSize);
            Marshal.StructureToPtr(new NativeMethods.WaveHdr { lpData = _data[i], dwBufferLength = BlockBytes }, _headers[i], false);
            if (NativeMethods.waveOutPrepareHeader(_device, _headers[i], (uint)headerSize) != 0)
                throw new InvalidOperationException("waveOutPrepareHeader failed");
        }
    }

    public bool Muted { get; set; }

    /// <summary>Output gain 0-1 applied to the emulated audio before it reaches the device.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>Device queue: blocks x block length.</summary>
    public static int BufferMilliseconds => Blocks * 20;
    public long BlocksPlayed { get; private set; }
    public long Dropped { get; private set; }

    /// <summary>Opens the default output device; returns null (with the reason) when no device is available.</summary>
    public static WaveOutPlayer? TryOpen(out string? error)
    {
        var format = new NativeMethods.WaveFormatEx
        {
            wFormatTag = 1, nChannels = 2, nSamplesPerSec = Ac97Controller.SampleRate, wBitsPerSample = 16, nBlockAlign = 4,
            nAvgBytesPerSec = Ac97Controller.SampleRate * 4,
        };
        try
        {
            var r = NativeMethods.waveOutOpen(out var device, NativeMethods.WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) { error = $"waveOutOpen failed (MMRESULT {r})"; return null; }
            error = null;
            return new WaveOutPlayer(device);
        }
        catch (DllNotFoundException e) { error = e.Message; return null; }
        catch (InvalidOperationException e) { error = e.Message; return null; }
    }

    public void Write(ReadOnlySpan<short> interleavedStereo)
    {
        lock (_lock)
        {
            if (_disposed) return;
            while (interleavedStereo.Length > 0)
            {
                var n = Math.Min(interleavedStereo.Length, _pending.Length - _pendingCount);
                interleavedStereo[..n].CopyTo(_pending.AsSpan(_pendingCount));
                _pendingCount += n;
                interleavedStereo = interleavedStereo[n..];
                if (_pendingCount == _pending.Length) Submit();
            }
        }
    }

    private void Submit()
    {
        _pendingCount = 0;
        var i = _next;
        if (_queued[i] && (Flags(i) & NativeMethods.WhdrDone) == 0)
        {
            Dropped++; // every buffer still playing: the emulator is ahead of the sound card
            return;
        }
        if (Muted) Array.Clear(_pending);
        else if (Volume < 0.999)
        {
            var gain = Math.Clamp(Volume, 0, 1);
            for (var k = 0; k < _pending.Length; k++) _pending[k] = (short)(_pending[k] * gain);
        }
        Marshal.Copy(_pending, 0, _data[i], _pending.Length);
        if (NativeMethods.waveOutWrite(_device, _headers[i], (uint)Marshal.SizeOf<NativeMethods.WaveHdr>()) != 0)
        {
            Dropped++;
            return;
        }
        _queued[i] = true;
        _next = (i + 1) % Blocks;
        BlocksPlayed++;
    }

    private uint Flags(int i) => (uint)Marshal.ReadInt32(_headers[i], (int)Marshal.OffsetOf<NativeMethods.WaveHdr>(nameof(NativeMethods.WaveHdr.dwFlags)));

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _ = NativeMethods.waveOutReset(_device);
            var headerSize = (uint)Marshal.SizeOf<NativeMethods.WaveHdr>();
            for (var i = 0; i < Blocks; i++)
            {
                _ = NativeMethods.waveOutUnprepareHeader(_device, _headers[i], headerSize);
                Marshal.FreeHGlobal(_headers[i]);
                Marshal.FreeHGlobal(_data[i]);
            }
            _ = NativeMethods.waveOutClose(_device);
        }
    }

    private static class NativeMethods
    {
        internal const uint WaveMapper = 0xFFFF_FFFF;
        internal const uint WhdrDone = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        internal struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WaveHdr
        {
            public IntPtr lpData;
            public int dwBufferLength;
            public int dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);

        [DllImport("winmm.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint waveOutWrite(IntPtr device, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint waveOutReset(IntPtr device);

        [DllImport("winmm.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint waveOutClose(IntPtr device);
    }
}
