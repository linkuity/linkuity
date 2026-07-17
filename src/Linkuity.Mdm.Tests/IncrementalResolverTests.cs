using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Mdm.Resolution;

namespace Linkuity.Mdm.Tests;

/// <summary>
/// Resolver-level mirror of the FileMetadataStore conformance scenarios. The engine is built
/// with no index (<c>hasIndex: false</c>) so the existing corpus comes from
/// <see cref="InMemoryResolutionContext.GetLinearCorpus"/> and retrieval runs blocking-linear.
/// </summary>
public class IncrementalResolverTests
{
    private static readonly MatchingProfile PersonProfile =
        new DefaultMatchingProfileProvider(DefaultMatchingProfileProvider.BuiltInProfiles()).GetProfile("person");

    private static IncrementalResolver NewResolver()
        => new(MatchingDefaults.CreateEngine(), hasIndex: false);

    private static EntityRecord Record(
        Guid projectId, Guid sourceId, Guid batchId, string srid,
        Dictionary<string, string> fields, DateTimeOffset at)
        => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SourceId = sourceId,
            IngestBatchId = batchId,
            SourceRecordId = srid,
            Fields = fields,
            BlockingKeys = [],
            CreatedAt = at
        };

    private static EntityRecord Keyed(IncrementalResolver resolver, EntityRecord r)
        => new()
        {
            Id = r.Id,
            ProjectId = r.ProjectId,
            SourceId = r.SourceId,
            IngestBatchId = r.IngestBatchId,
            SourceRecordId = r.SourceRecordId,
            Fields = r.Fields,
            BlockingKeys = resolver.GenerateBlockingKeys(r, PersonProfile),
            CreatedAt = r.CreatedAt
        };

    private static Project PersonProject(Guid id, DateTimeOffset now)
        => new() { Id = id, Name = "MDM", ContentType = "person", CreatedAt = now };

    // ── Scenario 1: shared-email incoming vs one existing record (its own cluster) ──────────
    [Fact]
    public void SharedEmail_AutoMatches_JoinsExistingClusterAndRecordsAutoEdge()
    {
        var resolver = NewResolver();
        var projectId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var seedBatch = Guid.NewGuid();
        var incBatch = Guid.NewGuid();

        var existing = Keyed(resolver, Record(projectId, sourceId, seedBatch, "crm-001",
            new() { ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Alice" }, now));
        var existingCluster = new Cluster
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            MemberEntityRecordIds = [existing.Id],
            CreatedAt = now
        };

        var context = new InMemoryResolutionContext();
        context.Records.Add(existing);
        context.Clusters.Add(existingCluster);

        var incoming = Keyed(resolver, Record(projectId, sourceId, incBatch, "web-001",
            new() { ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Alice Verified" }, now.AddMinutes(1)));
        var request = new IncrementalIngestRequest(projectId, sourceId, incBatch, [incoming], 0.90, 0.75);

        var (result, mutations) = resolver.Resolve(
            request, PersonProject(projectId, now), PersonProfile, [incoming], context, now.AddMinutes(1));

        Assert.Equal(1, result.RecordsAdded);
        Assert.Equal(1, result.AutoMatches);
        Assert.Equal(0, result.ReviewTasks);
        Assert.Equal(0, result.SingletonClusters);

        var cluster = Assert.Single(mutations.ClustersToUpsert);
        Assert.Equal(existingCluster.Id, cluster.Id);
        Assert.Equal(2, cluster.MemberEntityRecordIds.Count);
        Assert.Contains(existing.Id, cluster.MemberEntityRecordIds);
        Assert.Contains(incoming.Id, cluster.MemberEntityRecordIds);

        var edge = Assert.Single(mutations.EdgesToInsert);
        Assert.Equal("auto", edge.Decision);
        Assert.NotEmpty(edge.Breakdown);

        Assert.Equal(incoming, Assert.Single(mutations.RecordsToInsert));
    }

    // ── Scenario 2: two brand-new in-batch records sharing an email, no existing ────────────
    [Fact]
    public void TwoNetNewDuplicates_FormSingleNewCluster()
    {
        var resolver = NewResolver();
        var projectId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var batch = Guid.NewGuid();

        var a = Keyed(resolver, Record(projectId, sourceId, batch, "in-a",
            new() { ["source"] = "CRM", ["email"] = "maria@x.com", ["name"] = "Maria Garcia" }, now));
        var b = Keyed(resolver, Record(projectId, sourceId, batch, "in-b",
            new() { ["source"] = "CRM", ["email"] = "maria@x.com", ["name"] = "Maria Garcia" }, now));

        var context = new InMemoryResolutionContext();
        var request = new IncrementalIngestRequest(projectId, sourceId, batch, [a, b], 0.90, 0.75);

        var (result, mutations) = resolver.Resolve(
            request, PersonProject(projectId, now), PersonProfile, [a, b], context, now);

        var cluster = Assert.Single(mutations.ClustersToUpsert);
        Assert.Equal(2, cluster.MemberEntityRecordIds.Count);
        Assert.Contains(a.Id, cluster.MemberEntityRecordIds);
        Assert.Contains(b.Id, cluster.MemberEntityRecordIds);
        Assert.Equal(1, result.AutoMatches);
        Assert.Equal(0, result.ReviewTasks);
        Assert.Equal(2, mutations.RecordsToInsert.Count);
    }

    // ── Scenario 3: bridge — incoming auto-joins cluster A and auto-matches cluster B ───────
    [Fact]
    public void Bridge_MergesClusters_TombstonesLoser_AndClearsLoserGolden()
    {
        var resolver = NewResolver();
        var projectId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var seedBatch = Guid.NewGuid();
        var incBatch = Guid.NewGuid();

        // Cluster A (older, survivor): email anchor. Cluster B (newer, loser): phone anchor.
        var r1 = Keyed(resolver, Record(projectId, sourceId, seedBatch, "ex-1",
            new() { ["source"] = "CRM", ["email"] = "jon@acme.com", ["name"] = "Jonathan Smith" }, now));
        var r2 = Keyed(resolver, Record(projectId, sourceId, seedBatch, "ex-2",
            new() { ["source"] = "CRM", ["phone"] = "555-9876", ["name"] = "J Smith" }, now));
        var clusterA = new Cluster { Id = Guid.NewGuid(), ProjectId = projectId, MemberEntityRecordIds = [r1.Id], CreatedAt = now };
        var clusterB = new Cluster { Id = Guid.NewGuid(), ProjectId = projectId, MemberEntityRecordIds = [r2.Id], CreatedAt = now.AddSeconds(1) };

        // Seed a golden for the loser cluster B so its removal surfaces in GoldenRecordClusterIdsToClear.
        var goldenA = Golden(projectId, clusterA.Id, new() { ["name"] = "Jonathan Smith" }, now);
        var goldenB = Golden(projectId, clusterB.Id, new() { ["name"] = "J Smith" }, now);

        var context = new InMemoryResolutionContext();
        context.Records.AddRange([r1, r2]);
        context.Clusters.AddRange([clusterA, clusterB]);
        context.GoldenRecords.AddRange([goldenA.Golden, goldenB.Golden]);
        context.GoldenRecordVersions.AddRange([goldenA.Version, goldenB.Version]);

        var x = Keyed(resolver, Record(projectId, sourceId, incBatch, "in-x",
            new() { ["source"] = "CRM", ["email"] = "jon@acme.com", ["phone"] = "555-9876", ["name"] = "Jonathan Smith" },
            now.AddMinutes(1)));
        var request = new IncrementalIngestRequest(projectId, sourceId, incBatch, [x], 0.90, 0.75);

        var (result, mutations) = resolver.Resolve(
            request, PersonProject(projectId, now), PersonProfile, [x], context, now.AddMinutes(1));

        Assert.Equal(0, result.ReviewTasks);

        var mergeEvent = Assert.Single(mutations.MergeEventsToInsert);
        Assert.Equal(clusterA.Id, mergeEvent.SurvivorClusterId);
        Assert.Equal(clusterB.Id, mergeEvent.AbsorbedClusterId);
        Assert.Contains(x.Id, mergeEvent.TriggerRecordIds);

        var tombstone = Assert.Single(mutations.ClustersToUpsert, c => c.Status == "merged");
        Assert.Equal(clusterB.Id, tombstone.Id);
        Assert.Equal(clusterA.Id, tombstone.MergedIntoClusterId);

        var survivor = Assert.Single(mutations.ClustersToUpsert, c => c.Status != "merged");
        Assert.Equal(clusterA.Id, survivor.Id);
        Assert.Equal(3, survivor.MemberEntityRecordIds.Count);

        Assert.Equal(clusterB.Id, Assert.Single(mutations.GoldenRecordClusterIdsToClear));
    }

    // ── Scenario 4: review-band pair (matching last name + a real, non-identifier first-name
    // similarity signal, no email on either side) — weighted similarity legitimately clears the
    // review-floor gate (last_name exact 2.0 + first_name "Robert"/"Bob" fuzzy 0.67 over total
    // weight 3.0 = 0.89), so this is a genuinely uncertain pair, not a blocking-key-only match. ──
    [Fact]
    public void ReviewBand_CreatesOpenReviewTask_WithBreakdown()
    {
        var resolver = NewResolver();
        var projectId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var seedBatch = Guid.NewGuid();
        var incBatch = Guid.NewGuid();

        var existing = Keyed(resolver, Record(projectId, sourceId, seedBatch, "existing-1",
            new() { ["source"] = "CRM", ["first_name"] = "Robert", ["last_name"] = "Smith" }, now));
        var existingCluster = new Cluster
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            MemberEntityRecordIds = [existing.Id],
            CreatedAt = now
        };

        var context = new InMemoryResolutionContext();
        context.Records.Add(existing);
        context.Clusters.Add(existingCluster);

        var incoming = Keyed(resolver, Record(projectId, sourceId, incBatch, "in-1",
            new() { ["source"] = "CRM", ["first_name"] = "Bob", ["last_name"] = "Smith" },
            now.AddMinutes(1)));
        var request = new IncrementalIngestRequest(projectId, sourceId, incBatch, [incoming], 0.90, 0.75);

        var (result, mutations) = resolver.Resolve(
            request, PersonProject(projectId, now), PersonProfile, [incoming], context, now.AddMinutes(1));

        Assert.True(result.ReviewTasks >= 1);
        var task = Assert.Single(mutations.ReviewTasksToInsert);
        Assert.Equal("open", task.Status);
        Assert.NotEmpty(task.Breakdown);
        Assert.Equal(incoming.Id, task.NewEntityRecordId);
        Assert.Equal(existing.Id, task.CandidateEntityRecordId);
    }

    // ── Scenario 5 (M27.1 regression): a lowered ReviewFloorGate on the CALLER's profile must
    // actually reach the scorer through IncrementalResolver.WithCallOverrides. shared last_name
    // (fuzzy "Smith"/"Smith" = 1.0, weight 2) + conflicting postal_code (exact "10001"/"90210" =
    // 0.0, weight 1) -> weighted = (2*1.0 + 1*0.0)/3 = 0.6667, deterministically inside
    // [0.30, 0.75): below the default 0.75 gate (raw weighted stands -> NoMatch, no review task —
    // this is the RED behavior when WithCallOverrides strips ReviewFloorGate back to the 0.75
    // default) but above a profile-configured 0.30 gate (floored to the 0.80 review floor ->
    // Review band -> one review task — the GREEN behavior once the gate is propagated). No
    // identifier field (email/phone/dob/domain_name) is set on either side, so the identifier
    // floor never applies.
    [Fact]
    public void LoweredReviewFloorGate_PropagatesThroughCallOverrides_AndPromotesSubGatePairToReview()
    {
        var resolver = NewResolver();
        var projectId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var seedBatch = Guid.NewGuid();
        var incBatch = Guid.NewGuid();

        var lowGateProfile = new MatchingProfile
        {
            ContentType = PersonProfile.ContentType,
            Fields = PersonProfile.Fields,
            NormalizationStrategy = PersonProfile.NormalizationStrategy,
            BlockingStrategies = PersonProfile.BlockingStrategies,
            CandidateRetrievalStrategy = PersonProfile.CandidateRetrievalStrategy,
            SimilarityStrategy = PersonProfile.SimilarityStrategy,
            ScoringStrategy = PersonProfile.ScoringStrategy,
            DecisionStrategy = PersonProfile.DecisionStrategy,
            ClusteringStrategy = PersonProfile.ClusteringStrategy,
            AutoMatchThreshold = PersonProfile.AutoMatchThreshold,
            ReviewThreshold = PersonProfile.ReviewThreshold,
            ReviewFloorGate = 0.30
        };

        var existing = Keyed(resolver, Record(projectId, sourceId, seedBatch, "existing-1",
            new() { ["source"] = "CRM", ["last_name"] = "Smith", ["postal_code"] = "10001" }, now));
        var existingCluster = new Cluster
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            MemberEntityRecordIds = [existing.Id],
            CreatedAt = now
        };

        var context = new InMemoryResolutionContext();
        context.Records.Add(existing);
        context.Clusters.Add(existingCluster);

        var incoming = Keyed(resolver, Record(projectId, sourceId, incBatch, "in-1",
            new() { ["source"] = "CRM", ["last_name"] = "Smith", ["postal_code"] = "90210" },
            now.AddMinutes(1)));
        var request = new IncrementalIngestRequest(projectId, sourceId, incBatch, [incoming], 0.90, 0.75);

        var (result, mutations) = resolver.Resolve(
            request, PersonProject(projectId, now), lowGateProfile, [incoming], context, now.AddMinutes(1));

        Assert.True(result.ReviewTasks >= 1,
            "expected the lowered ReviewFloorGate (0.30) to promote the 0.6667-weighted pair into the review band");
        var task = Assert.Single(mutations.ReviewTasksToInsert);
        Assert.Equal("open", task.Status);
        Assert.Equal(0.80, task.Score);
        Assert.Equal(incoming.Id, task.NewEntityRecordId);
        Assert.Equal(existing.Id, task.CandidateEntityRecordId);
    }

    private static (GoldenRecord Golden, GoldenRecordVersion Version) Golden(
        Guid projectId, Guid clusterId, Dictionary<string, string> fields, DateTimeOffset now)
    {
        var goldenId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        return (
            new GoldenRecord
            {
                Id = goldenId,
                ProjectId = projectId,
                ClusterId = clusterId,
                CurrentVersionId = versionId,
                Fields = fields,
                UpdatedAt = now
            },
            new GoldenRecordVersion
            {
                Id = versionId,
                GoldenRecordId = goldenId,
                ProjectId = projectId,
                ClusterId = clusterId,
                IngestBatchId = Guid.NewGuid(),
                VersionNumber = 1,
                Fields = fields,
                CreatedAt = now
            });
    }
}
