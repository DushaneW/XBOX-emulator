using XboxPc.Domain;

namespace XboxPc.Runtime.Abstractions;

public static class RuntimeContract
{
    /// <summary>Contract version implemented by this build. Major mismatch = incompatible.</summary>
    public static readonly Version Current = new(1, 0);
}

public enum ImplementationState
{
    NotImplemented,
    ResearchOnly,
    Experimental,
    Available,
}

/// <summary>Explicit capability identifiers. A runtime may only do what it declares.</summary>
public static class RuntimeCapabilities
{
    public const string ContentValidate = "content.validate";
    public const string SessionRun = "session.run";
    public const string SessionStop = "session.stop";
    public const string ProgramOutput = "session.output";
    public const string VideoPresent = "video.present";
    public const string AudioOutput = "audio.output";
    public const string ControllerInput = "input.controller";
}

public sealed record RuntimeDescriptor
{
    public required string RuntimeId { get; init; }
    public required ConsoleGeneration Generation { get; init; }
    public required string DisplayName { get; init; }
    public required string Version { get; init; }
    public required ImplementationState State { get; init; }
    public IReadOnlySet<string> Capabilities { get; init; } = new HashSet<string>();
    public required string LicenseNotice { get; init; }
    public string? DocumentationPath { get; init; }
    public Version ContractVersion { get; init; } = RuntimeContract.Current;

    /// <summary>One sentence on what this runtime can and cannot do.</summary>
    public required string Summary { get; init; }

    public bool Has(string capability) => Capabilities.Contains(capability);
}

public sealed record ContentDescriptor(string Path, ConsoleGeneration Generation);

public sealed record ContentValidationResult(bool IsValid, string Message, string? Fingerprint = null)
{
    public static ContentValidationResult Invalid(string message) => new(false, message);
}

public sealed record SessionOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
}

public enum SessionState
{
    Created,
    Prepared,
    Running,
    Completed,
    Stopped,
    Faulted,
}

public sealed record DiagnosticEntry(DateTimeOffset Timestamp, string Level, string Message);

public sealed record RuntimeDiagnostics(
    SessionState State,
    IReadOnlyList<DiagnosticEntry> Entries,
    IReadOnlyDictionary<string, string> Counters);

public sealed record SessionResult(
    SessionState FinalState,
    string Summary,
    IReadOnlyList<string> Output,
    TimeSpan Elapsed);

public interface IRuntimeAdapter
{
    RuntimeDescriptor Descriptor { get; }

    Task<ContentValidationResult> ValidateContentAsync(ContentDescriptor content, CancellationToken cancellationToken);

    Task<IRuntimeSession> PrepareSessionAsync(ContentDescriptor content, SessionOptions options, CancellationToken cancellationToken);
}

public interface IRuntimeSession : IAsyncDisposable
{
    Guid SessionId { get; }
    SessionState State { get; }

    /// <summary>Starts execution. Completes when the session is running, not when it finishes.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Completes when the session reaches Completed, Stopped or Faulted.</summary>
    Task<SessionResult> Completion { get; }

    Task StopAsync(CancellationToken cancellationToken);

    RuntimeDiagnostics GetDiagnostics();
}

/// <summary>Thrown by adapters whose <see cref="ImplementationState"/> does not permit the operation.</summary>
public sealed class RuntimeNotAvailableException(string runtimeId, ImplementationState state)
    : InvalidOperationException($"Runtime '{runtimeId}' is {state}; it cannot validate content or run sessions.")
{
    public string RuntimeId { get; } = runtimeId;
    public ImplementationState State { get; } = state;
}
