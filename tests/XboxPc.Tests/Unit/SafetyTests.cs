using XboxPc.Application;

namespace XboxPc.Tests.Unit;

public class SafePathTests
{
    [Theory]
    [InlineData(@"..\..\Windows\System32\config")]
    [InlineData(@"relative\file.xpct")]
    [InlineData(@"C:\Games\..\Windows\notepad.exe")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"C:\Games\CON")]
    [InlineData(@"C:\Games\nul.txt")]
    [InlineData(@"C:\Games\file.txt:hidden")]
    [InlineData(@"C:\Games\*.xpct")]
    [InlineData("")]
    [InlineData("   ")]
    public void Unsafe_paths_are_rejected(string input)
    {
        var (path, error) = SafePath.Validate(input, mustExist: false);
        Assert.Null(path);
        Assert.NotNull(error);
    }

    [Fact]
    public void Existing_file_is_accepted_and_normalized()
    {
        using var dir = new TempDir();
        var file = dir.File("a.xpct", "x");
        var (path, error) = SafePath.Validate($"\"{file.Replace('\\', '/')}\"", mustExist: true);
        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(file), path);
    }

    [Fact]
    public void Missing_file_is_rejected_when_required()
    {
        using var dir = new TempDir();
        var (_, error) = SafePath.Validate(Path.Combine(dir.Path, "missing.bin"), mustExist: true);
        Assert.Contains("not found", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Directory_only_accepted_when_allowed()
    {
        using var dir = new TempDir();
        Assert.NotNull(SafePath.Validate(dir.Path, mustExist: true).Error);
        Assert.Null(SafePath.Validate(dir.Path, mustExist: true, allowDirectory: true).Error);
    }

    [Theory]
    [InlineData("sub/file.txt", true)]
    [InlineData("../escape.txt", false)]
    [InlineData("sub/../../escape.txt", false)]
    [InlineData(@"C:\Windows\evil.dll", false)]
    public void Zip_slip_guard(string entry, bool inside) => Assert.Equal(inside, SafePath.IsWithin(@"C:\extract\root", entry));
}

public class ImageFileValidatorTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    [Fact]
    public void Real_png_header_passes()
    {
        using var dir = new TempDir();
        Assert.Null(ImageFileValidator.Validate(dir.Bytes("c.png", Png)));
    }

    [Fact]
    public void Executable_renamed_to_png_fails()
    {
        using var dir = new TempDir();
        var path = dir.Bytes("evil.png", [(byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0, 4, 0]);
        Assert.Contains("does not match", ImageFileValidator.Validate(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_extension_fails()
    {
        using var dir = new TempDir();
        Assert.NotNull(ImageFileValidator.Validate(dir.Bytes("c.svg", Png)));
    }

    [Fact]
    public void Oversized_file_fails()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "big.png");
        using (var fs = File.Create(path))
        {
            fs.Write(Png);
            fs.SetLength(ImageFileValidator.MaxBytes + 1);
        }
        Assert.Contains("10 MB", ImageFileValidator.Validate(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Tiny_file_fails()
    {
        using var dir = new TempDir();
        Assert.NotNull(ImageFileValidator.Validate(dir.Bytes("t.jpg", [0xFF, 0xD8])));
    }
}

public class LogRedactorTests
{
    [Theory]
    [InlineData("login password=hunter2 ok", "hunter2")]
    [InlineData("token: abc.def.ghi", "abc.def.ghi")]
    [InlineData("refresh_token=\"r1 r2\"", "r1 r2")]
    [InlineData("Authorization: Bearer eyJhbGciOi.payload.sig", "eyJhbGciOi")]
    [InlineData("api_key=SECRET123", "SECRET123")]
    public void Secrets_are_masked(string input, string secret)
    {
        var output = LogRedactor.Redact(input, @"C:\Users\someone");
        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.Contains("***", output, StringComparison.Ordinal);
    }

    [Fact]
    public void User_profile_path_is_masked() =>
        Assert.Equal(@"opened %USERPROFILE%\Games\a.xpct", LogRedactor.Redact(@"opened C:\Users\Someone\Games\a.xpct", @"C:\Users\someone"));

    [Fact]
    public void Ordinary_text_is_unchanged() => Assert.Equal("Session started", LogRedactor.Redact("Session started", @"C:\Users\x"));
}
