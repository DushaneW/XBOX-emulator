namespace XboxPc.Domain;

/// <summary>Ordered from least to most progress. "Perfect" deliberately does not exist.</summary>
public enum CompatibilityStatus
{
    NotTested = 0,
    Researching = 1,
    DoesNotBoot = 2,
    Boots = 3,
    Intro = 4,
    InGame = 5,
    Playable = 6,
    Verified = 7,
}

public enum ReportSource
{
    /// <summary>Test executed on this machine by the user and confirmed by a named reviewer.</summary>
    LocalVerified,

    /// <summary>Imported / third-party report; never treated as locally verified.</summary>
    Community,
}

public sealed record HostInfo(string Os, string Cpu, string Gpu, string Ram, string GpuDriver);

public sealed record CompatibilityReport
{
    public Guid ReportId { get; init; } = Guid.NewGuid();
    public required Guid GameEntryId { get; init; }
    public required string RuntimeId { get; init; }
    public required string RuntimeVersion { get; init; }
    public DateTimeOffset TestDate { get; init; } = DateTimeOffset.UtcNow;
    public required HostInfo Host { get; init; }
    public string? TestAssetFingerprint { get; init; }
    public string TestProcedure { get; init; } = "";
    public required CompatibilityStatus Outcome { get; init; }
    public ReportSource Source { get; init; } = ReportSource.LocalVerified;

    /// <summary>Measured only. Null means "not measured", never zero.</summary>
    public double? AverageFps { get; init; }
    public string? FrameTimeSummary { get; init; }
    public string? AudioResult { get; init; }
    public string? InputResult { get; init; }
    public IReadOnlyList<string> GraphicsIssues { get; init; } = [];
    public string? CrashDetails { get; init; }
    public IReadOnlyList<string> EvidenceReferences { get; init; } = [];
    public string? Reviewer { get; init; }
    public bool ReviewerConfirmed { get; init; }
    public string? ReproducibilityNotes { get; init; }
}

public static class CompatibilityRules
{
    /// <summary>
    /// Returns validation errors. Any status above <see cref="CompatibilityStatus.Researching"/> is a claim and
    /// requires evidence, an asset fingerprint, a procedure and a confirmed reviewer.
    /// </summary>
    public static IReadOnlyList<string> Validate(CompatibilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var errors = new List<string>();
        if (!Enum.IsDefined(report.Outcome)) errors.Add("Unknown outcome status.");
        if (string.IsNullOrWhiteSpace(report.RuntimeId)) errors.Add("Runtime ID is required.");
        if (string.IsNullOrWhiteSpace(report.RuntimeVersion)) errors.Add("Runtime build/version is required.");
        if (string.IsNullOrWhiteSpace(report.Host.Os)) errors.Add("Host OS is required.");
        if (report.TestDate > DateTimeOffset.UtcNow.AddMinutes(5)) errors.Add("Test date cannot be in the future.");
        if (report.AverageFps is { } fps && (!double.IsFinite(fps) || fps < 0 || fps > 1000))
            errors.Add("Average FPS must be a measured value between 0 and 1000, or left empty.");

        if (report.Outcome > CompatibilityStatus.Researching)
        {
            if (string.IsNullOrWhiteSpace(report.TestAssetFingerprint)) errors.Add("A test asset fingerprint (hash) is required for this status.");
            if (string.IsNullOrWhiteSpace(report.TestProcedure)) errors.Add("The exact test procedure is required for this status.");
            if (!report.EvidenceReferences.Any(e => !string.IsNullOrWhiteSpace(e))) errors.Add("At least one evidence reference (log, video, screenshot) is required for this status.");
            if (string.IsNullOrWhiteSpace(report.Reviewer)) errors.Add("A reviewer name is required for this status.");
            if (!report.ReviewerConfirmed) errors.Add("The reviewer must confirm the result.");
        }

        if (report.Outcome == CompatibilityStatus.Verified && report.Source != ReportSource.LocalVerified)
            errors.Add("Only locally verified reports can carry the Verified status.");

        return errors;
    }

    public static string ToDisplay(this CompatibilityStatus status) => status switch
    {
        CompatibilityStatus.NotTested => "Not Tested",
        CompatibilityStatus.Researching => "Researching",
        CompatibilityStatus.DoesNotBoot => "Does Not Boot",
        CompatibilityStatus.Boots => "Boots",
        CompatibilityStatus.Intro => "Intro",
        CompatibilityStatus.InGame => "In-Game",
        CompatibilityStatus.Playable => "Playable",
        CompatibilityStatus.Verified => "Verified",
        _ => status.ToString(),
    };
}
