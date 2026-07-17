using Linkuity.Mdm.Benchmarks;

namespace Linkuity.Mdm.Benchmarks.Tests;

public class SyntheticDatasetGeneratorTests
{
    [Fact]
    public void Generate_IsDeterministicForASeed()
    {
        var options = new SyntheticDatasetOptions(TotalRecords: 200, BatchSize: 50,
            Sources: ["CRM", "Marketing"], DuplicateRate: 0.2, Seed: 42);
        var a = new SyntheticDatasetGenerator().Generate(options);
        var b = new SyntheticDatasetGenerator().Generate(options);

        Assert.Equal(200, a.Sum(batch => batch.Records.Count));
        Assert.Equal(4, a.Count); // 200 / 50
        Assert.Equal(
            a.SelectMany(x => x.Records).Select(r => r.SourceRecordId + ":" + r.Fields["email"]),
            b.SelectMany(x => x.Records).Select(r => r.SourceRecordId + ":" + r.Fields["email"]));
    }

    [Fact]
    public void Generate_ProducesNearDuplicatesAtTheConfiguredRate()
    {
        var options = new SyntheticDatasetOptions(1000, 1000, ["CRM"], DuplicateRate: 0.3, Seed: 7);
        var records = new SyntheticDatasetGenerator().Generate(options).SelectMany(b => b.Records).ToList();
        var distinctEmails = records.Select(r => r.Fields["email"]).Distinct().Count();
        // ~30% duplicates → fewer distinct emails than records, within tolerance.
        Assert.InRange(distinctEmails, 650, 750);
    }
}
