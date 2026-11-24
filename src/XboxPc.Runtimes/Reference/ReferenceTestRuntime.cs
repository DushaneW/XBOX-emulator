using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using XboxPc.Domain;
using XboxPc.Runtime.Abstractions;

namespace XboxPc.Runtimes.Reference;

/// <summary>
/// Original reference runtime that executes XPCT test programs. It exists to exercise the platform's runtime
/// contract with something real. It is NOT an Xbox emulator and does not accept any console content.
/// </summary>
public sealed class ReferenceTestRuntime : IRuntimeAdapter
{
    public const string Id = "xpc.reference-test";
    public const long DefaultMaxSteps = 50_000_000;

    public RuntimeDescriptor Descriptor { get; } = new()
    {
        RuntimeId = Id,
        Generation = ConsoleGeneration.Other,
        DisplayName = "XPC Reference Test Runtime",
        Version = typeof(ReferenceTestRuntime).Assembly.GetName().Version?.ToString() ?? "0.1.0",
        State = ImplementationState.Available,
        Capabilities = new HashSet<string>
        {
            RuntimeCapabilities.ContentValidate,
            RuntimeCapabilities.SessionRun,
            RuntimeCapabilities.SessionStop,
            RuntimeCapabilities.ProgramOutput,
        },
        LicenseNotice = "Original project code. Runs only original .xpct test programs.",
        DocumentationPath = "docs/RUNTIME_REFERENCE.md",
        Summary = "Not an Xbox emulator. Executes original .xpct test programs to prove the launch pipeline works end to end.",
    };

    public async Task<ContentValidationResult> ValidateContentAsync(ContentDescriptor content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var loaded = await LoadAsync(content, cancellationToken).ConfigureAwait(false);
        return loaded.Validation;
    }

    public async Task<IRuntimeSession> PrepareSessionAsync(ContentDescriptor content, SessionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        var loaded = await LoadAsync(content, cancellationToken).ConfigureAwait(false);
        if (loaded.Program is null) throw new InvalidDataException(loaded.Validation.Message);

        var maxSteps = DefaultMaxSteps;
        if (options.Settings.TryGetValue("maxSteps", out var raw))
        {
            if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out maxSteps) || maxSteps <= 0)
                throw new ArgumentException($"Setting maxSteps='{raw}' must be a positive integer.", nameof(options));
        }
        return new ReferenceSession(loaded.Program, loaded.Validation.Fingerprint!, maxSteps);
    }

    private static async Task<(XpctProgram? Program, ContentValidationResult Validation)> LoadAsync(ContentDescriptor content, CancellationToken ct)
    {
        if (content.Generation != ConsoleGeneration.Other)
            return (null, ContentValidationResult.Invalid($"This runtime only runs XPCT test programs (generation 'Other'), not {content.Generation.ToDisplay()} content."));
        if (!string.Equals(Path.GetExtension(content.Path), ".xpct", StringComparison.OrdinalIgnoreCase))
            return (null, ContentValidationResult.Invalid("Content must be an .xpct test program."));

        var info = new FileInfo(content.Path);
        if (!info.Exists) return (null, ContentValidationResult.Invalid($"File not found: {content.Path}"));
        if (info.Length > XpctProgram.MaxFileBytes) return (null, ContentValidationResult.Invalid($"File is {info.Length} bytes; limit is {XpctProgram.MaxFileBytes}."));

        var bytes = await File.ReadAllBytesAsync(content.Path, ct).ConfigureAwait(false);
        var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return (null, ContentValidationResult.Invalid("File is not valid UTF-8 text.")); }

        var (program, errors) = XpctProgram.Parse(text);
        if (program is null) return (null, new ContentValidationResult(false, string.Join(Environment.NewLine, errors), fingerprint));
        return (program, new ContentValidationResult(true, $"Valid XPCT program '{program.Name}' ({program.Instructions.Count} instructions).", fingerprint));
    }

    private sealed class ReferenceSession(XpctProgram program, string fingerprint, long maxSteps) : IRuntimeSession
    {
        private readonly ConcurrentQueue<DiagnosticEntry> _log = new();
        private readonly TaskCompletionSource<SessionResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = new();
        private long _outputs;
        private int _state = (int)SessionState.Prepared;
        private ExecutionResult? _result;
        private readonly Stopwatch _clock = new();

        public Guid SessionId { get; } = Guid.NewGuid();
        public SessionState State => (SessionState)Volatile.Read(ref _state);
        public Task<SessionResult> Completion => _completion.Task;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _state, (int)SessionState.Running, (int)SessionState.Prepared) != (int)SessionState.Prepared)
                throw new InvalidOperationException($"Session cannot start from state {State}.");

            Log("Info", $"Starting '{program.Name}' ({fingerprint}), step budget {maxSteps:N0}.");
            _clock.Start();
            _ = Task.Run(Execute, CancellationToken.None);
            return Task.CompletedTask;
        }

        private void Execute()
        {
            try
            {
                var result = XpctInterpreter.Run(program, maxSteps, _stop.Token, _ => Interlocked.Increment(ref _outputs));
                _clock.Stop();
                _result = result;
                var output = result.Output.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToList();
                var (state, summary) = result.Outcome switch
                {
                    ExecutionOutcome.Halted => (SessionState.Completed, $"Program halted normally after {result.Steps:N0} steps with {output.Count} output value(s)."),
                    ExecutionOutcome.BudgetExceeded => (SessionState.Faulted, $"Step budget of {maxSteps:N0} exceeded."),
                    ExecutionOutcome.OutputLimitExceeded => (SessionState.Faulted, "Output limit exceeded."),
                    _ => (SessionState.Faulted, "Program ran past its last instruction without HALT."),
                };
                Finish(state, summary, output);
            }
            catch (OperationCanceledException)
            {
                _clock.Stop();
                Finish(SessionState.Stopped, "Session stopped before the program finished.", []);
            }
#pragma warning disable CA1031 // A runtime fault must be reported as Faulted, never crash the host.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _clock.Stop();
                Finish(SessionState.Faulted, $"Runtime fault: {ex.Message}", []);
            }
        }

        private void Finish(SessionState state, string summary, IReadOnlyList<string> output)
        {
            Volatile.Write(ref _state, (int)state);
            Log(state == SessionState.Completed ? "Info" : "Warning", summary);
            _completion.TrySetResult(new SessionResult(state, summary, output, _clock.Elapsed));
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (State == SessionState.Prepared)
            {
                Finish(SessionState.Stopped, "Session stopped before it started.", []);
                return;
            }
            await _stop.CancelAsync().ConfigureAwait(false);
            await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public RuntimeDiagnostics GetDiagnostics()
        {
            var counters = new Dictionary<string, string>
            {
                ["program"] = program.Name,
                ["fingerprint"] = fingerprint,
                ["instructions"] = program.Instructions.Count.ToString(CultureInfo.InvariantCulture),
                ["outputs"] = Interlocked.Read(ref _outputs).ToString(CultureInfo.InvariantCulture),
                ["elapsedMs"] = _clock.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture),
            };
            if (_result is { } r) counters["steps"] = r.Steps.ToString(CultureInfo.InvariantCulture);
            return new RuntimeDiagnostics(State, _log.ToArray(), counters);
        }

        private void Log(string level, string message) => _log.Enqueue(new DiagnosticEntry(DateTimeOffset.UtcNow, level, message));

        public async ValueTask DisposeAsync()
        {
            if (State == SessionState.Running) await StopAsync(CancellationToken.None).ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}
