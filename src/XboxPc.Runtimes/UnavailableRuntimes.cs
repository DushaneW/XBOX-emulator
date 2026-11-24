using XboxPc.Domain;
using XboxPc.Runtime.Abstractions;

namespace XboxPc.Runtimes;

/// <summary>
/// Base for generation adapters that do not execute anything. Every operational call throws, so these adapters
/// can never report a successful validation or launch.
/// </summary>
public abstract class UnavailableRuntimeAdapter : IRuntimeAdapter
{
    public abstract RuntimeDescriptor Descriptor { get; }

    public Task<ContentValidationResult> ValidateContentAsync(ContentDescriptor content, CancellationToken cancellationToken) =>
        throw new RuntimeNotAvailableException(Descriptor.RuntimeId, Descriptor.State);

    public Task<IRuntimeSession> PrepareSessionAsync(ContentDescriptor content, SessionOptions options, CancellationToken cancellationToken) =>
        throw new RuntimeNotAvailableException(Descriptor.RuntimeId, Descriptor.State);
}

public sealed class SeriesXsResearchAdapter : UnavailableRuntimeAdapter
{
    public override RuntimeDescriptor Descriptor { get; } = new()
    {
        RuntimeId = "xpc.series-xs.research",
        Generation = ConsoleGeneration.SeriesXS,
        DisplayName = "Xbox Series X|S (research)",
        Version = "0.0.0-research",
        State = ImplementationState.ResearchOnly,
        LicenseNotice = "Original project code. No Microsoft code, firmware or keys are included.",
        DocumentationPath = "docs/phase0/SERIES_XS_FEASIBILITY.md",
        Summary = "Research only. Cannot run any Series X|S software. Implementation is NO-GO: no lawful path to execute encrypted console content exists.",
    };
}

public sealed class XboxOneResearchAdapter : UnavailableRuntimeAdapter
{
    public override RuntimeDescriptor Descriptor { get; } = new()
    {
        RuntimeId = "xpc.xbox-one.research",
        Generation = ConsoleGeneration.XboxOne,
        DisplayName = "Xbox One (research)",
        Version = "0.0.0-research",
        State = ImplementationState.ResearchOnly,
        LicenseNotice = "Original project code. No Microsoft code, firmware or keys are included.",
        DocumentationPath = "docs/phase0/SERIES_XS_FEASIBILITY.md",
        Summary = "Research only. Shares the Series X|S blockers (encrypted content, proprietary OS). Cannot run any software.",
    };
}

public sealed class Xbox360Adapter : UnavailableRuntimeAdapter
{
    public override RuntimeDescriptor Descriptor { get; } = new()
    {
        RuntimeId = "xpc.xbox360",
        Generation = ConsoleGeneration.Xbox360,
        DisplayName = "Xbox 360",
        Version = "0.0.0",
        State = ImplementationState.NotImplemented,
        LicenseNotice = "Not implemented. A future adapter may launch an external open-source emulator (e.g. Xenia, BSD-3-Clause) after license review.",
        Summary = "Not implemented. Planned for Phase 5 evaluation as an external runtime.",
    };
}

public sealed class OriginalXboxAdapter : UnavailableRuntimeAdapter
{
    public override RuntimeDescriptor Descriptor { get; } = new()
    {
        RuntimeId = "xpc.original-xbox",
        Generation = ConsoleGeneration.OriginalXbox,
        DisplayName = "Original Xbox",
        Version = "0.3.0-experimental",
        State = ImplementationState.Experimental,
        LicenseNotice = "Original emulator engine (src/XboxPc.Emulation.*). No Microsoft BIOS, kernel, keys or game files are included or needed.",
        DocumentationPath = "docs/engine/13_M3_REPORT.md",
        Summary = "Built-in emulator. Plays .xbe programs (homebrew) from the library or Home in the emulator window. Commercial games don't boot yet.",
    };
}

public static class BuiltInRuntimes
{
    public static IReadOnlyList<IRuntimeAdapter> Create() =>
    [
        new Reference.ReferenceTestRuntime(),
        new SeriesXsResearchAdapter(),
        new XboxOneResearchAdapter(),
        new Xbox360Adapter(),
        new OriginalXboxAdapter(),
    ];
}
