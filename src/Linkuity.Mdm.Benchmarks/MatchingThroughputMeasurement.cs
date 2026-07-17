using System.Diagnostics;
using System.Globalization;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;

namespace Linkuity.Mdm.Benchmarks;

/// <summary>
/// CPU-only micro-benchmark for the parallelizable ingest hot path: it times the per-record
/// engine.Resolve loop (Lucene retrieval + similarity scoring) at increasing degrees of
/// parallelism, isolating matching cost from Postgres fsync/checkpoint I/O. This is the honest
/// instrument for the parallel-matching speedup: same work, no database in the loop.
/// </summary>
internal static class MatchingThroughputMeasurement
{
    public static int Run(IReadOnlyDictionary<string, string> options)
    {
        var corpusSize = options.TryGetValue("corpus", out var cs) ? int.Parse(cs, CultureInfo.InvariantCulture) : 100_000;
        var batchSize = options.TryGetValue("batch", out var bs) ? int.Parse(bs, CultureInfo.InvariantCulture) : 1_000;
        var maxCandidates = options.TryGetValue("max-candidates", out var mc) ? int.Parse(mc, CultureInfo.InvariantCulture) : 50;
        var iterations = options.TryGetValue("iterations", out var it) ? int.Parse(it, CultureInfo.InvariantCulture) : 3;
        var indexDir = options.TryGetValue("index-dir", out var idx)
            ? idx
            : Path.Combine(Path.GetTempPath(), "linkuity-matching-bench", Guid.NewGuid().ToString("N"));

        var profile = WithLuceneRetrieval(DefaultMatchingProfileProvider.CreatePersonProfile());
        var dataset = new SyntheticDatasetGenerator().Generate(
            new SyntheticDatasetOptions(corpusSize + batchSize, batchSize, ["crm"], DuplicateRate: 0.2, Seed: 42));
        var all = dataset.SelectMany(b => b.Records).ToList();

        using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir, FuzzyMaxEdits = 0, MaxCandidates = maxCandidates });
        var engine = MatchingDefaults.CreateEngine(index);

        var corpus = all.Take(corpusSize).Select((r, i) => ToRecord(engine, profile, r, i)).ToList();
        foreach (var rec in corpus) index.Index(rec);
        index.Commit();

        var incoming = all.Skip(corpusSize).Take(batchSize).Select((r, i) => ToRecord(engine, profile, r, corpusSize + i)).ToList();

        Console.WriteLine($"corpus={corpusSize} batch={batchSize} maxCandidates={maxCandidates} iterations={iterations} cores={Environment.ProcessorCount}");
        double baseline = 0;
        foreach (var dop in new[] { 1, Environment.ProcessorCount })
        {
            var best = double.MaxValue;
            for (var iter = 0; iter < iterations; iter++)
            {
                var sw = Stopwatch.StartNew();
                Parallel.ForEach(incoming, new ParallelOptions { MaxDegreeOfParallelism = dop }, record =>
                {
                    var _ = engine.Resolve(record, Array.Empty<EntityRecord>(), profile);
                });
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }

            if (dop == 1) baseline = best;
            var speedup = baseline / best;
            Console.WriteLine($"dop={dop,3}  best={best,10:F1} ms  speedup={speedup,5:F2}x");
        }

        return 0;
    }

    private static MatchingProfile WithLuceneRetrieval(MatchingProfile p) => new()
    {
        ContentType = p.ContentType,
        Fields = p.Fields,
        NormalizationStrategy = p.NormalizationStrategy,
        BlockingStrategies = p.BlockingStrategies,
        CandidateRetrievalStrategy = "lucene",
        SimilarityStrategy = p.SimilarityStrategy,
        ScoringStrategy = p.ScoringStrategy,
        DecisionStrategy = p.DecisionStrategy,
        ClusteringStrategy = p.ClusteringStrategy,
        AutoMatchThreshold = p.AutoMatchThreshold,
        ReviewThreshold = p.ReviewThreshold
    };

    private static EntityRecord ToRecord(IMatchingEngine engine, MatchingProfile profile, SyntheticRecord r, int i)
    {
        var rec = new EntityRecord
        {
            Id = Guid.Parse($"33333333-0000-0000-0000-{i:D12}"),
            ProjectId = Guid.Empty,
            SourceId = Guid.Empty,
            IngestBatchId = Guid.Empty,
            SourceRecordId = r.SourceRecordId,
            Fields = new Dictionary<string, string>(r.Fields),
            CreatedAt = new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero)
        };
        return new EntityRecord
        {
            Id = rec.Id, ProjectId = rec.ProjectId, SourceId = rec.SourceId, IngestBatchId = rec.IngestBatchId,
            SourceRecordId = rec.SourceRecordId, Fields = rec.Fields,
            BlockingKeys = engine.GenerateBlockingKeys(rec, profile), CreatedAt = rec.CreatedAt
        };
    }
}
