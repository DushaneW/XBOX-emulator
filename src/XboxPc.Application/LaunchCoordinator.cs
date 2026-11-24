using XboxPc.Domain;
using XboxPc.Runtime.Abstractions;

namespace XboxPc.Application;

public sealed class RuntimeRegistry
{
    private readonly Dictionary<string, IRuntimeAdapter> _adapters;

    public RuntimeRegistry(IEnumerable<IRuntimeAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = new Dictionary<string, IRuntimeAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            if (!_adapters.TryAdd(adapter.Descriptor.RuntimeId, adapter))
                throw new ArgumentException($"Duplicate runtime id '{adapter.Descriptor.RuntimeId}'.", nameof(adapters));
        }
    }

    public IReadOnlyList<RuntimeDescriptor> Descriptors => _adapters.Values.Select(a => a.Descriptor).ToList();

    public IRuntimeAdapter? Find(string? runtimeId) => runtimeId is not null && _adapters.TryGetValue(runtimeId, out var a) ? a : null;

    public IReadOnlyList<RuntimeDescriptor> ForGeneration(ConsoleGeneration generation) =>
        Descriptors.Where(d => d.Generation == generation).ToList();

    /// <summary>The runtime that would be used for an entry: the preferred one if it matches, else the first for the generation.</summary>
    public RuntimeDescriptor? Resolve(ConsoleGeneration generation, string? preferredRuntimeId)
    {
        var preferred = Find(preferredRuntimeId)?.Descriptor;
        if (preferred is not null && preferred.Generation == generation) return preferred;
        return ForGeneration(generation).OrderByDescending(d => d.State).FirstOrDefault();
    }
}

public enum LaunchStatus
{
    Refused,
    Completed,
    Stopped,
    Faulted,
    TimedOut,
}

public sealed record LaunchOutcome(
    LaunchStatus Status,
    string Message,
    RuntimeDescriptor? Runtime,
    string? ContentFingerprint = null,
    SessionResult? Result = null,
    RuntimeDiagnostics? Diagnostics = null)
{
    public static LaunchOutcome Refuse(string message, RuntimeDescriptor? runtime = null) => new(LaunchStatus.Refused, message, runtime);
}

public interface ILaunchProgress
{
    void Report(string stage);
}

/// <summary>
/// Validates launch requests and drives a runtime session. It never produces a success outcome unless the runtime
/// actually completed a session.
/// </summary>
public sealed class LaunchCoordinator(RuntimeRegistry registry, LibraryService library, IAppLog log, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Why a launch would be refused, or null if it may proceed. Used by the UI to enable/disable Launch.</summary>
    public static string? CheckLaunchable(RuntimeDescriptor? runtime, GameEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (runtime is null) return $"No runtime is registered for {entry.Generation.ToDisplay()}.";
        if (runtime.ContractVersion.Major != RuntimeContract.Current.Major)
            return $"{runtime.DisplayName} implements contract v{runtime.ContractVersion}; this app requires v{RuntimeContract.Current.Major}.x.";
        if (runtime.State == ImplementationState.Experimental)
            return $"{runtime.DisplayName} is Experimental: only .xbe program files start, in the emulator window (Play).";
        if (runtime.State != ImplementationState.Available)
            return $"{runtime.DisplayName} is {runtime.State switch { ImplementationState.ResearchOnly => "Research Only", ImplementationState.NotImplemented => "Not Implemented", _ => runtime.State.ToString() }}. It cannot run software.";
        if (!runtime.Has(RuntimeCapabilities.SessionRun)) return $"{runtime.DisplayName} does not declare the '{RuntimeCapabilities.SessionRun}' capability.";
        if (runtime.Generation != entry.Generation) return $"{runtime.DisplayName} does not support {entry.Generation.ToDisplay()} entries.";
        if (string.IsNullOrWhiteSpace(entry.ContentPath)) return "This entry has no content path.";
        return null;
    }

    public async Task<LaunchOutcome> LaunchAsync(GameEntry entry, string? preferredRuntimeId, TimeSpan timeout, ILaunchProgress? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var runtime = registry.Resolve(entry.Generation, preferredRuntimeId);
        if (CheckLaunchable(runtime, entry) is { } refusal)
        {
            log.Info("Launch", $"Refused '{entry.DisplayName}': {refusal}");
            return LaunchOutcome.Refuse(refusal, runtime);
        }

        var (path, pathError) = SafePath.Validate(entry.ContentPath, mustExist: true);
        if (pathError is not null) return LaunchOutcome.Refuse($"Content path: {pathError}", runtime);

        var adapter = registry.Find(runtime!.RuntimeId)!;
        var content = new ContentDescriptor(path!, entry.Generation);
        using var timeoutCts = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        IRuntimeSession? session = null;
        try
        {
            progress?.Report("Validating content");
            var validation = await adapter.ValidateContentAsync(content, linked.Token).ConfigureAwait(false);
            if (!validation.IsValid) return LaunchOutcome.Refuse($"Content rejected by {runtime.DisplayName}: {validation.Message}", runtime);

            progress?.Report("Preparing session");
            session = await adapter.PrepareSessionAsync(content, new SessionOptions { Timeout = timeout }, linked.Token).ConfigureAwait(false);

            progress?.Report("Running");
            log.Info("Launch", $"Session {session.SessionId} starting '{entry.DisplayName}' on {runtime.RuntimeId} {runtime.Version}.");
            await session.StartAsync(linked.Token).ConfigureAwait(false);
            await library.MarkLaunchedAsync(entry.Id, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);

            SessionResult result;
            try
            {
                result = await session.Completion.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                progress?.Report("Stopping");
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5), _time);
                try { await session.StopAsync(stopCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { log.Warn("Launch", $"Session {session.SessionId} did not stop within 5 s."); }

                var timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
                var diag = session.GetDiagnostics();
                var message = timedOut ? $"Timed out after {timeout.TotalSeconds:0.#} s; session stopped." : "Cancelled by user; session stopped.";
                log.Info("Launch", $"Session {session.SessionId}: {message}");
                return new LaunchOutcome(timedOut ? LaunchStatus.TimedOut : LaunchStatus.Stopped, message, runtime, validation.Fingerprint, null, diag);
            }

            var status = result.FinalState switch
            {
                SessionState.Completed => LaunchStatus.Completed,
                SessionState.Stopped => LaunchStatus.Stopped,
                _ => LaunchStatus.Faulted,
            };
            log.Info("Launch", $"Session {session.SessionId} ended: {result.FinalState} ({result.Summary})");
            return new LaunchOutcome(status, result.Summary, runtime, validation.Fingerprint, result, session.GetDiagnostics());
        }
        catch (RuntimeNotAvailableException ex)
        {
            return LaunchOutcome.Refuse(ex.Message, runtime);
        }
        catch (OperationCanceledException)
        {
            var timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            return new LaunchOutcome(timedOut ? LaunchStatus.TimedOut : LaunchStatus.Stopped,
                timedOut ? "Timed out before the session started." : "Cancelled before the session started.", runtime);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            log.Error("Launch", $"Launch of '{entry.DisplayName}' failed.", ex);
            return new LaunchOutcome(LaunchStatus.Faulted, $"Launch failed: {ex.Message}", runtime);
        }
        finally
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
