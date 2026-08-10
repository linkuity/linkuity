using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline.Tests;

public class CorpusAuditCoverageTests
{
    private static CorpusAuditService NewService() => new(MatchingDefaults.CreateRegistry());

    private static readonly EntityRecord[] Records =
    [
        CorpusAuditFixtures.Org("a", "ACME WIDGETS INC"),
        CorpusAuditFixtures.Org("b", "ACME WIDGETS"),
        CorpusAuditFixtures.Org("orphan", "ZETA HOLDINGS")
    ];

    [Fact]
    public void GateMode_RejectsUnlabeledRecords_NamingThem()
    {
        var truth = new Dictionary<string, string> { ["a"] = "acme", ["b"] = "acme" };
        var ex = Assert.Throws<ArgumentException>(
            () => NewService().Audit(Records, CorpusAuditFixtures.Profile(), truth, null, gateMode: true));
        Assert.Contains("orphan", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GateMode_RejectsGroundTruthNamingAbsentRecords_NamingThem()
    {
        var truth = new Dictionary<string, string>
            { ["a"] = "acme", ["b"] = "acme", ["orphan"] = "zeta", ["ghost"] = "nowhere" };
        var ex = Assert.Throws<ArgumentException>(
            () => NewService().Audit(Records, CorpusAuditFixtures.Profile(), truth, null, gateMode: true));
        Assert.Contains("ghost", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GateMode_AcceptsExactIdSetEquality()
    {
        var truth = new Dictionary<string, string> { ["a"] = "acme", ["b"] = "acme", ["orphan"] = "zeta" };
        var result = NewService().Audit(Records, CorpusAuditFixtures.Profile(), truth, null, gateMode: true);
        Assert.Equal(0, result.Counts.UnlabeledRecordCount);
    }

    [Fact]
    public void ExploratoryMode_AllowsPartialLabelsAndReportsEndpointPairs()
    {
        var truth = new Dictionary<string, string> { ["a"] = "acme", ["b"] = "acme" };
        var result = NewService().Audit(Records, CorpusAuditFixtures.Profile(), truth);

        Assert.Equal(1, result.Counts.UnlabeledRecordCount);
        // Derivation: "a"/"b" ("ACME WIDGETS INC"/"ACME WIDGETS") share enough tokens to block and
        // score above AutoMatchThreshold, so they land in one predicted cluster; "orphan" ("ZETA
        // HOLDINGS") shares no tokens with either and blocks into no key with them, so it is an
        // isolated singleton cluster. Per-cluster contribution is labeled*(size-labeled) +
        // Choose2(size-labeled): {a,b} has labeled=2, size=2 -> 2*0 + Choose2(0) = 0; {orphan} has
        // labeled=0, size=1 -> 0*1 + Choose2(1) = 0. Total = 0 regardless of whether a/b actually
        // merge, since with only one unlabeled record (orphan) in a singleton cluster of its own,
        // no cluster ever has both a labeled and an unlabeled member.
        Assert.Equal(0, result.Counts.UnlabeledEndpointPairs);
    }

    // ------------------------------------------------------------------------------------
    // Task 5: field coverage must honour ProfileField.NullEquivalents, not a raw blank check --
    // otherwise a declared sentinel (e.g. GLEIF legal_form "8888") is reported as a populated,
    // shared value here while the matcher correctly treats it as absent.
    // ------------------------------------------------------------------------------------

    private static MatchingProfile ProfileWithSentinelLegalForm() => new()
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
            },
            new ProfileField
            {
                Name = "legal_form",
                SemanticType = SemanticFieldType.LegalForm,
                Roles = FieldRole.Matchable,
                SimilarityEvaluator = "exact",
                Weight = 1.0,
                NullEquivalents = ["8888"]
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

    private static EntityRecord OrgWithLegalForm(string id, string name, string legalForm) => new()
    {
        Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
        SourceRecordId = id,
        Fields = new Dictionary<string, string> { ["organization_name"] = name, ["legal_form"] = legalForm },
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public void DeclaredSentinelOnlySharedValue_FieldCoverageReportsAbsent()
    {
        var records = new[]
        {
            OrgWithLegalForm("a", "ACME WIDGETS INC", "8888"),
            OrgWithLegalForm("b", "ACME WIDGETS", "8888"),
        };
        var truth = new Dictionary<string, string> { ["a"] = "acme", ["b"] = "acme" };

        var result = NewService().Audit(records, ProfileWithSentinelLegalForm(), truth);

        var legalFormCoverage = result.Inputs.FieldCoverage.Single(f => f.FieldName == "legal_form");
        Assert.Equal(0, legalFormCoverage.PairsPopulatedBothSides);
    }

    [Fact]
    public void GenuineSharedValue_StillCountsAsFieldCoverage()
    {
        // Control: a real (non-sentinel) shared value on the same field must still count, so the
        // sentinel fix above is not achieved by breaking coverage counting generally.
        var records = new[]
        {
            OrgWithLegalForm("a", "ACME WIDGETS INC", "LLC"),
            OrgWithLegalForm("b", "ACME WIDGETS", "LLC"),
        };
        var truth = new Dictionary<string, string> { ["a"] = "acme", ["b"] = "acme" };

        var result = NewService().Audit(records, ProfileWithSentinelLegalForm(), truth);

        var legalFormCoverage = result.Inputs.FieldCoverage.Single(f => f.FieldName == "legal_form");
        Assert.Equal(1, legalFormCoverage.PairsPopulatedBothSides);
    }
}
