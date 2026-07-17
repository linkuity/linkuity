using System.Text;
using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Mdm.Resolution;

namespace Linkuity.Mdm.Tests;

public sealed class IncrementalResolverParallelTests
{
    private static readonly MatchingProfile PersonProfile = DefaultMatchingProfileProvider.CreatePersonProfile();

    private static IncrementalResolver NewResolver(int dop)
        => new(MatchingDefaults.CreateEngine(), hasIndex: false, degreeOfParallelism: dop);

    // Deterministic incoming batch: a hot block of identical records (many tied auto-match
    // edges — stresses the parallel reduce and first-wins tie-break), a second smaller
    // duplicate group, and singletons. Fixed GUIDs so canonical output is stable across runs.
    private static IReadOnlyList<EntityRecord> Batch(IncrementalResolver resolver, Guid projectId, Guid sourceId, Guid batchId, DateTimeOffset now)
    {
        EntityRecord Rec(int i, string name, string email, string phone)
        {
            var rec = new EntityRecord
            {
                Id = Guid.Parse($"11111111-0000-0000-0000-{i:D12}"),
                ProjectId = projectId,
                SourceId = sourceId,
                IngestBatchId = batchId,
                SourceRecordId = $"r{i}",
                Fields = new Dictionary<string, string> { ["name"] = name, ["email"] = email, ["phone"] = phone },
                CreatedAt = now
            };
            return new EntityRecord
            {
                Id = rec.Id, ProjectId = rec.ProjectId, SourceId = rec.SourceId, IngestBatchId = rec.IngestBatchId,
                SourceRecordId = rec.SourceRecordId, Fields = rec.Fields,
                BlockingKeys = resolver.GenerateBlockingKeys(rec, PersonProfile), CreatedAt = rec.CreatedAt
            };
        }

        var records = new List<EntityRecord>();
        for (var i = 0; i < 12; i++) records.Add(Rec(i, "Ada Lovelace", "hot@acme.com", "555-0100"));       // one hot cluster
        for (var i = 12; i < 16; i++) records.Add(Rec(i, "Alan Turing", "two@acme.com", "555-0200"));         // second cluster
        for (var i = 16; i < 22; i++) records.Add(Rec(i, $"Distinct {i}", $"user{i}@acme.com", $"555-03{i:D2}")); // singletons
        return records;
    }

    private static (IncrementalIngestResult Result, MutationSet Mutations) Run(int dop)
    {
        var projectId = Guid.Parse("22222222-0000-0000-0000-000000000001");
        var sourceId = Guid.Parse("22222222-0000-0000-0000-000000000002");
        var batchId = Guid.Parse("22222222-0000-0000-0000-000000000003");
        var now = new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero);

        var resolver = NewResolver(dop);
        var incoming = Batch(resolver, projectId, sourceId, batchId, now);
        var request = new IncrementalIngestRequest(projectId, sourceId, batchId, incoming, AutoMatchThreshold: 0.90, ReviewThreshold: 0.75);
        var project = new Project { Id = projectId, Name = "MDM", ContentType = "person", CreatedAt = now };
        var context = new InMemoryResolutionContext(); // empty: fully net-new batch
        return resolver.Resolve(request, project, PersonProfile, incoming, context, now.AddMinutes(1));
    }

    private static string Canon(IncrementalIngestResult r, MutationSet m)
    {
        static string Ord(Guid a, Guid b) => a.CompareTo(b) <= 0 ? $"{a}->{b}" : $"{b}->{a}";
        static string Fields(IReadOnlyDictionary<string, string> f)
            => string.Join(",", f.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

        var sb = new StringBuilder();
        sb.AppendLine($"result={r.RecordsAdded}/{r.AutoMatches}/{r.ReviewTasks}/{r.SingletonClusters}/{r.GoldenRecordVersionsCreated}");
        foreach (var l in m.EdgesToInsert.Select(e => $"edge {Ord(e.LeftEntityRecordId, e.RightEntityRecordId)} {e.Score:F10} {e.Decision}").OrderBy(x => x, StringComparer.Ordinal)) sb.AppendLine(l);
        foreach (var l in m.ClustersToUpsert.Select(c => $"cluster [{string.Join(",", c.MemberEntityRecordIds.OrderBy(g => g))}] {c.Status} merged={c.MergedIntoClusterId is not null}").OrderBy(x => x, StringComparer.Ordinal)) sb.AppendLine(l);
        foreach (var l in m.GoldenRecordsToUpsert.Select(g => $"golden {Fields(g.Fields)}").OrderBy(x => x, StringComparer.Ordinal)) sb.AppendLine(l);
        foreach (var l in m.VersionsToInsert.Select(v => $"version n={v.VersionNumber} {Fields(v.Fields)}").OrderBy(x => x, StringComparer.Ordinal)) sb.AppendLine(l);
        foreach (var l in m.ReviewTasksToInsert.Select(t => $"review new={t.NewEntityRecordId} cand={t.CandidateEntityRecordId} {t.Score:F10} {t.Reason}").OrderBy(x => x, StringComparer.Ordinal)) sb.AppendLine(l);
        foreach (var l in m.MergeEventsToInsert.Select(ev => $"merge absorbed=[{string.Join(",", ev.AbsorbedMemberEntityRecordIds.OrderBy(g => g))}] {ev.Score:F10}").OrderBy(x => x, StringComparer.Ordinal)) sb.AppendLine(l);
        return sb.ToString();
    }

    [Fact]
    public void Parallel_And_Sequential_Produce_Identical_Outcomes()
    {
        var (r1, m1) = Run(dop: 1);
        var (r8, m8) = Run(dop: 8);

        Assert.Equal(r1, r8);                 // IncrementalIngestResult is a record: value equality
        Assert.Equal(Canon(r1, m1), Canon(r8, m8));
        Assert.NotEmpty(m1.EdgesToInsert);    // guard: the batch actually produced auto-match edges
    }
}
