using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using XboxPc.Domain;

namespace XboxPc.Performance;

/// <summary>A measured value, or the reason it is unavailable. Never a guessed number.</summary>
public readonly record struct Reading(double? Value, string? UnavailableReason)
{
    public static Reading Of(double value) => new(value, null);
    public static Reading Unavailable(string reason) => new(null, reason);
    public bool HasValue => Value is not null;
}

/// <summary>CPU and memory usage via documented Win32 APIs (GetSystemTimes, GlobalMemoryStatusEx).</summary>
public sealed class SystemUsageSampler
{
    private ulong _lastIdle, _lastKernel, _lastUser;
    private TimeSpan _lastProcessCpu;
    private long _lastTimestamp;
    private bool _primed;

    public (Reading TotalCpuPercent, Reading ProcessCpuPercent) SampleCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return (Reading.Unavailable($"GetSystemTimes failed (error {Marshal.GetLastPInvokeError()})."), Reading.Unavailable("CPU times unavailable."));

        using var process = Process.GetCurrentProcess();
        var processCpu = process.TotalProcessorTime;
        var now = Stopwatch.GetTimestamp();

        if (!_primed)
        {
            (_lastIdle, _lastKernel, _lastUser, _lastProcessCpu, _lastTimestamp, _primed) = (idle.Value, kernel.Value, user.Value, processCpu, now, true);
            return (Reading.Unavailable("Collecting first sample..."), Reading.Unavailable("Collecting first sample..."));
        }

        var idleDelta = idle.Value - _lastIdle;
        var totalDelta = (kernel.Value - _lastKernel) + (user.Value - _lastUser); // kernel time includes idle
        var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now);
        var processDelta = processCpu - _lastProcessCpu;
        (_lastIdle, _lastKernel, _lastUser, _lastProcessCpu, _lastTimestamp) = (idle.Value, kernel.Value, user.Value, processCpu, now);

        var total = totalDelta == 0 ? Reading.Unavailable("No CPU time elapsed.") : Reading.Of(Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100));
        var proc = elapsed <= TimeSpan.Zero ? Reading.Unavailable("No time elapsed.")
            : Reading.Of(Math.Clamp(100.0 * processDelta.TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount), 0, 100));
        return (total, proc);
    }

    public static (Reading UsedPercent, Reading TotalGb, Reading ProcessWorkingSetMb) SampleMemory()
    {
        var status = new NativeMethods.MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        using var process = Process.GetCurrentProcess();
        var ws = Reading.Of(process.WorkingSet64 / 1024.0 / 1024.0);
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            return (Reading.Unavailable("GlobalMemoryStatusEx failed."), Reading.Unavailable("GlobalMemoryStatusEx failed."), ws);
        return (Reading.Of(status.dwMemoryLoad), Reading.Of(status.ullTotalPhys / 1024.0 / 1024.0 / 1024.0), ws);
    }
}

/// <summary>
/// GPU utilization and dedicated memory via the documented PDH "GPU Engine" / "GPU Adapter Memory" counters
/// (Windows 10 1709+ with WDDM 2.x drivers). Reports Unavailable when the counters are missing.
/// </summary>
public sealed class GpuCounterSampler : IDisposable
{
    private IntPtr _query;
    private IntPtr _util3D;
    private IntPtr _dedicated;
    private readonly string? _initError;
    private bool _primed;

    public GpuCounterSampler()
    {
        try
        {
            if (NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _initError = "PDH query could not be opened."; return; }
            if (NativeMethods.PdhAddEnglishCounterW(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _util3D) != 0) _util3D = IntPtr.Zero;
            if (NativeMethods.PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _dedicated) != 0) _dedicated = IntPtr.Zero;
            if (_util3D == IntPtr.Zero && _dedicated == IntPtr.Zero) _initError = "GPU performance counters are not available on this system/driver.";
        }
        catch (DllNotFoundException) { _initError = "pdh.dll is not available."; }
    }

    /// <summary>3D engine utilization (sum across processes, max across adapters) and dedicated VRAM in use (sum across adapters).</summary>
    public (Reading Gpu3DPercent, Reading DedicatedMemoryMb) Sample()
    {
        if (_initError is not null) return (Reading.Unavailable(_initError), Reading.Unavailable(_initError));
        if (NativeMethods.PdhCollectQueryData(_query) != 0) return (Reading.Unavailable("PDH collection failed."), Reading.Unavailable("PDH collection failed."));
        if (!_primed) { _primed = true; return (Reading.Unavailable("Collecting first sample..."), ReadDedicated()); }

        Reading util;
        if (_util3D == IntPtr.Zero) util = Reading.Unavailable("GPU Engine counter not available.");
        else
        {
            var items = ReadArray(_util3D);
            if (items is null) util = Reading.Unavailable("GPU Engine counter returned no data.");
            else
            {
                // Instance names look like "pid_1234_luid_0x..._0x..._phys_0_eng_0_engtype_3D". Group by adapter LUID.
                var perAdapter = items.GroupBy(i => AdapterKey(i.Name)).Select(g => g.Sum(i => i.Value));
                util = Reading.Of(Math.Clamp(perAdapter.DefaultIfEmpty(0).Max(), 0, 100));
            }
        }
        return (util, ReadDedicated());
    }

    private Reading ReadDedicated()
    {
        if (_dedicated == IntPtr.Zero) return Reading.Unavailable("GPU Adapter Memory counter not available.");
        var items = ReadArray(_dedicated);
        return items is null ? Reading.Unavailable("GPU Adapter Memory counter returned no data.") : Reading.Of(items.Sum(i => i.Value) / 1024.0 / 1024.0);
    }

    private static string AdapterKey(string instance)
    {
        var start = instance.IndexOf("luid_", StringComparison.Ordinal);
        if (start < 0) return instance;
        var end = instance.IndexOf("_phys", start, StringComparison.Ordinal);
        return end < 0 ? instance[start..] : instance[start..end];
    }

    private static List<(string Name, double Value)>? ReadArray(IntPtr counter)
    {
        uint size = 0;
        var status = NativeMethods.PdhGetFormattedCounterArrayW(counter, NativeMethods.PdhFmtDouble, ref size, out _, IntPtr.Zero);
        if (status != NativeMethods.PdhMoreData || size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = NativeMethods.PdhGetFormattedCounterArrayW(counter, NativeMethods.PdhFmtDouble, ref size, out var count, buffer);
            if (status != 0) return null;
            var list = new List<(string, double)>((int)count);
            // PDH_FMT_COUNTERVALUE_ITEM_W: { LPWSTR szName; PDH_FMT_COUNTERVALUE FmtValue; }, 8 + 16 bytes on x64.
            var itemSize = IntPtr.Size + 16;
            for (var i = 0; i < count; i++)
            {
                var itemPtr = buffer + (i * itemSize);
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(itemPtr)) ?? "";
                var value = Marshal.PtrToStructure<NativeMethods.PdhFmtCounterValue>(itemPtr + IntPtr.Size);
                if (value.CStatus is 0 or 1) list.Add((name, value.DoubleValue)); // PDH_CSTATUS_VALID_DATA / NEW_DATA
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { _ = NativeMethods.PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }
}

public sealed record DisplayInfo(string DeviceName, string AdapterName, int Width, int Height, int RefreshHz, int BitsPerPixel, bool IsPrimary);

public static class DisplayInfoProvider
{
    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var list = new List<DisplayInfo>();
        for (uint i = 0; i < 32; i++)
        {
            var dev = new NativeMethods.DisplayDevice { cb = Marshal.SizeOf<NativeMethods.DisplayDevice>() };
            if (!NativeMethods.EnumDisplayDevicesW(null, i, ref dev, 0)) break;
            if ((dev.StateFlags & NativeMethods.DisplayDeviceAttachedToDesktop) == 0) continue;
            var mode = new NativeMethods.DevMode { dmSize = (short)Marshal.SizeOf<NativeMethods.DevMode>() };
            if (!NativeMethods.EnumDisplaySettingsW(dev.DeviceName, NativeMethods.EnumCurrentSettings, ref mode)) continue;
            list.Add(new DisplayInfo(dev.DeviceName, dev.DeviceString, mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency, mode.dmBitsPerPel,
                (dev.StateFlags & NativeMethods.DisplayDevicePrimaryDevice) != 0));
        }
        return list;
    }

    /// <summary>Distinct display adapter names (all adapters, attached or not).</summary>
    public static IReadOnlyList<string> GetAdapterNames()
    {
        var names = new List<string>();
        for (uint i = 0; i < 32; i++)
        {
            var dev = new NativeMethods.DisplayDevice { cb = Marshal.SizeOf<NativeMethods.DisplayDevice>() };
            if (!NativeMethods.EnumDisplayDevicesW(null, i, ref dev, 0)) break;
            if (!string.IsNullOrWhiteSpace(dev.DeviceString) && !names.Contains(dev.DeviceString)) names.Add(dev.DeviceString);
        }
        return names;
    }
}

public static class HostInfoProvider
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static HostInfo Collect()
    {
        var os = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        var cpu = ReadRegistry(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim() ?? "Unknown CPU";
        cpu += $", {Environment.ProcessorCount} logical processors";
        var (_, totalGb, _) = SystemUsageSampler.SampleMemory();
        var ram = totalGb.Value is { } gb ? $"{gb:F1} GB" : "Unknown";

        var gpus = new List<string>();
        var drivers = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            foreach (var sub in cls?.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)) ?? [])
            {
                using var key = cls!.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is string desc)
                {
                    gpus.Add(desc);
                    drivers.Add($"{desc}: {key.GetValue("DriverVersion") as string ?? "unknown"}");
                }
            }
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        if (gpus.Count == 0) gpus.AddRange(DisplayInfoProvider.GetAdapterNames());

        return new HostInfo(os, cpu, gpus.Count > 0 ? string.Join("; ", gpus.Distinct()) : "Unknown GPU", ram,
            drivers.Count > 0 ? string.Join("; ", drivers.Distinct()) : "Unknown");
    }

    private static string? ReadRegistry(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (System.Security.SecurityException) { return null; }
    }
}

/// <summary>Temperature/clock sensors: deliberately not read. There is no documented vendor-neutral Windows API.</summary>
public static class SensorSupport
{
    public const string TemperatureUnavailableReason =
        "Not available: Windows has no documented, vendor-neutral API for CPU/GPU temperatures. Vendor SDK integration is a future, capability-checked feature.";
}
