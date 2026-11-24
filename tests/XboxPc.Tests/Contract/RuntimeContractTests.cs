using XboxPc.Application;
using XboxPc.Domain;
using XboxPc.Runtime.Abstractions;
using XboxPc.Runtimes;
using XboxPc.Runtimes.Reference;

namespace XboxPc.Tests.Contract;

/// <summary>Contract tests applied to every runtime shipped in the app.</summary>
public class RuntimeContractTests
{
    public static TheoryData<string> AllRuntimeIds()
    {
        var data = new TheoryData<string>();
        foreach (var r in BuiltInRuntimes.Create()) data.Add(r.Descriptor.RuntimeId);
        return data;
    }

    private static IRuntimeAdapter Get(string id) => BuiltInRuntimes.Create().Single(r => r.Descriptor.RuntimeId == id);

    [Theory]
    [MemberData(nameof(AllRuntimeIds))]
    public void Descriptor_is_complete_and_contract_compatible(string id)
    {
        var d = Get(id).Descriptor;
        Assert.False(string.IsNullOrWhiteSpace(d.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(d.Version));
        Assert.False(string.IsNullOrWhiteSpace(d.LicenseNotice));
        Assert.False(string.IsNullOrWhiteSpace(d.Summary));
        Assert.Equal(RuntimeContract.Current.Major, d.ContractVersion.Major);
        Assert.True(Enum.IsDefined(d.State));
    }

    [Theory]
    [MemberData(nameof(AllRuntimeIds))]
    public async Task Non_available_runtimes_cannot_do_anything(string id)
    {
        var adapter = Get(id);
        if (adapter.Descriptor.State == ImplementationState.Available) return;

        Assert.False(adapter.Descriptor.Has(RuntimeCapabilities.SessionRun));
        var content = new ContentDescriptor(Samples.Path("fibonacci.xpct"), adapter.Descriptor.Generation);
        await Assert.ThrowsAsync<RuntimeNotAvailableException>(() => adapter.ValidateContentAsync(content, CancellationToken.None));
        await Assert.ThrowsAsync<RuntimeNotAvailableException>(() => adapter.PrepareSessionAsync(content, new SessionOptions(), CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(AllRuntimeIds))]
    public void Available_runtimes_declare_required_capabilities(string id)
    {
        var d = Get(id).Descriptor;
        if (d.State != ImplementationState.Available) return;
        Assert.True(d.Has(RuntimeCapabilities.SessionRun));
        Assert.True(d.Has(RuntimeCapabilities.ContentValidate));
    }

    [Fact]
    public void Series_xs_is_research_only_until_a_real_implementation_exists()
    {
        var series = BuiltInRuntimes.Create().Single(r => r.Descriptor.Generation == ConsoleGeneration.SeriesXS).Descriptor;
        Assert.Equal(ImplementationState.ResearchOnly, series.State);
        Assert.Empty(series.Capabilities);
    }

    [Fact]
    public void Only_the_reference_runtime_is_available_and_it_is_not_a_console_runtime()
    {
        var available = BuiltInRuntimes.Create().Where(r => r.Descriptor.State == ImplementationState.Available).ToList();
        var single = Assert.Single(available);
        Assert.Equal(ReferenceTestRuntime.Id, single.Descriptor.RuntimeId);
        Assert.Equal(ConsoleGeneration.Other, single.Descriptor.Generation);
        Assert.Contains("Not an Xbox emulator", single.Descriptor.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void No_shipped_runtime_is_a_fake() =>
        Assert.DoesNotContain(BuiltInRuntimes.Create(), r => r.GetType().Name.Contains("Fake", StringComparison.OrdinalIgnoreCase)
            || r.Descriptor.DisplayName.Contains("Fake", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Registry_rejects_duplicate_ids() =>
        Assert.Throws<ArgumentException>(() => new RuntimeRegistry([new ReferenceTestRuntime(), new ReferenceTestRuntime()]));
}
