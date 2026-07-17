using Linkuity.Core.Interfaces;
using Linkuity.Infrastructure.Local;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Matching.Profiles;
using Linkuity.Mdm.Benchmarks;

namespace Linkuity.Mdm.Benchmarks.Tests;

/// <summary>
/// Smoke-tests <see cref="IngestMeasurement.RunAsync"/> against a real
/// <see cref="FileMetadataStore"/> backed by a temporary directory.
/// </summary>
public sealed class IngestMeasurementTests : IDisposable
{
    private readonly string _workDir;

    public IngestMeasurementTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"linkuity-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    [Fact]
    public async Task RunAsync_ProducesTwoRowsWithIncreasingCumulativeRecords()
    {
        var batches = new List<SyntheticBatch>
        {
            new("CRM",       GenerateRecords(5, "b0-")),
            new("Marketing", GenerateRecords(3, "b1-")),
        };

        var dbPath   = Path.Combine(_workDir, "metadata.db");
        var indexDir = Path.Combine(_workDir, "lucene-index");

        using var index = new LuceneCandidateRetrieval(
            new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir, FuzzyMaxEdits = 0 });
        var profileProvider = new DefaultMatchingProfileProvider(
            DefaultMatchingProfileProvider.BuiltInProfiles());
        IMetadataStore store = new FileMetadataStore(
            new FileMetadataStoreOptions { DatabasePath = dbPath },
            engine: null,
            profileProvider,
            index);

        var setup  = new MeasurementSetup("File");
        var report = await IngestMeasurement.RunAsync(() => store, batches, setup, CancellationToken.None);

        Assert.Equal(2, report.Rows.Count);
        Assert.Equal(5, report.Rows[0].RecordsInBatch);
        Assert.Equal(3, report.Rows[1].RecordsInBatch);
        Assert.True(
            report.Rows[1].CumulativeRecords > report.Rows[0].CumulativeRecords,
            "Cumulative records must increase across batches.");
    }

    private static IReadOnlyList<SyntheticRecord> GenerateRecords(int count, string prefix) =>
        Enumerable.Range(0, count)
            .Select(i => new SyntheticRecord(
                $"{prefix}{i:D4}",
                new Dictionary<string, string>
                {
                    ["id"]     = $"{prefix}{i:D4}",
                    ["source"] = "test",
                    ["name"]   = $"Person{i}",
                    ["email"]  = $"person{i}@example.com",
                    ["phone"]  = "(555) 000-0000",
                }))
            .ToList();

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
