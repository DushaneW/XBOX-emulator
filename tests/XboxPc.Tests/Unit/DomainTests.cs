using XboxPc.Domain;

namespace XboxPc.Tests.Unit;

public class CompatibilityRulesTests
{
    private static readonly HostInfo Host = new("Windows 11", "CPU", "GPU", "16 GB", "1.0");

    private static CompatibilityReport Report(CompatibilityStatus status) => new()
    {
        GameEntryId = Guid.NewGuid(),
        RuntimeId = "rt",
        RuntimeVersion = "1.0",
        Host = Host,
        Outcome = status,
    };

    [Theory]
    [InlineData(CompatibilityStatus.NotTested)]
    [InlineData(CompatibilityStatus.Researching)]
    public void Low_statuses_need_no_evidence(CompatibilityStatus status) =>
        Assert.Empty(CompatibilityRules.Validate(Report(status)));

    [Theory]
    [InlineData(CompatibilityStatus.DoesNotBoot)]
    [InlineData(CompatibilityStatus.Boots)]
    [InlineData(CompatibilityStatus.InGame)]
    [InlineData(CompatibilityStatus.Playable)]
    [InlineData(CompatibilityStatus.Verified)]
    public void Claims_without_evidence_are_rejected(CompatibilityStatus status)
    {
        var errors = CompatibilityRules.Validate(Report(status));
        Assert.Contains(errors, e => e.Contains("fingerprint", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("reviewer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("procedure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Complete_claim_is_accepted()
    {
        var report = Report(CompatibilityStatus.Boots) with
        {
            TestAssetFingerprint = "sha256:abc",
            TestProcedure = "launched",
            EvidenceReferences = ["log.jsonl"],
            Reviewer = "QA",
            ReviewerConfirmed = true,
        };
        Assert.Empty(CompatibilityRules.Validate(report));
    }

    [Fact]
    public void Unconfirmed_reviewer_is_rejected()
    {
        var report = Report(CompatibilityStatus.Boots) with
        {
            TestAssetFingerprint = "sha256:abc", TestProcedure = "x", EvidenceReferences = ["e"], Reviewer = "QA", ReviewerConfirmed = false,
        };
        Assert.Contains(CompatibilityRules.Validate(report), e => e.Contains("confirm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Community_reports_cannot_be_verified()
    {
        var report = Report(CompatibilityStatus.Verified) with
        {
            Source = ReportSource.Community, TestAssetFingerprint = "h", TestProcedure = "p", EvidenceReferences = ["e"], Reviewer = "r", ReviewerConfirmed = true,
        };
        Assert.Contains(CompatibilityRules.Validate(report), e => e.Contains("locally verified", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(5000)]
    public void Implausible_fps_is_rejected(double fps) =>
        Assert.NotEmpty(CompatibilityRules.Validate(Report(CompatibilityStatus.Researching) with { AverageFps = fps }));

    [Fact]
    public void There_is_no_perfect_status() =>
        Assert.DoesNotContain(Enum.GetNames<CompatibilityStatus>(), n => n.Contains("Perfect", StringComparison.OrdinalIgnoreCase));
}

public class InputMapTests
{
    [Fact]
    public void Default_map_is_valid() => Assert.Empty(InputMap.CreateDefault().Validate());

    [Fact]
    public void Missing_action_is_rejected()
    {
        var bindings = InputMap.CreateDefault().Bindings.ToDictionary(k => k.Key, v => v.Value);
        bindings.Remove(UiAction.Back);
        var errors = (InputMap.CreateDefault() with { Bindings = bindings }).Validate();
        Assert.Contains(errors, e => e.Contains("Back", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_binding_is_rejected()
    {
        var bindings = InputMap.CreateDefault().Bindings.ToDictionary(k => k.Key, v => v.Value);
        bindings[UiAction.Menu] = ["Pad.A"];
        Assert.Contains((InputMap.CreateDefault() with { Bindings = bindings }).Validate(), e => e.Contains("both", StringComparison.Ordinal));
    }

    [Fact]
    public void Accept_needs_a_controller_binding()
    {
        var bindings = InputMap.CreateDefault().Bindings.ToDictionary(k => k.Key, v => v.Value);
        bindings[UiAction.Accept] = ["Key.Enter"];
        Assert.Contains((InputMap.CreateDefault() with { Bindings = bindings }).Validate(), e => e.Contains("controller", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-0.1, 1)]
    [InlineData(0.95, 1)]
    [InlineData(0.2, 0.1)]
    [InlineData(0.2, 5)]
    [InlineData(double.NaN, 1)]
    public void Out_of_range_dead_zone_or_sensitivity_is_rejected(double deadZone, double sensitivity) =>
        Assert.NotEmpty((InputMap.CreateDefault() with { LeftStickDeadZone = deadZone, StickSensitivity = sensitivity }).Validate());

    [Theory]
    [InlineData("Pad.A", true)]
    [InlineData("Key.F5", true)]
    [InlineData("Pad.Turbo", false)]
    [InlineData("Key.", false)]
    [InlineData("Mouse.Left", false)]
    [InlineData("Key.a;drop", false)]
    public void Binding_format(string binding, bool expected) => Assert.Equal(expected, InputMap.IsWellFormed(binding));

    [Fact]
    public void Preferences_normalize_scale() =>
        Assert.Equal(UserPreferences.MaxUiScale, new UserPreferences { UiScale = 9 }.Normalized().UiScale);
}
