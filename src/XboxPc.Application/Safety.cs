using System.Text.RegularExpressions;

namespace XboxPc.Application;

public static class SafePath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Validates a user-selected path. Returns the normalized full path or an error.</summary>
    public static (string? Path, string? Error) Validate(string? input, bool mustExist, bool allowDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(input)) return (null, "Path is empty.");
        var path = input.Trim().Trim('"');
        if (path.Length > 32_000) return (null, "Path is too long.");
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.StartsWith("//?/", StringComparison.Ordinal) || path.StartsWith("//./", StringComparison.Ordinal))
            return (null, "Device and extended-length paths are not allowed.");
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('*', StringComparison.Ordinal) || path.Contains('?', StringComparison.Ordinal))
            return (null, "Path contains invalid characters.");
        if (!Path.IsPathFullyQualified(path)) return (null, "Path must be absolute (for example C:\\Games\\file).");

        var segments = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == "..") return (null, "Path must not contain '..' segments.");
            var stem = segment.Split('.')[0].TrimEnd(' ');
            if (ReservedNames.Contains(stem)) return (null, $"'{segment}' is a reserved Windows device name.");
        }

        // Alternate data streams: a colon is only legal as the drive separator.
        if (path.LastIndexOf(':') > 1) return (null, "Alternate data streams are not allowed.");

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, $"Path is not valid: {ex.Message}");
        }

        if (mustExist)
        {
            if (File.Exists(full)) return (full, null);
            if (allowDirectory && Directory.Exists(full)) return (full, null);
            return (null, $"File not found: {full}");
        }
        return (full, null);
    }

    /// <summary>True if <paramref name="candidate"/> resolves inside <paramref name="root"/> (zip-slip guard).</summary>
    public static bool IsWithin(string root, string candidate)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(candidate);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, candidate));
        return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}

public static class ImageFileValidator
{
    public const long MaxBytes = 10 * 1024 * 1024;
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp"];

    public static string? Validate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!Extensions.Contains(ext)) return "Cover art must be a PNG, JPEG or BMP file.";
        var info = new FileInfo(path);
        if (!info.Exists) return $"File not found: {path}";
        if (info.Length > MaxBytes) return "Cover art must be 10 MB or smaller.";
        if (info.Length < 8) return "File is too small to be an image.";

        Span<byte> header = stackalloc byte[8];
        using (var stream = info.OpenRead())
        {
            stream.ReadExactly(header);
        }
        var ok = ext switch
        {
            ".png" => header.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".jpg" or ".jpeg" => header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".bmp" => header[0] == (byte)'B' && header[1] == (byte)'M',
            _ => false,
        };
        return ok ? null : "File content does not match its image extension.";
    }
}

public static partial class LogRedactor
{
    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|token|access_token|refresh_token|secret|api[_-]?key|authorization)\b(\s*[:=]\s*)(""[^""]*""|\S+)")]
    private static partial Regex SecretPairs();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9\-\._~\+/]+=*")]
    private static partial Regex BearerToken();

    public static string Redact(string? message, string? userProfilePath = null)
    {
        if (string.IsNullOrEmpty(message)) return message ?? "";
        // Bearer tokens first, so "Authorization: Bearer x" does not leave x behind.
        var result = BearerToken().Replace(message, "Bearer ***");
        result = SecretPairs().Replace(result, m => m.Groups[3].Value is "***" or "Bearer" ? m.Value : m.Groups[1].Value + m.Groups[2].Value + "***");
        userProfilePath ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfilePath) && userProfilePath.Length > 3)
            result = result.Replace(userProfilePath, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return result;
    }
}
