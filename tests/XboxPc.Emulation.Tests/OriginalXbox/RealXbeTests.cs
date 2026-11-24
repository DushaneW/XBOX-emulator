using System.Security.Cryptography;
using XboxPc.Emulation.OriginalXbox;
using ThreadState = XboxPc.Emulation.OriginalXbox.ThreadState;
using Xunit.Abstractions;

namespace XboxPc.Emulation.Tests.OriginalXbox;

/// <summary>
/// End-to-end run of a genuine XBE: homebrew/xpc-probe (original source in this repository) built with the open-source
/// nxdk toolchain (commit fafbe41, clang/lld 23.1.5). Expected output comes from the same C source compiled natively
/// for the host PC (homebrew/xpc-probe/build.sh), an oracle independent of the emulator.
/// </summary>
public class RealXbeTests(ITestOutputHelper output)
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    [Fact]
    public void Asset_is_the_recorded_build()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Asset("xpc-probe.xbe"))));
        Assert.Equal(File.ReadAllText(Asset("xpc-probe.xbe.sha256")).Trim(), hash);
    }

    [Fact]
    public void Nxdk_built_probe_runs_from_entry_to_exit_and_matches_host_oracle()
    {
        var file = File.ReadAllBytes(Asset("xpc-probe.xbe"));
        var xbe = XbeImage.Parse(file);
        Assert.Equal("XPC Probe", xbe.TitleName);

        var machine = new OriginalXboxMachine();
        machine.Load(xbe, file);
        var result = machine.Run(50_000_000);

        output.WriteLine($"{result.Stop}: {result.Detail}; {result.Instructions:N0} instructions; {machine.Kernel.Calls} kernel calls");
        Assert.Equal(MachineStop.TitleExited, result.Stop);
        Assert.Contains("HalReturnToFirmware(2)", result.Detail, StringComparison.Ordinal); // HalQuickRebootRoutine, from nxdk's _PDCLIB_Exit

        var expected = File.ReadAllText(Asset("xpc-probe.expected.txt")).Replace("\r\n", "\n", StringComparison.Ordinal); // host CRT text mode
        Assert.Equal(expected, machine.Kernel.DebugOutput);

        // Execution structure that follows from nxdk's crt0: entry thread exits, main runs on a second thread.
        Assert.Equal(2, machine.Threads.Count);
        Assert.Equal(ThreadState.Terminated, machine.Threads[0].State);
        Assert.Equal(@"\Device\CdRom0\", machine.Kernel.SymbolicLinks[@"\??\D:"]); // nxdk automount of D:
        // The only unmodelled hardware the probe touches is the PCI read in nxdk's TSC-frequency probe.
        var unmodelled = Assert.Single(machine.UnmodelledAccesses.Distinct());
        Assert.Contains("register 0x6C", unmodelled, StringComparison.Ordinal);
    }
}
