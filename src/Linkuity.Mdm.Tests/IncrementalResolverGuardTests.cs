using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Mdm.Resolution;

namespace Linkuity.Mdm.Tests;

/// <summary>
/// Guards the Milestone 26 fail-fast check: the 'default' similarity strategy scores shared
/// blocking keys, but a Lucene candidate is a scoring projection with EMPTY BlockingKeys, so
/// pairing 'default' similarity with index-backed retrieval must throw instead of silently
/// scoring those matches 0.
/// </summary>
public class IncrementalResolverGuardTests
{
    private static readonly MatchingProfile PersonProfile = DefaultMatchingProfileProvider.CreatePersonProfile();

    private static MatchingProfile WithDefaultSimilarity(MatchingProfile profile)
        => new()
        {
            ContentType = profile.ContentType,
            Fields = profile.Fields,
            NormalizationStrategy = profile.NormalizationStrategy,
            BlockingStrategies = profile.BlockingStrategies,
            CandidateRetrievalStrategy = profile.CandidateRetrievalStrategy,
            SimilarityStrategy = "default",
            ScoringStrategy = profile.ScoringStrategy,
            DecisionStrategy = profile.DecisionStrategy,
            ClusteringStrategy = profile.ClusteringStrategy,
            AutoMatchThreshold = profile.AutoMatchThreshold,
            ReviewThreshold = profile.ReviewThreshold
        };

    [Fact]
    public void Resolve_Throws_WhenHasIndex_AndProfileUsesDefaultSimilarity()
    {
        var resolver = new IncrementalResolver(MatchingDefaults.CreateEngine(), hasIndex: true);
        var projectId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var profile = WithDefaultSimilarity(PersonProfile);
        var incoming = new EntityRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SourceId = sourceId,
            IngestBatchId = batchId,
            SourceRecordId = "r1",
            Fields = new Dictionary<string, string> { ["email"] = "a@example.com", ["name"] = "A" },
            BlockingKeys = [],
            CreatedAt = now
        };
        var request = new IncrementalIngestRequest(projectId, sourceId, batchId, [incoming], AutoMatchThreshold: 0.90, ReviewThreshold: 0.75);
        var project = new Project { Id = projectId, Name = "MDM", ContentType = "person", CreatedAt = now };
        var context = new InMemoryResolutionContext();

        Assert.Throws<InvalidOperationException>(
            () => resolver.Resolve(request, project, profile, [incoming], context, now));
    }
}
