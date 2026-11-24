namespace XboxPc.Emulation.Storage;

/// <summary>NTSTATUS values produced by the storage model.</summary>
public static class FsStatus
{
    public const uint Success = 0, NoMoreFiles = 0x8000_0006, BufferOverflow = 0x8000_0005;
    public const uint EndOfFile = 0xC000_0011, NoSuchFile = 0xC000_000F, AccessDenied = 0xC000_0022, BufferTooSmall = 0xC000_0023;
    public const uint ObjectNameInvalid = 0xC000_0033, ObjectNameNotFound = 0xC000_0034, ObjectNameCollision = 0xC000_0035;
    public const uint ObjectPathNotFound = 0xC000_003A, MediaWriteProtected = 0xC000_00A2, FileIsADirectory = 0xC000_00BA;
    public const uint NotADirectory = 0xC000_0103, DirectoryNotEmpty = 0xC000_0101, DiskFull = 0xC000_007F, SharingViolation = 0xC000_0043;
    public const uint InvalidParameter = 0xC000_000D;
}

public enum CreateDisposition : uint
{
    Supersede = 0,
    Open = 1,
    Create = 2,
    OpenIf = 3,
    Overwrite = 4,
    OverwriteIf = 5,
}

/// <summary>IO_STATUS_BLOCK.Information values for create/open.</summary>
public enum CreateResult : uint
{
    Superseded = 0,
    Opened = 1,
    Created = 2,
    Overwritten = 3,
}

[Flags]
public enum FileAttributes : uint
{
    ReadOnly = 0x01,
    Hidden = 0x02,
    System = 0x04,
    Directory = 0x10,
    Archive = 0x20,
    Normal = 0x80,
}

public sealed record FileMetadata(long CreationTime, long LastAccessTime, long LastWriteTime, long ChangeTime, long EndOfFile, long AllocationSize, FileAttributes Attributes, string Name);

/// <summary>A mounted Xbox device backed by a user-selected host directory.</summary>
public sealed record Mount(string Device, string HostRoot, bool ReadOnly, int ClusterSize, string Description);

/// <summary>
/// Xbox storage model: NT device paths (\Device\CdRom0, \Device\Harddisk0\Partition1, ...) are mapped to host directories
/// chosen by the user. Path components follow FATX rules (≤ 42 characters, no control characters or "*/:<>?\|"); "."
/// and ".." are rejected, and every resolved host path is verified to stay inside its mount root. The disc drive is
/// mounted read-only. Nothing outside the mounted directories is ever touched.
/// </summary>
public sealed class VirtualStorage
{
    public const int FatxMaxNameLength = 42;
    private static readonly char[] Forbidden = ['"', '*', '/', ':', '<', '>', '?', '\\', '|'];
    private readonly List<Mount> _mounts = [];

    public IReadOnlyList<Mount> Mounts => _mounts;

    public void MountDirectory(string device, string hostRoot, bool readOnly, string description, int clusterSize = 16384)
    {
        ArgumentException.ThrowIfNullOrEmpty(device);
        ArgumentException.ThrowIfNullOrEmpty(hostRoot);
        var full = Path.GetFullPath(hostRoot);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Mount root does not exist: {full}");
        _mounts.RemoveAll(m => string.Equals(m.Device, device.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        _mounts.Add(new Mount(device.TrimEnd('\\'), full, readOnly, clusterSize, description));
    }

    /// <summary>Maps an NT path to (mount, host path). Returns an NTSTATUS error instead when it cannot.</summary>
    public (Mount? Mount, string? HostPath, uint Status) Resolve(string ntPath)
    {
        ArgumentNullException.ThrowIfNull(ntPath);
        var mount = _mounts.Where(m => ntPath.Equals(m.Device, StringComparison.OrdinalIgnoreCase) ||
                                       ntPath.StartsWith(m.Device + "\\", StringComparison.OrdinalIgnoreCase))
                           .OrderByDescending(m => m.Device.Length).FirstOrDefault();
        if (mount is null) return (null, null, FsStatus.ObjectPathNotFound);
        var rest = ntPath[mount.Device.Length..].Trim('\\');
        var host = mount.HostRoot;
        if (rest.Length > 0)
        {
            foreach (var part in rest.Split('\\'))
            {
                if (part.Length == 0 || part is "." or ".." || part.Length > FatxMaxNameLength || part.Any(c => c < 0x20 || c > 0x7E || Forbidden.Contains(c)))
                    return (mount, null, FsStatus.ObjectNameInvalid);
                host = Path.Combine(host, part);
            }
        }
        var fullHost = Path.GetFullPath(host);
        var root = Path.TrimEndingDirectorySeparator(mount.HostRoot);
        if (!fullHost.Equals(root, StringComparison.OrdinalIgnoreCase) && !fullHost.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return (mount, null, FsStatus.ObjectNameInvalid);
        return (mount, fullHost, FsStatus.Success);
    }

    /// <summary>Opens or creates a file or directory with NT create semantics.</summary>
    public (StorageFile? File, CreateResult Result, uint Status) Open(string ntPath, bool wantWrite, CreateDisposition disposition, bool directoryFile, bool nonDirectoryFile, bool deleteOnClose = false)
    {
        var (mount, host, status) = Resolve(ntPath);
        if (status != FsStatus.Success) return (null, 0, status);
        var isDir = Directory.Exists(host);
        var exists = isDir || File.Exists(host);
        var mutates = wantWrite || deleteOnClose || disposition is CreateDisposition.Supersede or CreateDisposition.Overwrite or CreateDisposition.OverwriteIf || (!exists && disposition is CreateDisposition.Create or CreateDisposition.OpenIf);
        if (mutates && mount!.ReadOnly) return (null, 0, FsStatus.MediaWriteProtected);
        if (!exists && !Directory.Exists(Path.GetDirectoryName(host!))) return (null, 0, FsStatus.ObjectPathNotFound);
        if (exists && isDir && nonDirectoryFile) return (null, 0, FsStatus.FileIsADirectory);
        if (exists && !isDir && directoryFile) return (null, 0, FsStatus.NotADirectory);

        CreateResult result;
        switch (disposition)
        {
            case CreateDisposition.Open:
                if (!exists) return (null, 0, FsStatus.ObjectNameNotFound);
                result = CreateResult.Opened;
                break;
            case CreateDisposition.Create:
                if (exists) return (null, 0, FsStatus.ObjectNameCollision);
                result = CreateResult.Created;
                break;
            case CreateDisposition.OpenIf:
                result = exists ? CreateResult.Opened : CreateResult.Created;
                break;
            case CreateDisposition.Overwrite:
                if (!exists) return (null, 0, FsStatus.ObjectNameNotFound);
                result = CreateResult.Overwritten;
                break;
            case CreateDisposition.OverwriteIf:
                result = exists ? CreateResult.Overwritten : CreateResult.Created;
                break;
            case CreateDisposition.Supersede:
                result = exists ? CreateResult.Superseded : CreateResult.Created;
                break;
            default:
                return (null, 0, FsStatus.InvalidParameter);
        }
        if (isDir && result is CreateResult.Overwritten or CreateResult.Superseded) return (null, 0, FsStatus.FileIsADirectory);

        try
        {
            if (result == CreateResult.Created && directoryFile)
            {
                Directory.CreateDirectory(host!);
                return (new StorageFile(this, mount!, ntPath, host!, null, deleteOnClose), result, FsStatus.Success);
            }
            if (isDir || (exists && directoryFile))
                return (new StorageFile(this, mount!, ntPath, host!, null, deleteOnClose), result, FsStatus.Success);
            var mode = result switch
            {
                CreateResult.Created => FileMode.CreateNew,
                CreateResult.Overwritten or CreateResult.Superseded => FileMode.Truncate,
                _ => FileMode.Open,
            };
            var access = mount!.ReadOnly || !wantWrite && mode == FileMode.Open ? FileAccess.Read : FileAccess.ReadWrite;
            var stream = new FileStream(host!, mode, access, FileShare.ReadWrite | FileShare.Delete);
            return (new StorageFile(this, mount, ntPath, host!, stream, deleteOnClose), result, FsStatus.Success);
        }
        catch (UnauthorizedAccessException) { return (null, 0, FsStatus.AccessDenied); }
        catch (IOException e) when (e.HResult == unchecked((int)0x80070020)) { return (null, 0, FsStatus.SharingViolation); }
        catch (IOException e) when (e.HResult == unchecked((int)0x80070070)) { return (null, 0, FsStatus.DiskFull); }
    }

    public (FileMetadata? Metadata, uint Status) Stat(string ntPath)
    {
        var (mount, host, status) = Resolve(ntPath);
        if (status != FsStatus.Success) return (null, status);
        if (!File.Exists(host) && !Directory.Exists(host)) return (null, FsStatus.ObjectNameNotFound);
        return (Describe(mount!, host!), FsStatus.Success);
    }

    internal static FileMetadata Describe(Mount mount, string host)
    {
        FileSystemInfo info = Directory.Exists(host) ? new DirectoryInfo(host) : new FileInfo(host);
        var length = info is FileInfo f ? f.Length : 0;
        var attributes = MapAttributes(info.Attributes, mount.ReadOnly);
        var allocation = (length + mount.ClusterSize - 1) / mount.ClusterSize * mount.ClusterSize;
        return new FileMetadata(info.CreationTimeUtc.ToFileTimeUtc(), info.LastAccessTimeUtc.ToFileTimeUtc(), info.LastWriteTimeUtc.ToFileTimeUtc(),
            info.LastWriteTimeUtc.ToFileTimeUtc(), length, allocation, attributes, info.Name);
    }

    private static FileAttributes MapAttributes(System.IO.FileAttributes host, bool readOnlyMount)
    {
        FileAttributes a = 0;
        if (host.HasFlag(System.IO.FileAttributes.Directory)) a |= FileAttributes.Directory;
        if (host.HasFlag(System.IO.FileAttributes.ReadOnly) || readOnlyMount) a |= FileAttributes.ReadOnly;
        if (host.HasFlag(System.IO.FileAttributes.Hidden)) a |= FileAttributes.Hidden;
        if (host.HasFlag(System.IO.FileAttributes.System)) a |= FileAttributes.System;
        if (host.HasFlag(System.IO.FileAttributes.Archive)) a |= FileAttributes.Archive;
        return a == 0 ? FileAttributes.Normal : a;
    }

    public static (long Total, long Free, int ClusterSize, uint Status) VolumeSize(Mount mount)
    {
        ArgumentNullException.ThrowIfNull(mount);
        if (mount.ReadOnly)
        {
            var used = Directory.EnumerateFiles(mount.HostRoot, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
            return ((used + mount.ClusterSize - 1) / mount.ClusterSize, 0, mount.ClusterSize, FsStatus.Success);
        }
        var drive = new DriveInfo(Path.GetPathRoot(mount.HostRoot)!);
        return (drive.TotalSize / mount.ClusterSize, drive.AvailableFreeSpace / mount.ClusterSize, mount.ClusterSize, FsStatus.Success);
    }

    public uint Rename(StorageFile file, string newNtPath, bool replaceIfExists)
    {
        ArgumentNullException.ThrowIfNull(file);
        var (mount, host, status) = Resolve(newNtPath);
        if (status != FsStatus.Success) return status;
        if (mount != file.Mount) return 0xC000_00D4; // STATUS_NOT_SAME_DEVICE
        if (mount.ReadOnly) return FsStatus.MediaWriteProtected;
        if ((File.Exists(host) || Directory.Exists(host)) && !replaceIfExists) return FsStatus.ObjectNameCollision;
        if (!Directory.Exists(Path.GetDirectoryName(host!))) return FsStatus.ObjectPathNotFound;
        try
        {
            if (file.IsDirectory) Directory.Move(file.HostPath, host!);
            else
            {
                file.CloseStream();
                File.Move(file.HostPath, host!, replaceIfExists);
                file.Reopen(host!);
            }
            file.Retarget(newNtPath, host!);
            return FsStatus.Success;
        }
        catch (IOException) { return FsStatus.AccessDenied; }
        catch (UnauthorizedAccessException) { return FsStatus.AccessDenied; }
    }

    public uint Delete(string ntPath)
    {
        var (mount, host, status) = Resolve(ntPath);
        if (status != FsStatus.Success) return status;
        if (mount!.ReadOnly) return FsStatus.MediaWriteProtected;
        if (Directory.Exists(host))
        {
            if (Directory.EnumerateFileSystemEntries(host).Any()) return FsStatus.DirectoryNotEmpty;
            Directory.Delete(host);
            return FsStatus.Success;
        }
        if (!File.Exists(host)) return FsStatus.ObjectNameNotFound;
        File.Delete(host);
        return FsStatus.Success;
    }
}

/// <summary>An open file or directory.</summary>
public sealed class StorageFile : IDisposable
{
    private readonly VirtualStorage _owner;
    private FileStream? _stream;
    private List<FileMetadata>? _listing;
    private int _listingIndex;

    internal StorageFile(VirtualStorage owner, Mount mount, string ntPath, string hostPath, FileStream? stream, bool deleteOnClose)
    {
        _owner = owner;
        Mount = mount;
        NtPath = ntPath;
        HostPath = hostPath;
        _stream = stream;
        DeletePending = deleteOnClose;
    }

    public Mount Mount { get; }
    public string NtPath { get; private set; }
    public string HostPath { get; private set; }
    public bool IsDirectory => _stream is null;
    public bool DeletePending { get; set; }
    public long Position { get; set; }

    public FileMetadata Metadata => VirtualStorage.Describe(Mount, HostPath);

    public (int Read, uint Status) Read(Span<byte> buffer, long offset)
    {
        if (_stream is null) return (0, FsStatus.FileIsADirectory);
        if (offset >= _stream.Length && buffer.Length > 0) return (0, FsStatus.EndOfFile);
        _stream.Position = offset;
        var total = 0;
        while (total < buffer.Length)
        {
            var n = _stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        Position = offset + total;
        return (total, FsStatus.Success);
    }

    public (int Written, uint Status) Write(ReadOnlySpan<byte> data, long offset)
    {
        if (_stream is null) return (0, FsStatus.FileIsADirectory);
        if (Mount.ReadOnly || !_stream.CanWrite) return (0, FsStatus.AccessDenied);
        try
        {
            _stream.Position = offset;
            _stream.Write(data);
            Position = offset + data.Length;
            return (data.Length, FsStatus.Success);
        }
        catch (IOException) { return (0, FsStatus.DiskFull); }
    }

    public uint SetLength(long length)
    {
        if (_stream is null) return FsStatus.FileIsADirectory;
        if (Mount.ReadOnly || !_stream.CanWrite) return FsStatus.AccessDenied;
        _stream.SetLength(length);
        return FsStatus.Success;
    }

    public void Flush() => _stream?.Flush(flushToDisk: true);

    public uint SetTimes(long creation, long lastAccess, long lastWrite)
    {
        if (Mount.ReadOnly) return FsStatus.MediaWriteProtected;
        // A value of 0 means "do not change" (NT semantics).
        if (_stream is not null) _stream.Flush();
        if (creation != 0) File.SetCreationTimeUtc(HostPath, DateTime.FromFileTimeUtc(creation));
        if (lastAccess != 0) File.SetLastAccessTimeUtc(HostPath, DateTime.FromFileTimeUtc(lastAccess));
        if (lastWrite != 0) File.SetLastWriteTimeUtc(HostPath, DateTime.FromFileTimeUtc(lastWrite));
        return FsStatus.Success;
    }

    /// <summary>Returns the next directory entry matching the mask (* and ? wildcards, case-insensitive).</summary>
    public (FileMetadata? Entry, uint Status) NextEntry(string? mask, bool restart)
    {
        if (!IsDirectory) return (null, FsStatus.InvalidParameter);
        var first = _listing is null || restart;
        if (first)
        {
            var pattern = string.IsNullOrEmpty(mask) ? "*" : mask;
            _listing = Directory.EnumerateFileSystemEntries(HostPath)
                .Select(p => VirtualStorage.Describe(Mount, p))
                .Where(m => Matches(m.Name, pattern))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _listingIndex = 0;
        }
        if (_listingIndex >= _listing!.Count) return (null, first ? FsStatus.NoSuchFile : FsStatus.NoMoreFiles);
        return (_listing[_listingIndex++], FsStatus.Success);
    }

    /// <summary>Puts back the entry returned last (caller's buffer was too small).</summary>
    public void UnreadEntry() => _listingIndex = Math.Max(0, _listingIndex - 1);

    public static bool Matches(string name, string pattern)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(pattern);
        return Match(name.AsSpan(), pattern.AsSpan());

        static bool Match(ReadOnlySpan<char> n, ReadOnlySpan<char> p)
        {
            if (p.IsEmpty) return n.IsEmpty;
            if (p[0] == '*') return Match(n, p[1..]) || (!n.IsEmpty && Match(n[1..], p));
            if (n.IsEmpty) return false;
            return (p[0] == '?' || char.ToUpperInvariant(p[0]) == char.ToUpperInvariant(n[0])) && Match(n[1..], p[1..]);
        }
    }

    internal void CloseStream()
    {
        _stream?.Dispose();
        _stream = null;
    }

    internal void Reopen(string host) => _stream = new FileStream(host, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

    internal void Retarget(string ntPath, string host)
    {
        NtPath = ntPath;
        HostPath = host;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        if (DeletePending) _owner.Delete(NtPath);
    }
}
