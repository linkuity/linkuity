using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline.Tests;

public class NonComparableAfterOutcomesTests
{
    // CorpusAuditFixtures.Profile() has one field that is both Blocking and Matchable, so a blank
    // value there makes a pair unreachable (no blocking key) rather than reachable-but-uncompared.
    // This profile separates the two roles: "blocking_key" drives candidate retrieval and is
    // populated on both records, while "organization_name" is the only Matchable field and is
    // blank on both — so the pair IS a candidate, but the similarity strategy has nothing to
    // compare it on.
    private static MatchingProfile Profile() => new()
    {
        ContentType = "organization",
        Fields =
        [
            new ProfileField
            {
                Name = "blocking_key",
                SemanticType = SemanticFieldType.OrganizationName,
                Roles = FieldRole.Blocking,
                Weight = 1.0
            },
            new ProfileField
            {
                Name = "organization_name",
                SemanticType = SemanticFieldType.OrganizationName,
                Roles = FieldRole.Matchable,
                SimilarityEvaluator = "canonical-jaccard",
                Weight = 1.0
            }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["fingerprint"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.9,
        ReviewThreshold = 0.75
    };

    private static EntityRecord Record(string id, string blockingKey) => new()
    {
        Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
        SourceRecordId = id,
        Fields = new Dictionary<string, string> { ["blocking_key"] = blockingKey, ["organization_name"] = "" },
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public void APairSharingNoPopulatedField_IsStillNonComparable()
    {
        // Emitting a signal per matchable field made "any signal" permanently true. If
        // comparability is inferred from signal COUNT, this pair silently becomes NoMatch, the
        // non-comparable stratum empties, and the frozen baseline fails.
        var records = new List<EntityRecord>
        {
            Record("a", "ACME HOLDINGS CORP"),
            Record("b", "ACME HOLDINGS CORP")
        };
        var truth = new Dictionary<string, string> { ["a"] = "A", ["b"] = "A" };

        var result = new CorpusAuditService(MatchingDefaults.CreateRegistry())
            .Audit(records, Profile(), truth);

        var pair = Assert.Single(result.AllTruePairs);
        Assert.True(pair.Reachable);
        Assert.Equal(CorpusBand.NonComparable, pair.Band);
        Assert.DoesNotContain(result.AllTruePairs, p => p.Band == CorpusBand.NoMatch && p.Score == 0);
    }
}
