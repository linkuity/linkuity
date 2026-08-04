using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline.Tests;

/// <summary>
/// Shared test fixtures. MatchingProfile is a sealed CLASS with init-only properties, not a
/// record, so `with` expressions do not compile — Clone is the substitute.
/// </summary>
internal static class CorpusAuditFixtures
{
    internal static MatchingProfile Profile() => new()
    {
        ContentType = "organization",
        Fields =
        [
            new ProfileField
            {
                Name = "organization_name",
                SemanticType = SemanticFieldType.OrganizationName,
                Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking,
                SimilarityEvaluator = "canonical-jaccard",
                Weight = 4.0
            }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["fingerprint", "token", "acronym"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.41,
        ReviewThreshold = 0.31,
        MaxBlockSize = 50
    };

    internal static MatchingProfile Clone(
        MatchingProfile source,
        string? normalizationStrategy = null,
        string? similarityStrategy = null,
        string? scoringStrategy = null,
        string? decisionStrategy = null,
        string? clusteringStrategy = null,
        IReadOnlyList<string>? blockingStrategies = null,
        double? autoMatchThreshold = null,
        double? reviewThreshold = null,
        double? minClusterCohesion = null) => new()
    {
        ContentType = source.ContentType,
        Fields = source.Fields,
        NormalizationStrategy = normalizationStrategy ?? source.NormalizationStrategy,
        BlockingStrategies = blockingStrategies ?? source.BlockingStrategies,
        CandidateRetrievalStrategy = source.CandidateRetrievalStrategy,
        SimilarityStrategy = similarityStrategy ?? source.SimilarityStrategy,
        ScoringStrategy = scoringStrategy ?? source.ScoringStrategy,
        DecisionStrategy = decisionStrategy ?? source.DecisionStrategy,
        ClusteringStrategy = clusteringStrategy ?? source.ClusteringStrategy,
        AutoMatchThreshold = autoMatchThreshold ?? source.AutoMatchThreshold,
        ReviewThreshold = reviewThreshold ?? source.ReviewThreshold,
        ReviewFloorGate = source.ReviewFloorGate,
        MaxBlockSize = source.MaxBlockSize,
        MinClusterCohesion = minClusterCohesion ?? source.MinClusterCohesion
    };

    /// <summary>
    /// A profile that actually runs on the evidence scorer: unlike Profile(), the matchable field
    /// carries FieldEvidence (the scorer throws without it), and the thresholds are expressed in
    /// bits — unbounded log-odds, not [0,1] — which is exactly the shape that exposed the
    /// ThresholdsOn() defaulting bug in Audit()/BandOf().
    /// </summary>
    internal static MatchingProfile EvidenceProfile() => new()
    {
        ContentType = "organization",
        Fields =
        [
            new ProfileField
            {
                Name = "organization_name",
                SemanticType = SemanticFieldType.OrganizationName,
                Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking,
                SimilarityEvaluator = "canonical-jaccard",
                Weight = 4.0,
                Evidence = new FieldEvidence { SameEntityAgreement = 0.9, ChanceAgreement = 0.1, MaxAgreementBits = 6.0 }
            }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["fingerprint", "token", "acronym"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "evidence",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 8.0,
        ReviewThreshold = 4.0,
        MaxBlockSize = 50
    };

    internal static EntityRecord Org(string id, string name) => new()
    {
        Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
        SourceRecordId = id,
        Fields = new Dictionary<string, string> { ["organization_name"] = name },
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    /// <summary>
    /// Builds a KeyIndex directly from an explicit record -> key-name map. Suppression and
    /// ownership tests use this instead of real names, so they test the ALGORITHM rather than
    /// which key a strategy happens to emit. Real-strategy fidelity is Task 3's job.
    /// </summary>
    internal static CorpusAuditService.KeyIndex SyntheticIndex(params string[][] keysPerRecord)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        var members = new List<List<int>>();
        var recordKeys = new int[keysPerRecord.Length][];

        for (var i = 0; i < keysPerRecord.Length; i++)
        {
            var set = new SortedSet<int>();
            foreach (var key in keysPerRecord[i])
            {
                if (!ids.TryGetValue(key, out var id))
                {
                    id = names.Count;
                    ids[key] = id;
                    names.Add(key);
                    members.Add([]);
                }
                set.Add(id);
            }
            recordKeys[i] = [.. set];
            foreach (var id in set) members[id].Add(i);
        }

        var keyMembers = new int[members.Count][];
        var keyCount = new int[members.Count];
        for (var k = 0; k < members.Count; k++)
        {
            keyMembers[k] = [.. members[k]];
            keyCount[k] = members[k].Count;
        }
        return new CorpusAuditService.KeyIndex(recordKeys, keyCount, keyMembers, [.. names]);
    }
}
