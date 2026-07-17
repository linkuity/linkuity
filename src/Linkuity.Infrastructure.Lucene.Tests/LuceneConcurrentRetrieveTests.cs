using System.Collections.Concurrent;
using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Infrastructure.Lucene.Tests;

public sealed class LuceneConcurrentRetrieveTests
{
    private static readonly MatchingProfile Profile = DefaultMatchingProfileProvider.CreatePersonProfile();
    private static readonly IReadOnlyCollection<EntityRecord> NoCorpus = [];

    [Fact]
    public void Retrieve_UnderConcurrency_IsStable_AndUsesPerThreadReaders()
    {
        using var index = new LuceneCandidateRetrieval(
            new LuceneCandidateRetrievalOptions { IndexDirectory = LuceneTestRecords.TempDir() });

        for (var i = 0; i < 200; i++)
            index.Index(LuceneTestRecords.Person($"r{i}",
                new Dictionary<string, string> { ["email"] = $"user{i}@example.com", ["name"] = $"Person {i}" }));
        index.Commit();

        var probe = LuceneTestRecords.Person("probe", new Dictionary<string, string> { ["email"] = "user0@example.com" });

        // Warm one reader (single-threaded), capture the baseline result + reopen count.
        var expected = index.Retrieve(probe, NoCorpus, Profile).Select(r => r.SourceRecordId).OrderBy(x => x).ToList();
        var reopensAfterWarm = index.ReopenCount;

        const int dop = 16;
        var results = new ConcurrentBag<List<string>>();
        Parallel.For(0, 500, new ParallelOptions { MaxDegreeOfParallelism = dop }, _ =>
            results.Add(index.Retrieve(probe, NoCorpus, Profile).Select(r => r.SourceRecordId).OrderBy(x => x).ToList()));

        Assert.Equal(500, results.Count);
        Assert.All(results, r => Assert.Equal(expected, r));          // no torn reads / corruption — outcome-neutral

        var extraReopens = index.ReopenCount - reopensAfterWarm;
        // Per-thread readers: each worker thread opens its own committed reader at most once for the
        // unchanged index — so reopens grow, but stay bounded (no per-call reopen storm). A single
        // shared reader would leave this at 0. The upper bound is a generous sanity cap, not an
        // exact bound on `dop` — the thread pool may use more than `dop` distinct threads over the
        // life of the loop, so we cap well above that instead of asserting exact thread reuse.
        Assert.InRange(extraReopens, 1, Environment.ProcessorCount * 4);
    }
}
