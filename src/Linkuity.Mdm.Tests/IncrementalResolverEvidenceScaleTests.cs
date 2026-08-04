using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Clustering;
using Linkuity.Matching.Profiles;
using Linkuity.Mdm.Resolution;

namespace Linkuity.Mdm.Tests;

/// <summary>
/// Pins the two IncrementalResolver sites that build their own <see cref="MatchThresholds"/>
/// instead of going through <see cref="Linkuity.Matching.Strategies.IDecisionStrategy.Decide"/>:
/// <see cref="IncrementalResolver.ThresholdsFor"/> (the fail-fast check both metadata stores run)
/// and the threshold construction inside <c>BuildResolutionEdges</c> (private; reached only
/// through <see cref="IncrementalResolver.Resolve"/>). Neither is reachable through
/// <c>IDecisionStrategy.Decide</c> — both classify with <see cref="MatchBandClassifier"/>
/// directly — so fixing <c>Decide</c>'s signature alone does not fix either of them. Before the
/// fix, both built <see cref="MatchThresholds"/> on the default <see cref="ScoreScale.UnitInterval"/>,
/// which throws <see cref="ArgumentOutOfRangeException"/> for an evidence-scored profile's
/// log-odds thresholds.
/// </summary>
public class IncrementalResolverEvidenceScaleTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid SourceId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static MatchingProfile EvidenceProfile() => new()
    {
        ContentType = "organization",
        Fields =
        [
            new ProfileField
            {
                Name = "organization_name",
                SemanticType = SemanticFieldType.OrganizationName,
                Roles = FieldRole.Matchable,
                SimilarityEvaluator = "exact",
                Evidence = new FieldEvidence { SameEntityAgreement = 0.9, ChanceAgreement = 0.01, MaxAgreementBits = 6.0 }
            }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["token"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "evidence",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 5.0,
        ReviewThreshold = 2.0
    };

    private static EntityRecord Record(string sourceRecordId, string organizationName, Guid batchId) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = ProjectId,
        SourceId = SourceId,
        IngestBatchId = batchId,
        SourceRecordId = sourceRecordId,
        Fields = new Dictionary<string, string> { ["organization_name"] = organizationName },
        // Set explicitly rather than derived: candidateRetrievalStrategy is "linear" (retrieval
        // ignores blocking keys entirely), so this only needs to be non-empty for the profile's
        // BlockingStrategies to be a plausible config, not for retrieval to work.
        BlockingKeys = ["tok:shared"],
        CreatedAt = Now
    };

    // ── Site: IncrementalResolver.ThresholdsFor (the fail-fast check PostgresMetadataStore and
    // FileMetadataStore both run once they know the profile) ───────────────────────────────────

    [Fact]
    public void ThresholdsFor_WithTheResolvedLogOddsScale_AcceptsThresholdsOutsideUnitInterval()
    {
        var request = new IncrementalIngestRequest(
            ProjectId, SourceId, Guid.NewGuid(), [], AutoMatchThreshold: 6.0, ReviewThreshold: 3.0);

        var thresholds = IncrementalResolver.ThresholdsFor(request, ScoreScale.LogOdds);

        Assert.Equal(6.0, thresholds.AutoMatch);
        Assert.Equal(3.0, thresholds.Review);
        Assert.Equal(ScoreScale.LogOdds, thresholds.Scale);
    }

    [Fact]
    public void ThresholdsFor_DefaultScale_StillRejectsLogOddsShapedThresholds()
    {
        // Documents WHY both metadata stores must resolve the profile's scale and pass it
        // explicitly (see PostgresMetadataStore/FileMetadataStore.SaveIncrementalIngestAsync):
        // the default parameter preserves the pre-fix behaviour for a caller that has not
        // resolved a scale, rather than silently reinterpreting an evidence profile's thresholds.
        var request = new IncrementalIngestRequest(
            ProjectId, SourceId, Guid.NewGuid(), [], AutoMatchThreshold: 6.0, ReviewThreshold: 3.0);

        // ThresholdsFor wraps MatchThresholds' ArgumentOutOfRangeException as a plain
        // ArgumentException naming the request parameter — see its own catch block.
        Assert.Throws<ArgumentException>(() => IncrementalResolver.ThresholdsFor(request));
    }

    // ── Site: BuildResolutionEdges (private; reached only through Resolve) ──────────────────────

    [Fact]
    public void Resolve_WithEvidenceScoredProfile_AutoMatchesOnLogOddsThresholds_InsteadOfThrowing()
    {
        var profile = EvidenceProfile();
        var resolver = new IncrementalResolver(MatchingDefaults.CreateEngine(), hasIndex: false, new CohesionClusterMergePolicy());

        var seedBatch = Guid.NewGuid();
        var incBatch = Guid.NewGuid();
        var existing = Record("existing-1", "Acme Corp", seedBatch);
        var existingCluster = new Cluster
        {
            Id = Guid.NewGuid(),
            ProjectId = ProjectId,
            MemberEntityRecordIds = [existing.Id],
            CreatedAt = Now
        };

        var context = new InMemoryResolutionContext();
        context.Records.Add(existing);
        context.Clusters.Add(existingCluster);

        var incoming = Record("in-1", "Acme Corp", incBatch);
        var request = new IncrementalIngestRequest(
            ProjectId, SourceId, incBatch, [incoming],
            AutoMatchThreshold: profile.AutoMatchThreshold, ReviewThreshold: profile.ReviewThreshold);
        var project = new Project { Id = ProjectId, Name = "MDM", ContentType = "organization", CreatedAt = Now };

        // Identical organization_name -> exact similarity 1.0 -> full AgreementBits (capped at
        // 6.0), clearing the 5.0 auto threshold. Before the fix this throws
        // ArgumentOutOfRangeException instead of returning a result.
        var (result, mutations) = resolver.Resolve(request, project, profile, [incoming], context, Now);

        Assert.Equal(1, result.AutoMatches);
        var edge = Assert.Single(mutations.EdgesToInsert);
        Assert.Equal("auto", edge.Decision);
        Assert.Equal(6.0, edge.Score, 6);
    }
}
