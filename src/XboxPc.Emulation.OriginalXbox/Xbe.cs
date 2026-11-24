using System.Buffers.Binary;
using System.Text;

namespace XboxPc.Emulation.OriginalXbox;

public sealed class XbeFormatException(string message) : Exception(message)
{
    public XbeFormatException() : this("Invalid XBE.") { }

    public XbeFormatException(string message, Exception inner) : this(message + " " + inner.Message) { }
}

public enum XbeKeyKind
{
    Retail,
    Debug,
}

public sealed record XbeSection(string Name, uint Flags, uint VirtualAddress, uint VirtualSize, uint RawAddress, uint RawSize)
{
    public bool IsExecutable => (Flags & 0x4) != 0;
    public bool IsWritable => (Flags & 0x1) != 0;
}

public sealed record XbeLibrary(string Name, ushort Major, ushort Minor, ushort Build);

/// <summary>
/// Parser for the Original Xbox executable format (XBE). Layout per the public format description
/// (xboxdevwiki.net "Xbe"). The digital signature is not checked: the emulator neither needs nor bypasses it.
/// Input is treated as untrusted: every offset and size is bounds-checked.
/// </summary>
public sealed class XbeImage
{
    public const uint Magic = 0x48454258; // "XBEH"
    public const int MinimumHeaderSize = 0x178;
    public const int MaxSections = 256;
    public const int SectionHeaderSize = 0x38;

    // Entry-point / kernel-thunk obfuscation constants from the public XBE format description.
    public const uint EntryXorRetail = 0xA8FC57AB, EntryXorDebug = 0x94859D4B;
    public const uint ThunkXorRetail = 0x5B6D40B6, ThunkXorDebug = 0xEFB1F152;

    public required uint BaseAddress { get; init; }
    public required uint SizeOfHeaders { get; init; }
    public required uint SizeOfImage { get; init; }
    public required uint EntryPoint { get; init; }
    public required uint KernelThunkAddress { get; init; }
    public required XbeKeyKind KeyKind { get; init; }
    public required uint TitleId { get; init; }
    public required string TitleName { get; init; }
    public required IReadOnlyList<XbeSection> Sections { get; init; }
    public required IReadOnlyList<XbeLibrary> Libraries { get; init; }
    public required uint TlsAddress { get; init; }
    public required uint StackCommit { get; init; }

    public static XbeImage Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < MinimumHeaderSize) throw new XbeFormatException($"File is {file.Length} bytes; an XBE header needs at least {MinimumHeaderSize}.");
        if (U32(file, 0) != Magic) throw new XbeFormatException("Missing 'XBEH' magic.");

        var baseAddress = U32(file, 0x104);
        var sizeOfHeaders = U32(file, 0x108);
        var sizeOfImage = U32(file, 0x10C);
        if (sizeOfHeaders < MinimumHeaderSize || sizeOfHeaders > file.Length) throw new XbeFormatException($"SizeOfHeaders 0x{sizeOfHeaders:X} is outside the file.");
        if (sizeOfImage < sizeOfHeaders || (ulong)baseAddress + sizeOfImage > 0x8000_0000UL) throw new XbeFormatException($"Image 0x{baseAddress:X8}+0x{sizeOfImage:X} is not a valid user-space range.");

        var headers = file[..(int)sizeOfHeaders];

        int HeaderOffset(uint va, int length, string what)
        {
            if (va < baseAddress || (ulong)va - baseAddress + (ulong)length > sizeOfHeaders)
                throw new XbeFormatException($"{what} at 0x{va:X8} is outside the XBE headers.");
            return (int)(va - baseAddress);
        }

        var sectionCount = U32(file, 0x11C);
        if (sectionCount > MaxSections) throw new XbeFormatException($"{sectionCount} sections; limit is {MaxSections}.");
        var sectionTable = HeaderOffset(U32(file, 0x120), (int)sectionCount * SectionHeaderSize, "Section table");

        var sections = new List<XbeSection>((int)sectionCount);
        for (var i = 0; i < sectionCount; i++)
        {
            var h = headers.Slice(sectionTable + (i * SectionHeaderSize), SectionHeaderSize);
            var flags = U32(h, 0x00);
            var va = U32(h, 0x04);
            var vsize = U32(h, 0x08);
            var raw = U32(h, 0x0C);
            var rawSize = U32(h, 0x10);
            var name = ReadAscii(headers, HeaderOffset(U32(h, 0x14), 1, $"Section {i} name"), 64);
            if ((ulong)raw + rawSize > (ulong)file.Length) throw new XbeFormatException($"Section '{name}' raw data 0x{raw:X}+0x{rawSize:X} exceeds the file.");
            if (rawSize > vsize && vsize != 0) throw new XbeFormatException($"Section '{name}' raw size exceeds its virtual size.");
            if (va < baseAddress || (ulong)va + vsize > (ulong)baseAddress + sizeOfImage) throw new XbeFormatException($"Section '{name}' 0x{va:X8}+0x{vsize:X} lies outside the image.");
            sections.Add(new XbeSection(name, flags, va, vsize, raw, rawSize));
        }

        var encodedEntry = U32(file, 0x128);
        var encodedThunk = U32(file, 0x158);
        bool InCode(uint va) => sections.Any(s => va >= s.VirtualAddress && va < s.VirtualAddress + s.VirtualSize);
        XbeKeyKind kind;
        if (InCode(encodedEntry ^ EntryXorRetail)) kind = XbeKeyKind.Retail;
        else if (InCode(encodedEntry ^ EntryXorDebug)) kind = XbeKeyKind.Debug;
        else throw new XbeFormatException("Entry point does not decode into any section with either the retail or debug constant.");
        var entry = encodedEntry ^ (kind == XbeKeyKind.Retail ? EntryXorRetail : EntryXorDebug);
        var thunk = encodedThunk ^ (kind == XbeKeyKind.Retail ? ThunkXorRetail : ThunkXorDebug);
        if (!InCode(thunk)) throw new XbeFormatException($"Kernel thunk table 0x{thunk:X8} is not inside any section.");

        uint titleId = 0;
        var titleName = "";
        var certVa = U32(file, 0x118);
        if (certVa != 0)
        {
            var cert = HeaderOffset(certVa, 0x0C + 80, "Certificate");
            titleId = U32(headers, cert + 0x08);
            titleName = Encoding.Unicode.GetString(headers.Slice(cert + 0x0C, 80)).Split('\0')[0];
        }

        var libraries = new List<XbeLibrary>();
        var libCount = U32(file, 0x160);
        if (libCount > 64) throw new XbeFormatException($"{libCount} library versions; limit is 64.");
        if (libCount > 0)
        {
            var libs = HeaderOffset(U32(file, 0x164), (int)libCount * 16, "Library version table");
            for (var i = 0; i < libCount; i++)
            {
                var l = headers.Slice(libs + (i * 16), 16);
                libraries.Add(new XbeLibrary(ReadAscii(l, 0, 8), U16(l, 8), U16(l, 10), U16(l, 12)));
            }
        }

        return new XbeImage
        {
            BaseAddress = baseAddress,
            SizeOfHeaders = sizeOfHeaders,
            SizeOfImage = sizeOfImage,
            EntryPoint = entry,
            KernelThunkAddress = thunk,
            KeyKind = kind,
            TitleId = titleId,
            TitleName = titleName,
            Sections = sections,
            Libraries = libraries,
            TlsAddress = U32(file, 0x12C),
            StackCommit = U32(file, 0x130),
        };
    }

    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o, 4));
    private static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o, 2));

    private static string ReadAscii(ReadOnlySpan<byte> s, int offset, int max)
    {
        var sb = new StringBuilder();
        for (var i = offset; i < s.Length && i < offset + max && s[i] != 0; i++) sb.Append(s[i] is >= 0x20 and < 0x7F ? (char)s[i] : '?');
        return sb.ToString();
    }
}
