using XboxPc.Application;
using XboxPc.Domain;
using XboxPc.Runtime.Abstractions;
using XboxPc.Runtimes;
using XboxPc.Runtimes.Reference;

namespace XboxPc.Tests.Integration;

public class LaunchCoordinatorTests
{
    private static async Task<(LaunchCoordinator Launcher, LibraryService Library)> CreateAsync(TempDir dir, params IRuntimeAdapter[] runtimes)
    {
        var (library, _) = await TestDb.LibraryAsync(dir);
        var registry = new RuntimeRegistry(runtimes.Length > 0 ? runtimes : BuiltInRuntimes.Create());
        return (new LaunchCoordinator(registry, library, NullAppLog.Instance), library);
    }

    [Fact]
    public async Task Reference_program_runs_end_to_end_and_marks_launched()
    {
        using var dir = new TempDir();
        var (launcher, library) = await CreateAsync(dir);
        var entry = await library.AddAsync(new GameEntry { DisplayName = "Fib", Generation = ConsoleGeneration.Other, ContentPath = Samples.Path("fibonacci.xpct") });

        var outcome = await launcher.LaunchAsync(entry, null, TimeSpan.FromSeconds(20), null, CancellationToken.None);

        Assert.Equal(LaunchStatus.Completed, outcome.Status);
        Assert.Equal(ReferenceTestRuntime.Id, outcome.Runtime!.RuntimeId);
        Assert.StartsWith("sha256:", outcome.ContentFingerprint, StringComparison.Ordinal);
        Assert.Equal(Samples.Fibonacci(30).Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)), outcome.Result!.Output);
        Assert.NotNull((await library.GetAsync(entry.Id))!.LastLaunchedAt);
    }

    [Theory]
    [InlineData(ConsoleGeneration.SeriesXS, "Research Only")]
    [InlineData(ConsoleGeneration.XboxOne, "Research Only")]
    [InlineData(ConsoleGeneration.Xbox360, "Not Implemented")]
    [InlineData(ConsoleGeneration.OriginalXbox, "Experimental")]
    public async Task Console_generations_are_refused_and_never_marked_launched(ConsoleGeneration generation, string expected)
    {
        using var dir = new TempDir();
        var (launcher, library) = await CreateAsync(dir);
        var file = dir.File("content.bin", "user content");
        var entry = await library.AddAsync(new GameEntry { DisplayName = "Console title", Generation = generation, ContentPath = file });

        var outcome = await launcher.LaunchAsync(entry, null, TimeSpan.FromSeconds(5), null, CancellationToken.None);

        Assert.Equal(LaunchStatus.Refused, outcome.Status);
        Assert.Contains(expected, outcome.Message, StringComparison.Ordinal);
        Assert.Null(outcome.Result);
        Assert.Null((await library.GetAsync(entry.Id))!.LastLaunchedAt);
    }

    [Fact]
    public async Task Missing_content_path_is_refused()
    {
        using var dir = new TempDir();
        var (launcher, library) = await CreateAsync(dir);
        var entry = await library.AddAsync(new GameEntry { DisplayName = "No path", Generation = ConsoleGeneration.Other });
        Assert.Equal(LaunchStatus.Refused, (await launcher.LaunchAsync(entry, null, TimeSpan.FromSeconds(5), null, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Content_deleted_after_adding_is_refused()
    {
        using var dir = new TempDir();
        var (launcher, library) = await CreateAsync(dir);
        var file = dir.File("gone.xpct", "XPCT 1\nhalt");
        var entry = await library.AddAsync(new GameEntry { DisplayName = "Gone", Generation = ConsoleGeneration.Other, ContentPath = file });
        File.Delete(file);
        var outcome = await launcher.LaunchAsync(entry, null, TimeSpan.FromSeconds(5), null, CancellationToken.None);
        Assert.Equal(LaunchStatus.Refused, outcome.Status);
        Assert.Contains("not found", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_program_is_refused_by_runtime_validation()
    {
        using var dir = new TempDir();
        var (launcher, library) = await CreateAsync(dir);
        var entry = await library.AddAsync(new GameEntry { DisplayName = "Bad", Generation = ConsoleGeneration.Other, ContentPath = dir.File("bad.xpct", "not a program") });
        var outcome = await launcher.LaunchAsync(entry, null, TimeSpan.FromSeconds(5), null, CancellationToken.None);
        Assert.Equal(LaunchStatus.Refused, outcome.Status);
        Assert.Contains("header", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hanging_session_times_out_and_is_stopped()
    {
        using var dir = new TempDir();
        var fake = new FakeRuntime { Mode = FakeRuntime.Behavior.Hang };
        var (launcher, library) = await CreateAsync(dir, fake);
        var entry = await library.AddAsync(new GameEntry { DisplayName = "Hang", Generation = ConsoleGeneration.Other, ContentPath = dir.File("h.bin", "x") });

        var outcome = await launcher.LaunchAsync(entry, null, TimeSpan.FromMilliseconds(200), null, CancellationToken.None);

        Assert.Equal(LaunchStatus.TimedOut, outcome.Status);
        Assert.Equal(1, fake.StopCalls);
    }

    [Fact]
    public async Task User_cancellation_stops_the_session()
    {
        using var dir = new TempDir();
        var fake = new FakeRuntime { Mode = FakeRuntime.Behavior.Hang };
        var (launcher, library) = await CreateAsync(dir, fake);
        var entry = await library.AddAsync(new GameEntry { DisplayName = "Cancel", Generation = ConsoleGeneration.Other, ContentPath = dir.File("c.bin", "x") });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var outcome = await launcher.LaunchAsync(entry, null, TimeSpan.FromSeconds(30), null, cts.Token);

        Assert.Equal(LaunchStatus.Stopped, outcome.Status);
        Assert.Equal(1, fake.StopCalls);
    }

    [Fact]
    public async Task Faulted_session_is_reported_as_fault()
    {
        using var dir = new TempDir();
        var (launcher, library) = await CreateAsync(dir, new FakeRuntime { Mode = FakeRuntime.Behavior.Fault });
        var entry = await library.AddAsync(new GameEntry { DisplayName = "F", Generation = ConsoleGeneration.Other, ContentPath = dir.File("f.bin", "x") });
        Assert.Equal(LaunchStatus.Faulted, (await launcher.LaunchAsync(entry, null, TimeSpan.FromSeconds(5), null, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Incompatible_contract_major_is_refused()
    {
        using var dir = new TempDir();
        var (launcher, library) = await CreateAsync(dir, new FakeRuntime { Contract = new Version(2, 0) });
        var entry = await library.AddAsync(new GameEntry { DisplayName = "V2", Generation = ConsoleGeneration.Other, ContentPath = dir.File("v.bin", "x") });
        var outcome = await launcher.LaunchAsync(entry, null, TimeSpan.FromSeconds(5), null, CancellationToken.None);
        Assert.Equal(LaunchStatus.Refused, outcome.Status);
        Assert.Contains("contract", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Experimental_runtime_is_refused()
    {
        using var dir = new TempDir();
        var (launcher, library) = await CreateAsync(dir, new FakeRuntime(ImplementationState.Experimental));
        var entry = await library.AddAsync(new GameEntry { DisplayName = "E", Generation = ConsoleGeneration.Other, ContentPath = dir.File("e.bin", "x") });
        Assert.Equal(LaunchStatus.Refused, (await launcher.LaunchAsync(entry, null, TimeSpan.FromSeconds(5), null, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Preferred_runtime_of_other_generation_is_ignored()
    {
        using var dir = new TempDir();
        var (launcher, library) = await CreateAsync(dir);
        var entry = await library.AddAsync(new GameEntry { DisplayName = "S", Generation = ConsoleGeneration.SeriesXS, ContentPath = dir.File("s.bin", "x") });
        var outcome = await launcher.LaunchAsync(entry, ReferenceTestRuntime.Id, TimeSpan.FromSeconds(5), null, CancellationToken.None);
        Assert.Equal(LaunchStatus.Refused, outcome.Status);
        Assert.Equal(ConsoleGeneration.SeriesXS, outcome.Runtime!.Generation);
    }
}
