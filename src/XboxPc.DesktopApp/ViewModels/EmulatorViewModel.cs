using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using XboxPc.Application;
using XboxPc.Domain;
using XboxPc.Emulation.OriginalXbox;
using XboxPc.Runtime.Abstractions;

namespace XboxPc.DesktopApp.ViewModels;

public sealed record RuntimeRow(RuntimeDescriptor Descriptor, ICommand OpenDocsCommand)
{
    public string Name => Descriptor.DisplayName;
    public string Generation => Descriptor.Generation.ToDisplay();
    public string State => RuntimeCard.StateLabel(Descriptor.State);
    public bool IsRunnable => Descriptor.State is ImplementationState.Available or ImplementationState.Experimental;
    public string Version => $"v{Descriptor.Version} · contract v{Descriptor.ContractVersion}";
    public string Capabilities => Descriptor.Capabilities.Count == 0 ? "No capabilities" : string.Join(", ", Descriptor.Capabilities.Order());
    public string License => Descriptor.LicenseNotice;
    public string Summary => Descriptor.Summary;
    public bool HasDocs => Descriptor.DocumentationPath is not null;
}

public sealed record LogRow(string Time, string Level, string Category, string Message);

/// <summary>One hardware block of the emulated machine and what is actually modelled.</summary>
public sealed record HardwareRow(string Name, string Status, string Detail, bool Implemented);

/// <summary>
/// Emulator page: what the Original Xbox core is, what it emulates and what it does not, firmware/kernel status,
/// the other registered runtimes and the recent log.
/// </summary>
public sealed class EmulatorViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public EmulatorViewModel(MainViewModel main)
    {
        _main = main;
        var openDoc = new RelayCommand<RuntimeDescriptor>(OpenDoc);
        Runtimes = new ObservableCollection<RuntimeRow>(main.Host.Runtimes.Descriptors.Select(d => new RuntimeRow(d, openDoc)));
        Core = Runtimes.FirstOrDefault(r => r.Descriptor.Generation == ConsoleGeneration.OriginalXbox);
        RefreshLogCommand = new RelayCommand(Refresh);
        OpenLogsCommand = new RelayCommand(() => _main.Host.Dialogs.OpenFolder(_main.Host.LogDirectory));
        OpenDataCommand = new RelayCommand(() => _main.Host.Dialogs.OpenFolder(_main.Host.DataDirectory));
        OpenFeasibilityCommand = new RelayCommand(() => OpenDocPath("docs/phase0/SERIES_XS_FEASIBILITY.md"));
        OpenCoreDocsCommand = new RelayCommand(() => { if (Core?.Descriptor is { } d) OpenDoc(d); });
        RunXbeCommand = new RelayCommand(_main.RunXbe);
    }

    public string Version => typeof(EmulatorViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public string ContractVersion => $"Runtime contract v{RuntimeContract.Current}";
    public string DataDirectory => GameEditorViewModel.ShortenProfile(_main.Host.DataDirectory);
    public RuntimeRow? Core { get; }
    public string CoreVersion => Core is null ? "Not registered" : $"{Core.Descriptor.Version}";
    public string CoreState => Core?.State ?? "Not registered";
    public ObservableCollection<RuntimeRow> Runtimes { get; }
    public IEnumerable<RuntimeRow> OtherRuntimes => Runtimes.Where(r => r != Core);
    public ObservableCollection<LogRow> RecentLog { get; } = [];

    public static int KernelImplemented => KernelHle.ImplementedOrdinals.Count;
    public static int KernelTotal => KernelExports.ByOrdinal.Count;
    public static string KernelText => $"{KernelImplemented} of {KernelTotal} exports";
    public static double KernelFraction => KernelTotal == 0 ? 0 : (double)KernelImplemented / KernelTotal;

    public static string FirmwareText =>
        "Not used. The Xbox kernel is emulated at a high level, so no BIOS, kernel or firmware image is needed, loaded or downloaded.";

    public static string EepromText => "Blank emulated EEPROM: no console keys, serial or settings from real hardware.";

    public static string CompatibilityText =>
        "Homebrew programs built with nxdk run, including the bundled 2D, 3D, texture, lighting and blending test programs. " +
        "Commercial games have not been tested to boot and are not expected to yet.";

    public static IReadOnlyList<HardwareRow> Hardware { get; } =
    [
        new("CPU", "Interpreter", "Pentium III-class IA-32 instruction interpreter, paced to real time.", true),
        new("Kernel", "High-level emulation", $"{KernelText}. A program that calls a missing one stops and names it.", true),
        new("Graphics (NV2A)", "Experimental", "Command FIFO, 3D (Kelvin) and 2D engines. Rendered with Direct3D 12 at up to 6x resolution, or by the CPU reference renderer.", true),
        new("Audio", "AC97 output", "PCM and S/PDIF output through the Windows default device. The APU (hardware DirectSound) is not emulated.", true),
        new("Controllers", "USB, up to 4", "OHCI host with Xbox controllers; keyboard, mouse and XInput pads map onto them. Rumble goes back to XInput.", true),
        new("Storage", "Host folders", "D: is the program's own folder (read-only); E: is a private folder per title under your data folder.", true),
        new("Disc drive", "Not emulated", "Disc images are not supported.", false),
        new("Network", "Not emulated", "No network adapter; nothing connects to any service.", false),
    ];

    public ICommand RefreshLogCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenDataCommand { get; }
    public ICommand OpenFeasibilityCommand { get; }
    public ICommand OpenCoreDocsCommand { get; }
    public ICommand RunXbeCommand { get; }

    public static string Statement =>
        "XBOX PC is an experimental Original Xbox emulator and game library for Windows. It runs Original Xbox .xbe programs " +
        "(homebrew today) in its built-in core. Xbox 360, Xbox One and Xbox Series X|S software cannot run. " +
        "No DRM, security or anti-cheat protection is bypassed, no firmware or games are downloaded, and no account passwords are collected.";

    public void Refresh()
    {
        RecentLog.Clear();
        foreach (var e in _main.Host.Log.Recent.Reverse().Take(200))
            RecentLog.Add(new LogRow(e.Timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture), e.Level.ToString(), e.Category, e.Message));
    }

    private void OpenDoc(RuntimeDescriptor? d)
    {
        if (d?.DocumentationPath is { } p) OpenDocPath(p);
    }

    private void OpenDocPath(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(AppHost.BaseDirectory, relative));
        if (!SafePath.IsWithin(AppHost.BaseDirectory, relative) || !File.Exists(path))
        {
            _main.Host.Dialogs.ShowError("Document not found", $"The document is not bundled with this build: {relative}");
            return;
        }
        _main.Host.Dialogs.OpenFile(path);
    }
}
