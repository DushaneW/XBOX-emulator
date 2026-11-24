using XboxPc.Domain;
using XboxPc.Runtime.Abstractions;
using XboxPc.Runtimes.Reference;

namespace XboxPc.Tests.Unit;

public class XpctParserTests
{
    [Theory]
    [InlineData("set r0 1\nhalt", "header")]
    [InlineData("XPCT 1\nfoo r1", "unknown instruction")]
    [InlineData("XPCT 1\nset r9 1", "not a register")]
    [InlineData("XPCT 1\nset r0 abc", "not an integer")]
    [InlineData("XPCT 1\njmp nowhere", "unknown label")]
    [InlineData("XPCT 1\nadd r0", "expects 2")]
    [InlineData("XPCT 1\na:\na:\nhalt", "duplicate label")]
    [InlineData("XPCT 1\n; only a comment", "no instructions")]
    public void Invalid_programs_report_errors(string text, string expected)
    {
        var (program, errors) = XpctProgram.Parse(text);
        Assert.Null(program);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Errors_include_line_numbers()
    {
        var (_, errors) = XpctProgram.Parse("XPCT 1\nhalt\nbogus");
        Assert.StartsWith("Line 3:", errors[0], StringComparison.Ordinal);
    }
}

public class XpctInterpreterTests
{
    private static XpctProgram Load(string file) => XpctProgram.Parse(File.ReadAllText(Samples.Path(file))).Program!;

    [Fact]
    public void Fibonacci_sample_matches_independent_computation()
    {
        var result = XpctInterpreter.Run(Load("fibonacci.xpct"), 1_000_000, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Halted, result.Outcome);
        Assert.Equal(Samples.Fibonacci(30), result.Output);
    }

    [Fact]
    public void Primes_sample_matches_sieve()
    {
        var result = XpctInterpreter.Run(Load("primes.xpct"), 50_000_000, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Halted, result.Outcome);
        Assert.Equal(Samples.PrimesBelow(2000), result.Output);
    }

    [Fact]
    public void Infinite_loop_hits_budget()
    {
        var program = XpctProgram.Parse("XPCT 1\nloop:\njmp loop").Program!;
        var result = XpctInterpreter.Run(program, 10_000, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.BudgetExceeded, result.Outcome);
        Assert.Equal(10_000, result.Steps);
    }

    [Fact]
    public void Missing_halt_is_detected()
    {
        var result = XpctInterpreter.Run(XpctProgram.Parse("XPCT 1\nset r0 1").Program!, 100, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.FellOffEnd, result.Outcome);
    }

    [Fact]
    public void Arithmetic_wraps_instead_of_throwing()
    {
        var program = XpctProgram.Parse($"XPCT 1\nset r0 {long.MaxValue}\ninc r0\nout r0\nhalt").Program!;
        Assert.Equal([long.MinValue], XpctInterpreter.Run(program, 100, CancellationToken.None).Output);
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        var program = XpctProgram.Parse("XPCT 1\nloop:\njmp loop").Program!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => XpctInterpreter.Run(program, long.MaxValue, cts.Token));
    }
}

public class ReferenceRuntimeSessionTests
{
    private readonly ReferenceTestRuntime _runtime = new();

    [Fact]
    public async Task Rejects_console_generations()
    {
        var result = await _runtime.ValidateContentAsync(new ContentDescriptor(Samples.Path("fibonacci.xpct"), ConsoleGeneration.SeriesXS), CancellationToken.None);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Rejects_wrong_extension_and_missing_file()
    {
        using var dir = new TempDir();
        Assert.False((await _runtime.ValidateContentAsync(new ContentDescriptor(dir.File("a.iso", "XPCT 1\nhalt"), ConsoleGeneration.Other), CancellationToken.None)).IsValid);
        Assert.False((await _runtime.ValidateContentAsync(new ContentDescriptor(Path.Combine(dir.Path, "nope.xpct"), ConsoleGeneration.Other), CancellationToken.None)).IsValid);
    }

    [Fact]
    public async Task Rejects_invalid_utf8()
    {
        using var dir = new TempDir();
        var path = dir.Bytes("bad.xpct", [0x58, 0x50, 0xC3, 0x28]);
        Assert.False((await _runtime.ValidateContentAsync(new ContentDescriptor(path, ConsoleGeneration.Other), CancellationToken.None)).IsValid);
    }

    [Fact]
    public async Task Valid_program_reports_sha256_fingerprint()
    {
        var path = Samples.Path("fibonacci.xpct");
        var result = await _runtime.ValidateContentAsync(new ContentDescriptor(path, ConsoleGeneration.Other), CancellationToken.None);
        Assert.True(result.IsValid);
        var expected = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(expected, result.Fingerprint);
    }

    [Fact]
    public async Task Session_lifecycle_runs_to_completion()
    {
        await using var session = await _runtime.PrepareSessionAsync(new ContentDescriptor(Samples.Path("fibonacci.xpct"), ConsoleGeneration.Other), new SessionOptions(), CancellationToken.None);
        Assert.Equal(SessionState.Prepared, session.State);
        await session.StartAsync(CancellationToken.None);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionState.Completed, result.FinalState);
        Assert.Equal(Samples.Fibonacci(30).Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)), result.Output);
        var diag = session.GetDiagnostics();
        Assert.Equal("30", diag.Counters["outputs"]);
        Assert.Contains(diag.Entries, e => e.Message.Contains("halted normally", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Stop_ends_a_running_session()
    {
        using var dir = new TempDir();
        var path = dir.File("spin.xpct", "XPCT 1\nloop:\njmp loop");
        await using var session = await _runtime.PrepareSessionAsync(new ContentDescriptor(path, ConsoleGeneration.Other),
            new SessionOptions { Settings = new Dictionary<string, string> { ["maxSteps"] = "9000000000000" } }, CancellationToken.None);
        await session.StartAsync(CancellationToken.None);
        await session.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionState.Stopped, session.State);
    }

    [Fact]
    public async Task Budget_exhaustion_is_a_fault()
    {
        using var dir = new TempDir();
        var path = dir.File("spin.xpct", "XPCT 1\nloop:\njmp loop");
        await using var session = await _runtime.PrepareSessionAsync(new ContentDescriptor(path, ConsoleGeneration.Other),
            new SessionOptions { Settings = new Dictionary<string, string> { ["maxSteps"] = "5000" } }, CancellationToken.None);
        await session.StartAsync(CancellationToken.None);
        Assert.Equal(SessionState.Faulted, (await session.Completion.WaitAsync(TimeSpan.FromSeconds(10))).FinalState);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("lots")]
    public async Task Invalid_max_steps_setting_is_rejected(string value) =>
        await Assert.ThrowsAsync<ArgumentException>(() => _runtime.PrepareSessionAsync(new ContentDescriptor(Samples.Path("fibonacci.xpct"), ConsoleGeneration.Other),
            new SessionOptions { Settings = new Dictionary<string, string> { ["maxSteps"] = value } }, CancellationToken.None));
}
