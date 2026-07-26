using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline.Tests;

public class ScoringAuditServiceTests
{
    // Showcase-shaped org profile: jaccard name (4.0) + jaccard address (2.5) + exact postal (0.5),
    // thresholds 0.41/0.31 — the config the instrument exists to measure.
    private static MatchingProfile OrgProfile(int? maxBlockSize = null) => new()
    {
        ContentType = "organization",
        Fields =
        [
            new ProfileField { Name = "organization_name", SemanticType = SemanticFieldType.OrganizationName,
                Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking,
                SimilarityEvaluator = "jaccard", Weight = 4.0 },
            new ProfileField { Name = "address_line", SemanticType = SemanticFieldType.AddressLine,
                Roles = FieldRole.Searchable | FieldRole.Matchable,
                SimilarityEvaluator = "jaccard", Weight = 2.5 },
            new ProfileField { Name = "postal_code", SemanticType = SemanticFieldType.PostalCode,
                Roles = FieldRole.Matchable, SimilarityEvaluator = "exact", Weight = 0.5 }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["exact-value", "token"],
        CandidateRetrievalStrategy = "blocking-linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.41,
        ReviewThreshold = 0.31,
        MaxBlockSize = maxBlockSize
    };

    private static ScoringAuditService NewService() => new(MatchingDefaults.CreateRegistry());

    private static EntityRecord Rec(string id, string name, string address = "", string postal = "") => new()
    {
        Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
        SourceRecordId = id,
        Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["organization_name"] = name, ["address_line"] = address, ["postal_code"] = postal
        },
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public void CandidatePairs_AreSharedKeyPairs_ScoredAndBanded()
    {
        // "ACME ROCKETS" / "ACME ROCKETS" identical -> jaccard 1.0 on name, auto band.
        // "ACME PLUMBING" shares token ACME -> compared, low score -> no-match band.
        var records = new[]
        {
            Rec("a1", "ACME ROCKETS", "1 MAIN ST", "11111"),
            Rec("a2", "ACME ROCKETS", "1 MAIN ST", "11111"),
            Rec("a3", "ACME PLUMBING", "9 OTHER RD", "99999")
        };
        var result = NewService().Audit(records, OrgProfile());

        Assert.Equal(3, result.Pairs.Count); // a1-a2, a1-a3, a2-a3 (all share a token key)
        var top = result.Pairs.Single(p => p.LeftSourceRecordId == "a1" && p.RightSourceRecordId == "a2");
        Assert.True(top.Reachable);
        Assert.True(top.Comparable);
        Assert.Equal(ScoreBand.Auto, top.EngineBand);
        Assert.True(top.Score >= 0.41);
    }

    [Fact]
    public void Bands_CountAutoReviewNoMatchNonComparable()
    {
        var records = new[]
        {
            Rec("a1", "ACME ROCKETS", "1 MAIN ST", "11111"),
            Rec("a2", "ACME ROCKETS", "1 MAIN ST", "11111"),   // auto vs a1
            Rec("a3", "ACME PLUMBING", "9 OTHER RD", "99999")  // no-match vs both
        };
        var result = NewService().Audit(records, OrgProfile());
        Assert.Equal(1, result.Bands.Auto);
        Assert.Equal(0, result.Bands.Review);
        Assert.Equal(2, result.Bands.NoMatch);
        Assert.Equal(0, result.Bands.NonComparable);
    }

    [Fact]
    public void NonComparablePair_IsClassifiedSeparately_NotAsNoMatch()
    {
        // Same blocking key via identical name field used ONLY for blocking:
        // strip Matchable from every field except one that is blank on both sides.
        // Simpler: two records sharing a name token but with a profile whose only
        // matchable fields (address, postal) are blank on both -> zero signals.
        var blockOnly = new MatchingProfile
        {
            ContentType = "organization",
            Fields =
            [
                new ProfileField { Name = "organization_name", SemanticType = SemanticFieldType.OrganizationName,
                    Roles = FieldRole.Searchable | FieldRole.Blocking, Weight = 4.0 },       // NOT Matchable
                new ProfileField { Name = "address_line", SemanticType = SemanticFieldType.AddressLine,
                    Roles = FieldRole.Matchable, SimilarityEvaluator = "jaccard", Weight = 2.5 }
            ],
            NormalizationStrategy = "identity",
            BlockingStrategies = ["exact-value", "token"],
            CandidateRetrievalStrategy = "blocking-linear",
            SimilarityStrategy = "field-weighted",
            ScoringStrategy = "identifier-weighted",
            DecisionStrategy = "threshold",
            ClusteringStrategy = "union-find",
            AutoMatchThreshold = 0.41,
            ReviewThreshold = 0.31
        };
        var records = new[] { Rec("b1", "ACME ROCKETS"), Rec("b2", "ACME ROCKETS") }; // addresses blank
        var result = NewService().Audit(records, blockOnly);
        var pair = Assert.Single(result.Pairs);
        Assert.False(pair.Comparable);
        Assert.Equal(ScoreBand.NonComparable, pair.EngineBand);
        Assert.Equal(1, result.Bands.NonComparable);
    }

    [Fact]
    public void EngineParitySuppression_BlockOfMaxPlusOne_StaysActive()
    {
        // maxBlockSize=2, block of exactly 3 members: blocking-linear sees corpus
        // frequency 2 (== max) per query -> NOT suppressed. Whole-block rule (3 > 2)
        // would wrongly suppress. Audit must use engine parity.
        var records = new[]
        {
            Rec("c1", "ZENITH WIDGETS", "1 A ST", "11111"),
            Rec("c2", "ZENITH WIDGETS", "1 A ST", "11111"),
            Rec("c3", "ZENITH WIDGETS", "1 A ST", "11111")
        };
        var result = NewService().Audit(records, OrgProfile(maxBlockSize: 2));
        Assert.Equal(3, result.Pairs.Count);
        Assert.All(result.Pairs, p => Assert.Equal(ScoreBand.Auto, p.EngineBand));
    }

    [Fact]
    public void EngineParitySuppression_MatchesBatchMatchingService_AtBoundary()
    {
        // The pinned parity test from the spec: same rows through the audit and the
        // real batch path must yield the same auto-band pair set at max+1 (active)
        // and max+2 (suppressed).
        var profile = OrgProfile(maxBlockSize: 2);
        foreach (var (count, expectedPairs) in new[] { (3, 3), (4, 0) })
        {
            var records = Enumerable.Range(1, count)
                .Select(i => Rec($"d{i}", "ORBIT DYNAMICS", "5 LOOP RD", "22222")).ToList();
            var rows = records
                .Select(r => (r.SourceRecordId, (IReadOnlyDictionary<string, string>)r.Fields))
                .ToList();

            var batchCsv = BatchMatchingService.BuildMatchesCsv(rows, profile);
            var batchPairs = batchCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1) // header
                .Select(line => (line.Split(',')[0], line.Split(',')[1].TrimEnd('\r')))
                .ToHashSet();

            var audit = NewService().Audit(records, profile);
            var auditAutoPairs = audit.Pairs
                .Where(p => p.EngineBand == ScoreBand.Auto)
                .Select(p => (p.LeftSourceRecordId, p.RightSourceRecordId))
                .ToHashSet();

            Assert.Equal(expectedPairs, batchPairs.Count);
            Assert.Equal(batchPairs, auditAutoPairs);
        }
    }

    [Fact]
    public void DuplicateSourceRecordId_FailsFast()
    {
        var records = new[] { Rec("dup", "ACME"), Rec("dup", "ACME") };
        var ex = Assert.Throws<ArgumentException>(() => NewService().Audit(records, OrgProfile()));
        Assert.Contains("dup", ex.Message);
    }

    [Fact]
    public void UnsupportedSimilarityStrategy_IsRejectedWithClearError()
    {
        var legacy = new MatchingProfile
        {
            ContentType = "organization",
            Fields = OrgProfile().Fields,
            NormalizationStrategy = "identity",
            BlockingStrategies = ["exact-value", "token-name"],
            CandidateRetrievalStrategy = "blocking-linear",
            SimilarityStrategy = "default",
            ScoringStrategy = "default",
            DecisionStrategy = "threshold",
            ClusteringStrategy = "union-find",
            AutoMatchThreshold = 0.9,
            ReviewThreshold = 0.75
        };
        var ex = Assert.Throws<ArgumentException>(() =>
            NewService().Audit([Rec("x", "ACME")], legacy));
        Assert.Contains("field-weighted", ex.Message);
    }

    [Theory]
    [InlineData(0.41, 0.41)]  // review == auto -> invalid (loader requires strict)
    [InlineData(0.3, 0.5)]    // review > auto after override
    [InlineData(1.5, 0.3)]    // auto > 1
    public void InvalidThresholdOverrides_AreRejected(double auto, double review)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            NewService().Audit([Rec("x", "ACME")], OrgProfile(),
                autoThresholdOverride: auto, reviewThresholdOverride: review));
        Assert.Contains("review", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pairs_AreOrderedByCanonicalPairIdentity()
    {
        var records = new[]
        {
            Rec("z9", "NOVA LABS", "1 X", "1"),
            Rec("a1", "NOVA LABS", "1 X", "1"),
            Rec("m5", "NOVA LABS", "1 X", "1")
        };
        var result = NewService().Audit(records, OrgProfile());
        var ids = result.Pairs.Select(p => (p.LeftSourceRecordId, p.RightSourceRecordId)).ToList();
        var sorted = ids.OrderBy(p => p.Item1, StringComparer.Ordinal)
                        .ThenBy(p => p.Item2, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, ids);
        Assert.All(ids, p => Assert.True(string.CompareOrdinal(p.Item1, p.Item2) < 0));
    }
}
