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

        // Drive concurrency with a fixed set of dedicated threads (not thread-pool threads) and a
        // barrier, so every worker is inside Retrieve simultaneously. This makes the reopen count
        // deterministic and independent of the host's core count or thread-pool injection
        // heuristics — the earlier Parallel.For version could run entirely on the calling thread on
        // low-core CI, leaving zero extra reopens and flaking.
        const int workers = 8;
        const int callsPerWorker = 20;
        using var barrier = new Barrier(workers);
        var results = new ConcurrentBag<List<string>>();
        var threads = new List<Thread>(workers);
        for (var t = 0; t < workers; t++)
        {
            var thread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var k = 0; k < callsPerWorker; k++)
                    results.Add(index.Retrieve(probe, NoCorpus, Profile).Select(r => r.SourceRecordId).OrderBy(x => x).ToList());
            });
            thread.Start();
            threads.Add(thread);
        }
        foreach (var thread in threads)
            thread.Join();

        Assert.Equal(workers * callsPerWorker, results.Count);
        Assert.All(results, r => Assert.Equal(expected, r));          // no torn reads / corruption — outcome-neutral

        var extraReopens = index.ReopenCount - reopensAfterWarm;
        // Per-thread readers: each of the `workers` distinct worker threads opens its own committed
        // reader once for the unchanged index. A single shared reader would leave this at 0, so the
        // count tracks the worker-thread count — deterministic because we control the threads.
        Assert.InRange(extraReopens, 1, workers);
    }
}
