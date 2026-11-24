using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using XboxPc.Emulation.OriginalXbox;

namespace XboxPc.DesktopApp.ViewModels;

/// <summary>
/// Facts read from an .xbe file without running it: title, title ID, sections, linked libraries and the kernel
/// exports it imports, checked against what the emulator implements. A title that imports a missing export will
/// stop when it calls it; this lists those exports before Play is pressed.
/// </summary>
public sealed record XbeInfo(uint TitleId, string TitleName, int SectionCount, string Libraries, IReadOnlyList<int> Imports, IReadOnlyList<string> MissingImports, long FileSize)
{
    public string TitleIdText => TitleId.ToString("X8", CultureInfo.InvariantCulture);
    public string ImportsText => MissingImports.Count == 0
        ? $"All {Imports.Count} kernel imports are implemented."
        : $"{Imports.Count - MissingImports.Count} of {Imports.Count} kernel imports are implemented. Missing: {string.Join(", ", MissingImports)}.";
    public bool AllImportsImplemented => MissingImports.Count == 0;
    public string SizeText => FileSize >= 1 << 20 ? $"{FileSize / 1048576.0:0.0} MB" : $"{FileSize / 1024.0:0} KB";

    public static XbeInfo Read(string path)
    {
        var file = File.ReadAllBytes(path);
        var image = XbeImage.Parse(file);
        var imports = new List<int>();
        if (RawOffset(image, image.KernelThunkAddress) is { } offset)
        {
            for (var at = offset; at + 4 <= file.Length && imports.Count <= 1024; at += 4)
            {
                var entry = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at, 4));
                if (entry == 0 || (entry & 0x8000_0000) == 0) break;
                imports.Add((int)(entry & 0x7FFF_FFFF));
            }
        }
        var missing = imports.Where(o => !KernelHle.ImplementedOrdinals.Contains(o)).Distinct().Select(KernelHle.NameOf).ToList();
        var libs = image.Libraries.Count == 0 ? "None listed" : string.Join(", ", image.Libraries.Select(l => $"{l.Name.TrimEnd('\0')} {l.Major}.{l.Minor}.{l.Build}"));
        return new XbeInfo(image.TitleId, image.TitleName, image.Sections.Count, libs, imports, missing, file.LongLength);
    }

    private static int? RawOffset(XbeImage image, uint va)
    {
        if (va >= image.BaseAddress && va - image.BaseAddress < image.SizeOfHeaders) return (int)(va - image.BaseAddress);
        foreach (var s in image.Sections)
            if (va >= s.VirtualAddress && va < s.VirtualAddress + s.RawSize)
                return (int)(s.RawAddress + (va - s.VirtualAddress));
        return null;
    }
}
