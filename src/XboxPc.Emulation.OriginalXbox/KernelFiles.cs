using System.Text;
using XboxPc.Emulation.Storage;

namespace XboxPc.Emulation.OriginalXbox;

/// <summary>An open file/directory handle. I/O is synchronous, so the object is always signaled.</summary>
public sealed class FileObject(StorageFile file) : DispatcherObject
{
    public StorageFile File { get; } = file;
    public override string Kind => File.IsDirectory ? "Directory" : "File";
    public override bool IsSignaledFor(GuestThread thread) => true;
    public override void Acquire(GuestThread thread) { }
}

/// <summary>
/// File-system kernel exports over <see cref="VirtualStorage"/>. Structure layouts from nxdk xboxkrnl.h:
/// OBJECT_ATTRIBUTES { RootDirectory, ObjectName (PANSI_STRING), Attributes }, IO_STATUS_BLOCK { Status, Information },
/// FILE_DIRECTORY_INFORMATION with 8-bit (OCHAR) names. All I/O is synchronous; APC completion is not supported.
/// </summary>
public sealed partial class KernelHle
{
    private const int FileDirectoryInformation = 1, FileBasicInformation = 4, FileStandardInformation = 5, FileRenameInformation = 10;
    private const int FileDispositionInformation = 13, FilePositionInformation = 14, FileAllocationInformation = 19, FileEndOfFileInformation = 20;
    private const int FileNetworkOpenInformation = 34, FileFsSizeInformation = 3;
    private const uint WriteAccessMask = 0x2 | 0x4 | 0x40000000 | 0x10000000;
    private const uint FileDirectoryFile = 0x1, FileNonDirectoryFile = 0x40, FileDeleteOnClose = 0x1000;

    private void RegisterFileExports()
    {
        _handlers[190] = NtCreateFile;
        _handlers[202] = NtOpenFile;
        _handlers[219] = NtReadFile;
        _handlers[236] = NtWriteFile;
        _handlers[211] = NtQueryInformationFile;
        _handlers[226] = NtSetInformationFile;
        _handlers[210] = NtQueryFullAttributesFile;
        _handlers[207] = NtQueryDirectoryFile;
        _handlers[218] = NtQueryVolumeInformationFile;
        _handlers[198] = NtFlushBuffersFile;
        _handlers[195] = NtDeleteFile;
    }

    private void WriteIoStatus(uint iosb, uint status, uint information)
    {
        if (iosb == 0) return;
        Bus.Write32(iosb, status);
        Bus.Write32(iosb + 4, information);
    }

    /// <summary>Builds the NT path for OBJECT_ATTRIBUTES, applying RootDirectory and \??\ symbolic links.</summary>
    private (string? Path, uint Status) ObjectPath(uint objectAttributes)
    {
        var root = Bus.Read32(objectAttributes);
        var name = ReadAnsiString(Bus.Read32(objectAttributes + 4));
        if (root == 0xFFFF_FFFD)
        {
            // ObDosDevicesDirectory() = (HANDLE)-3: name is relative to the \?? directory (drive letters).
            name = @"\??\" + name.TrimStart('\\');
        }
        else if (root == 0xFFFF_FFFC)
        {
            throw Unsupported("ObWin32NamedObjectsDirectory()-relative names (named objects are not emulated)");
        }
        else if (root != 0)
        {
            if (Lookup(root) is not FileObject { File.IsDirectory: true } dir) return (null, NtStatus.InvalidHandle);
            name = dir.File.NtPath.TrimEnd('\\') + "\\" + name.TrimStart('\\');
        }
        if (name.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            var link = _symbolicLinks.Keys.Where(k => name.Equals(k, StringComparison.OrdinalIgnoreCase) || name.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase))
                                          .OrderByDescending(k => k.Length).FirstOrDefault();
            if (link is null) return (null, FsStatus.ObjectPathNotFound);
            name = _symbolicLinks[link].TrimEnd('\\') + name[link.Length..];
        }
        return (name, NtStatus.Success);
    }

    private void Open(uint handleOut, uint access, uint oa, uint iosb, CreateDisposition disposition, uint options)
    {
        var (path, status) = ObjectPath(oa);
        if (path is null) { WriteIoStatus(iosb, status, 0); Return(status); return; }
        var (file, result, st) = _m.Storage.Open(path, (access & WriteAccessMask) != 0, disposition,
            (options & FileDirectoryFile) != 0, (options & FileNonDirectoryFile) != 0, (options & FileDeleteOnClose) != 0);
        if (file is null)
        {
            // IO_STATUS_BLOCK.Information for a failed open: FILE_EXISTS (4) on collision, FILE_DOES_NOT_EXIST (5) otherwise.
            WriteIoStatus(iosb, st, st == FsStatus.ObjectNameCollision ? 4u : 5u);
            Return(st);
            return;
        }
        Bus.Write32(handleOut, NewHandle(new FileObject(file)));
        WriteIoStatus(iosb, NtStatus.Success, (uint)result);
        _m.FileActivity.Add($"open {path} ({result})");
        Return(NtStatus.Success);
    }

    // NtCreateFile(FileHandle, DesiredAccess, ObjectAttributes, IoStatusBlock, AllocationSize, FileAttributes, ShareAccess, CreateDisposition, CreateOptions)
    private void NtCreateFile() => Open(Arg(0), Arg(1), Arg(2), Arg(3), (CreateDisposition)Arg(7), Arg(8));

    // NtOpenFile(FileHandle, DesiredAccess, ObjectAttributes, IoStatusBlock, ShareAccess, OpenOptions)
    private void NtOpenFile() => Open(Arg(0), Arg(1), Arg(2), Arg(3), CreateDisposition.Open, Arg(5));

    private FileObject? FileFor(uint handle, out uint status)
    {
        var o = Lookup(handle);
        status = o is null ? NtStatus.InvalidHandle : o is FileObject ? NtStatus.Success : NtStatus.ObjectTypeMismatch;
        return o as FileObject;
    }

    private long OffsetArg(uint pointer, FileObject f)
    {
        if (pointer == 0) return f.File.Position;
        var low = Bus.Read32(pointer);
        var high = Bus.Read32(pointer + 4);
        if (high == 0xFFFF_FFFF && low == 0xFFFF_FFFE) return f.File.Position; // FILE_USE_FILE_POINTER_POSITION
        return (long)(((ulong)high << 32) | low);
    }

    private void CompleteIo(uint eventHandle, uint iosb, uint status, uint information)
    {
        WriteIoStatus(iosb, status, information);
        if (eventHandle != 0 && Lookup(eventHandle) is GuestEvent e) e.Signaled = true;
        Return(status);
    }

    // NtReadFile(FileHandle, Event, ApcRoutine, ApcContext, IoStatusBlock, Buffer, Length, ByteOffset)
    private void NtReadFile()
    {
        var f = FileFor(Arg(0), out var status);
        if (f is null) { Return(status); return; }
        if (Arg(2) != 0) throw Unsupported("NtReadFile with an APC completion routine");
        var length = (int)Math.Min(Arg(6), 64u * 1024 * 1024);
        var buffer = new byte[length];
        var (read, st) = f.File.Read(buffer, OffsetArg(Arg(7), f));
        if (st == NtStatus.Success) Bus.WriteBytes(Arg(5), buffer.AsSpan(0, read));
        CompleteIo(Arg(1), Arg(4), st, (uint)read);
    }

    // NtWriteFile(FileHandle, Event, ApcRoutine, ApcContext, IoStatusBlock, Buffer, Length, ByteOffset)
    private void NtWriteFile()
    {
        var f = FileFor(Arg(0), out var status);
        if (f is null) { Return(status); return; }
        if (Arg(2) != 0) throw Unsupported("NtWriteFile with an APC completion routine");
        var data = Bus.ReadBytes(Arg(5), (int)Math.Min(Arg(6), 64u * 1024 * 1024));
        var (written, st) = f.File.Write(data, OffsetArg(Arg(7), f));
        CompleteIo(Arg(1), Arg(4), st, (uint)written);
    }

    private void WriteTimes(uint p, FileMetadata m)
    {
        WriteI64(p, m.CreationTime);
        WriteI64(p + 8, m.LastAccessTime);
        WriteI64(p + 16, m.LastWriteTime);
        WriteI64(p + 24, m.ChangeTime);
    }

    private void WriteI64(uint p, long v)
    {
        Bus.Write32(p, (uint)v);
        Bus.Write32(p + 4, (uint)((ulong)v >> 32));
    }

    private long ReadI64(uint p) => (long)(Bus.Read32(p) | ((ulong)Bus.Read32(p + 4) << 32));

    private void WriteNetworkOpen(uint p, FileMetadata m)
    {
        WriteTimes(p, m);
        WriteI64(p + 32, m.AllocationSize);
        WriteI64(p + 40, m.EndOfFile);
        Bus.Write32(p + 48, (uint)m.Attributes);
    }

    // NtQueryInformationFile(FileHandle, IoStatusBlock, FileInformation, Length, FileInformationClass)
    private void NtQueryInformationFile()
    {
        var f = FileFor(Arg(0), out var status);
        if (f is null) { Return(status); return; }
        uint iosb = Arg(1), p = Arg(2), length = Arg(3);
        var cls = (int)Arg(4);
        var m = f.File.Metadata;
        var (needed, write) = cls switch
        {
            FileBasicInformation => (40u, (Action)(() => { WriteTimes(p, m); Bus.Write32(p + 32, (uint)m.Attributes); })),
            FileStandardInformation => (24u, () => { WriteI64(p, m.AllocationSize); WriteI64(p + 8, m.EndOfFile); Bus.Write32(p + 16, 1); Bus.Write8(p + 20, (byte)(f.File.DeletePending ? 1 : 0)); Bus.Write8(p + 21, (byte)(f.File.IsDirectory ? 1 : 0)); }),
            FilePositionInformation => (8u, () => WriteI64(p, f.File.Position)),
            FileNetworkOpenInformation => (56u, () => WriteNetworkOpen(p, m)),
            _ => throw Unsupported($"NtQueryInformationFile class {cls}"),
        };
        if (length < needed) { WriteIoStatus(iosb, FsStatus.BufferTooSmall, 0); Return(FsStatus.BufferTooSmall); return; }
        write();
        WriteIoStatus(iosb, NtStatus.Success, needed);
        Return(NtStatus.Success);
    }

    // NtSetInformationFile(FileHandle, IoStatusBlock, FileInformation, Length, FileInformationClass)
    private void NtSetInformationFile()
    {
        var f = FileFor(Arg(0), out var status);
        if (f is null) { Return(status); return; }
        uint iosb = Arg(1), p = Arg(2);
        var cls = (int)Arg(4);
        uint st;
        switch (cls)
        {
            case FilePositionInformation: f.File.Position = ReadI64(p); st = NtStatus.Success; break;
            case FileEndOfFileInformation: st = f.File.SetLength(ReadI64(p)); break;
            case FileAllocationInformation:
            {
                var allocation = ReadI64(p);
                st = allocation < f.File.Metadata.EndOfFile ? f.File.SetLength(allocation) : NtStatus.Success; // growing allocation has no visible effect
                break;
            }
            case FileDispositionInformation:
                if (f.File.Mount.ReadOnly) { st = FsStatus.MediaWriteProtected; break; }
                f.File.DeletePending = Bus.Read8(p) != 0;
                st = NtStatus.Success;
                break;
            case FileBasicInformation: st = f.File.SetTimes(ReadI64(p), ReadI64(p + 8), ReadI64(p + 16)); break;
            case FileRenameInformation:
            {
                // FILE_RENAME_INFORMATION { BOOLEAN ReplaceIfExists; HANDLE RootDirectory; OBJECT_STRING FileName; }
                var oa = _m.Allocate(16, "rename scratch");
                Bus.Write32(oa, Bus.Read32(p + 4)); // RootDirectory (e.g. ObDosDevicesDirectory)
                Bus.Write32(oa + 4, p + 8);
                var (path, ps) = ObjectPath(oa);
                _m.Memory.Free(oa);
                st = path is null ? ps : _m.Storage.Rename(f.File, path, Bus.Read8(p) != 0);
                break;
            }
            default: throw Unsupported($"NtSetInformationFile class {cls}");
        }
        WriteIoStatus(iosb, st, 0);
        Return(st);
    }

    // NtQueryFullAttributesFile(ObjectAttributes, FILE_NETWORK_OPEN_INFORMATION*)
    private void NtQueryFullAttributesFile()
    {
        var (path, status) = ObjectPath(Arg(0));
        if (path is null) { Return(status); return; }
        var (m, st) = _m.Storage.Stat(path);
        if (m is not null) WriteNetworkOpen(Arg(1), m);
        Return(st);
    }

    // NtQueryDirectoryFile(FileHandle, Event, ApcRoutine, ApcContext, IoStatusBlock, FileInformation, Length, Class, FileMask, RestartScan)
    private void NtQueryDirectoryFile()
    {
        var f = FileFor(Arg(0), out var status);
        if (f is null) { Return(status); return; }
        if (Arg(2) != 0) throw Unsupported("NtQueryDirectoryFile with an APC completion routine");
        if (Arg(7) != FileDirectoryInformation) throw Unsupported($"NtQueryDirectoryFile class {Arg(7)}");
        uint iosb = Arg(4), p = Arg(5), length = Arg(6);
        var mask = Arg(8) == 0 ? null : ReadAnsiString(Arg(8));
        var (entry, st) = f.File.NextEntry(mask, (Arg(9) & 0xFF) != 0);
        if (entry is null) { CompleteIo(Arg(1), iosb, st, 0); return; }
        var name = Encoding.ASCII.GetBytes(entry.Name);
        var needed = 64 + (uint)name.Length;
        if (length < needed) { f.File.UnreadEntry(); CompleteIo(Arg(1), iosb, FsStatus.BufferTooSmall, 0); return; }
        Bus.Write32(p, 0);               // NextEntryOffset: single entry
        Bus.Write32(p + 4, 0);           // FileIndex
        WriteTimes(p + 8, entry);
        WriteI64(p + 40, entry.EndOfFile);
        WriteI64(p + 48, entry.AllocationSize);
        Bus.Write32(p + 56, (uint)entry.Attributes);
        Bus.Write32(p + 60, (uint)name.Length);
        Bus.WriteBytes(p + 64, name);
        CompleteIo(Arg(1), iosb, NtStatus.Success, needed);
    }

    // NtQueryVolumeInformationFile(FileHandle, IoStatusBlock, FsInformation, Length, FsInformationClass)
    private void NtQueryVolumeInformationFile()
    {
        var f = FileFor(Arg(0), out var status);
        if (f is null) { Return(status); return; }
        if (Arg(4) != FileFsSizeInformation) throw Unsupported($"NtQueryVolumeInformationFile class {Arg(4)}");
        if (Arg(3) < 24) { WriteIoStatus(Arg(1), FsStatus.BufferTooSmall, 0); Return(FsStatus.BufferTooSmall); return; }
        var (total, free, cluster, st) = VirtualStorage.VolumeSize(f.File.Mount);
        var p = Arg(2);
        WriteI64(p, total);
        WriteI64(p + 8, free);
        Bus.Write32(p + 16, (uint)(cluster / 512));
        Bus.Write32(p + 20, 512);
        WriteIoStatus(Arg(1), st, 24);
        Return(st);
    }

    private void NtFlushBuffersFile()
    {
        var f = FileFor(Arg(0), out var status);
        if (f is null) { Return(status); return; }
        f.File.Flush();
        WriteIoStatus(Arg(1), NtStatus.Success, 0);
        Return(NtStatus.Success);
    }

    private void NtDeleteFile()
    {
        var (path, status) = ObjectPath(Arg(0));
        Return(path is null ? status : _m.Storage.Delete(path));
    }
}
