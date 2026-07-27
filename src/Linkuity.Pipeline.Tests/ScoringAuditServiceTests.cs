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

    [Fact]
    public void NonIdentityNormalization_IsRejectedWithClearError()
    {
        var profile = new MatchingProfile
        {
            ContentType = "organization",
            Fields = OrgProfile().Fields,
            NormalizationStrategy = "semantic-field",
            BlockingStrategies = ["exact-value", "token"],
            CandidateRetrievalStrategy = "blocking-linear",
            SimilarityStrategy = "field-weighted",
            ScoringStrategy = "identifier-weighted",
            DecisionStrategy = "threshold",
            ClusteringStrategy = "union-find",
            AutoMatchThreshold = 0.41,
            ReviewThreshold = 0.31
        };
        var ex = Assert.Throws<ArgumentException>(() =>
            NewService().Audit([Rec("x", "ACME")], profile));
        Assert.Contains("identity", ex.Message);
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

    // Ground truth: g1+g2 same company (reachable, auto), g3+g4 same company but
    // UNREACHABLE (no shared token), g5 unlabeled, g6 labeled false vs g1/g2.
    private static (EntityRecord[] Records, Dictionary<string, string> Truth) GroundTruthFixture()
    {
        var records = new[]
        {
            Rec("g1", "APEX ENERGY", "1 MAIN ST", "11111"),
            Rec("g2", "APEX ENERGY", "1 MAIN ST", "11111"),
            Rec("g3", "HELIOS POWER", "2 SUN RD", "22222"),
            Rec("g4", "SOLARIS GRID", "9 FAR AVE", "33333"),   // true pair with g3, no shared token
            Rec("g5", "APEX PLUMBING", "5 PIPE LN", "44444"),  // shares APEX token; unlabeled
            Rec("g6", "APEX MINERALS", "7 ROCK WAY", "55555")  // shares APEX token; labeled, different company
        };
        var truth = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["g1"] = "apex", ["g2"] = "apex",
            ["g3"] = "helios", ["g4"] = "helios",
            ["g6"] = "minerals",
            ["ghost"] = "absent-company" // names a record not in the input -> skipped, counted
        };
        return (records, truth);
    }

    [Fact]
    public void Metrics_UseExactDefinitions()
    {
        var (records, truth) = GroundTruthFixture();
        var result = NewService().Audit(records, OrgProfile(), truth);

        // True pairs: g1-g2, g3-g4 => 2. Predicted positive: g1-g2 only (labeled,
        // reachable, comparable, >= auto). g3-g4 unreachable -> FN.
        Assert.NotNull(result.Metrics);
        Assert.Equal(2, result.Metrics!.TruePairs);
        Assert.Equal(1, result.Metrics.PredictedPositives);
        Assert.Equal(1, result.Metrics.TruePositives);
        Assert.Equal(1.0, result.Metrics.Precision);
        Assert.Equal(0.5, result.Metrics.Recall);

        // Coverage: 5 labeled records present, 1 skipped ground-truth row (ghost),
        // pairs touching unlabeled g5 excluded from metrics.
        Assert.NotNull(result.Coverage);
        Assert.Equal(5, result.Coverage!.LabeledRecordCount);
        Assert.Equal(1, result.Coverage.SkippedGroundTruthRows);
        Assert.True(result.Coverage.UnlabeledEndpointPairs > 0);
    }

    [Fact]
    public void UnreachableTruePair_GetsOfflineScore_ExcludedFromMetricsAndSweep()
    {
        var (records, truth) = GroundTruthFixture();
        var result = NewService().Audit(records, OrgProfile(), truth);

        var unreachable = result.Pairs.Single(p => p.EngineBand == ScoreBand.Unreachable);
        Assert.Equal(("g3", "g4"), (unreachable.LeftSourceRecordId, unreachable.RightSourceRecordId));
        Assert.False(unreachable.Reachable);
        Assert.Null(unreachable.Score);
        Assert.NotNull(unreachable.OfflineScore);
        Assert.NotNull(unreachable.WouldBeBand);
        Assert.True(unreachable.IsTrue);

        // Sweep rows never count unreachable pairs: every row's PredictedPositives
        // must be <= the number of labeled reachable comparable candidate pairs.
        var labeledReachable = result.Pairs.Count(p => p.Reachable && p.Comparable && p.IsTrue is not null);
        Assert.All(result.Sweep, row => Assert.True(row.PredictedPositives <= labeledReachable));
    }

    [Fact]
    public void MissDecomposition_AttributesEveryTruePairOnce()
    {
        var (records, truth) = GroundTruthFixture();
        var result = NewService().Audit(records, OrgProfile(), truth);

        Assert.NotNull(result.Misses);
        var m = result.Misses!;
        Assert.Equal(2, m.TruePairs);
        Assert.Equal(1, m.AutoMatched);
        Assert.Equal(1, m.Unreachable);
        Assert.Equal(0, m.NonComparable);
        Assert.Equal(0, m.InReview);
        Assert.Equal(0, m.BelowReview);
        Assert.Equal(m.TruePairs, m.AutoMatched + m.Unreachable + m.NonComparable + m.InReview + m.BelowReview);
    }

    [Fact]
    public void Sweep_RowsAreDistinctObservedScores_PlusEffectiveThreshold()
    {
        var (records, truth) = GroundTruthFixture();
        var result = NewService().Audit(records, OrgProfile(), truth);

        Assert.NotEmpty(result.Sweep);
        Assert.Single(result.Sweep, r => r.IsEffectiveThreshold && r.Cut == 0.41);
        var cuts = result.Sweep.Select(r => r.Cut).ToList();
        Assert.Equal(cuts.OrderBy(c => c).ToList(), cuts);          // ascending
        Assert.Equal(cuts.Distinct().Count(), cuts.Count);          // distinct
        // At the lowest observed cut, every labeled comparable reachable pair is predicted positive.
        var labeled = result.Pairs.Count(p => p.Reachable && p.Comparable && p.IsTrue is not null);
        Assert.Equal(labeled, result.Sweep.First().PredictedPositives);
    }

    [Fact]
    public void Diagnostics_ListTrueBelowAuto_AndFalseAtOrAboveReview()
    {
        var (records, truth) = GroundTruthFixture();
        var result = NewService().Audit(records, OrgProfile(), truth);

        // g3-g4 is the true pair that failed (unreachable) -> in TrueBelowAuto.
        Assert.Contains(result.TrueBelowAuto,
            p => p.LeftSourceRecordId == "g3" && p.RightSourceRecordId == "g4");
        // g1-g2 matched -> never in TrueBelowAuto.
        Assert.DoesNotContain(result.TrueBelowAuto,
            p => p.LeftSourceRecordId == "g1" && p.RightSourceRecordId == "g2");
        // False hazards: labeled false pairs at/above review. Fixture false pairs
        // (g6 vs g1/g2) score ~4*(1/3)/7 = 0.19 < 0.31 -> empty list.
        Assert.Empty(result.FalseAtOrAboveReview);
        // Unlabeled g5's pairs are in NEITHER list.
        Assert.DoesNotContain(result.TrueBelowAuto, p => p.LeftSourceRecordId == "g5" || p.RightSourceRecordId == "g5");
        Assert.DoesNotContain(result.FalseAtOrAboveReview, p => p.LeftSourceRecordId == "g5" || p.RightSourceRecordId == "g5");
    }

    [Fact]
    public void ZeroDenominators_ReportNull_NotZeroOrOne()
    {
        // Ground truth with only singleton clusters -> zero true pairs -> recall n/a.
        // High thresholds -> zero predicted positives -> precision n/a.
        var records = new[] { Rec("s1", "IRIS OPTICS", "1 A", "1"), Rec("s2", "IRIS METALS", "2 B", "2") };
        var truth = new Dictionary<string, string>(StringComparer.Ordinal) { ["s1"] = "one", ["s2"] = "two" };
        var result = NewService().Audit(records, OrgProfile(), truth,
            autoThresholdOverride: 0.99, reviewThresholdOverride: 0.98);

        Assert.NotNull(result.Metrics);
        Assert.Null(result.Metrics!.Precision);
        Assert.Null(result.Metrics.Recall);
        Assert.Null(result.Metrics.F1);
    }

    [Fact]
    public void ThresholdOverrides_ChangeBands()
    {
        // Names differ by one token: jaccard {VEGA,SYSTEMS,X} vs {VEGA,SYSTEMS,Y} = 2/4 = 0.5.
        // Same address (1.0) and postal (1.0): weighted = (4*0.5 + 2.5*1 + 0.5*1)/7 = 5/7 = 0.714,
        // below the 0.75 review-floor gate so the raw weighted score stands.
        // 0.714 >= 0.41 -> Auto at profile thresholds; 0.714 < 0.999 -> NoMatch under the override.
        var records = new[] { Rec("t1", "VEGA SYSTEMS X", "1 A", "1"), Rec("t2", "VEGA SYSTEMS Y", "1 A", "1") };
        var strict = NewService().Audit(records, OrgProfile(),
            autoThresholdOverride: 0.999, reviewThresholdOverride: 0.99);
        var loose = NewService().Audit(records, OrgProfile());
        Assert.Equal(1, loose.Bands.Auto);
        Assert.Equal(0, strict.Bands.Auto);
        Assert.True(strict.ThresholdsOverridden);
        Assert.False(loose.ThresholdsOverridden);
    }
}
