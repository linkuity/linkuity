namespace Linkuity.Pipeline.Tests;

public class CorpusAuditBaselineTests
{
    /// <summary>Every refusal input is a parameter. A hardcoded input is an untested input: if
    /// Compare's corresponding Refuse call were deleted, no test would notice.</summary>
    private static CorpusAuditBaseline Make(
        string recordsHash = "R", string truthHash = "T", string profileHash = "P",
        string corpusSourceHash = "C", string strataHash = "S",
        int? maxBlockSize = 50, double auto = 0.41, double review = 0.31, double floorGate = 0.75,
        long tp = 100, long pp = 100, long ap = 200, long reachable = 150, long directAuto = 140,
        long truePairs = 200,
        params (Stratum Id, long TruePairs, long PostClusterTruePositive)[] strata)
    {
        var rows = strata.Length > 0
            ? strata.Select(s => Row(s.Id, s.TruePairs, s.PostClusterTruePositive)).ToList()
            : [Row(Stratum.S1Identical, 200, 100)];
        return new CorpusAuditBaseline(1, "2026-07-28T00:00:00Z",
            new BaselineInputs(recordsHash, truthHash, profileHash, corpusSourceHash, strataHash,
                maxBlockSize, auto, review, floorGate),
            new BaselineCounts(1000, truePairs, 5000, ap, pp, tp, reachable, directAuto), rows);
    }

    /// <summary>The gate reads only TruePairs and PostClusterTruePositive; the outcome breakdown
    /// is filled plausibly so the artifact shape is exercised without the gate depending on it.</summary>
    private static BaselineStratum Row(Stratum id, long truePairs, long postClusterTp)
        => new(id, truePairs, truePairs, postClusterTp, 0, truePairs - postClusterTp, 0, postClusterTp);

    private static CorpusAuditBaseline Vary(string changed) => changed switch
    {
        "recordsSha256"      => Make(recordsHash: "X"),
        "groundTruthSha256"  => Make(truthHash: "X"),
        "corpusSourceSha256" => Make(corpusSourceHash: "X"),
        "strataSha256"       => Make(strataHash: "X"),
        "profileSha256"      => Make(profileHash: "X"),
        "maxBlockSize"       => Make(maxBlockSize: 40),
        "autoMatchThreshold" => Make(auto: 0.50),
        "reviewThreshold"    => Make(review: 0.29),
        "reviewFloorGate"    => Make(floorGate: 0.80),
        _                    => throw new ArgumentOutOfRangeException(nameof(changed), changed, null)
    };

    // ---- refusal: the two categories (spec §10) ----

    [Theory]
    [InlineData("recordsSha256")]
    [InlineData("groundTruthSha256")]
    [InlineData("corpusSourceSha256")]
    [InlineData("strataSha256")]
    [InlineData("profileSha256")]
    [InlineData("maxBlockSize")]
    [InlineData("autoMatchThreshold")]
    [InlineData("reviewThreshold")]
    [InlineData("reviewFloorGate")]
    public void RefusesToCompareWhenAnInputDiffers(string changed)
    {
        var comparison = CorpusAuditBaseline.Compare(Make(), Vary(changed), acceptProfileChange: false, 0);

        Assert.True(comparison.Refused);
        Assert.Contains(changed, comparison.RefusalReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The approved split: the corpus is the ruler and must never move; the profile is the thing
    /// being measured and is expected to. Only system-under-test configuration may be acknowledged.
    /// </summary>
    [Theory]
    [InlineData("profileSha256")]
    [InlineData("maxBlockSize")]
    [InlineData("autoMatchThreshold")]
    [InlineData("reviewThreshold")]
    [InlineData("reviewFloorGate")]
    public void AcceptProfileChangeSuppressesRefusalForConfigurationOnly(string changed)
    {
        var comparison = CorpusAuditBaseline.Compare(Make(), Vary(changed), acceptProfileChange: true, 0);

        Assert.False(comparison.Refused);
        Assert.Null(comparison.RefusalReason);
    }

    [Theory]
    [InlineData("recordsSha256")]
    [InlineData("groundTruthSha256")]
    [InlineData("corpusSourceSha256")]
    [InlineData("strataSha256")]
    public void AcceptProfileChangeDoesNotSuppressRefusalForEvaluationInputs(string changed)
    {
        var comparison = CorpusAuditBaseline.Compare(Make(), Vary(changed), acceptProfileChange: true, 0);

        Assert.True(comparison.Refused);
        Assert.Contains(changed, comparison.RefusalReason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusesWhenStratumSetDiffers(bool acceptProfileChange)
    {
        var baseline = Make(strata: [(Stratum.S1Identical, 200, 100)]);
        var current = Make(strata: [(Stratum.S1Identical, 200, 100), (Stratum.S5Disjoint, 0, 0)]);

        var comparison = CorpusAuditBaseline.Compare(baseline, current, acceptProfileChange, 0);

        Assert.True(comparison.Refused);
        Assert.Contains("stratum", comparison.RefusalReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rule 3 compares raw reachable counts, which only means "reachability decreased"
    /// against an identical true-pair denominator.</summary>
    [Fact]
    public void RefusesWhenTheTruePairDenominatorChanges()
    {
        var comparison = CorpusAuditBaseline.Compare(
            Make(truePairs: 200), Make(truePairs: 201), acceptProfileChange: true, 0);

        Assert.True(comparison.Refused);
        Assert.Contains("truePairs", comparison.RefusalReason!, StringComparison.Ordinal);
    }

    /// <summary>A refusal is not a failure: the two outcomes map to different CLI exit codes and
    /// must never be collapsed, or a refusal sends someone chasing a regression that does not exist.</summary>
    [Fact]
    public void RefusalCarriesNoFailures()
    {
        var comparison = CorpusAuditBaseline.Compare(
            Make(reachable: 150), Make(recordsHash: "X", reachable: 1), acceptProfileChange: false, 0);

        Assert.True(comparison.Refused);
        Assert.Empty(comparison.Failures);
    }

    [Fact]
    public void ReclassifiedPairsIsPassedThroughOnBothOutcomes()
    {
        Assert.Equal(7, CorpusAuditBaseline.Compare(Make(), Make(), false, 7).ReclassifiedPairs);
        Assert.Equal(7, CorpusAuditBaseline.Compare(Make(), Make(recordsHash: "X"), false, 7).ReclassifiedPairs);
    }

    // ---- gate rules (spec §10) ----

    [Fact]
    public void FailsWhenPrecisionDecreases()
    {
        var comparison = CorpusAuditBaseline.Compare(Make(tp: 100, pp: 100), Make(tp: 100, pp: 101), false, 0);
        Assert.False(comparison.Refused);
        Assert.Contains(comparison.Failures, f => f.Contains("precision", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PassesWhenPrecisionUnchangedAtDifferentScale()
    {
        var comparison = CorpusAuditBaseline.Compare(Make(tp: 100, pp: 200), Make(tp: 150, pp: 300), false, 0);
        Assert.DoesNotContain(comparison.Failures, f => f.Contains("precision", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FailsWhenReachabilityDecreases()
        => Assert.Contains(
            CorpusAuditBaseline.Compare(Make(reachable: 150), Make(reachable: 149), false, 0).Failures,
            f => f.Contains("reachability", StringComparison.OrdinalIgnoreCase));

    /// <summary>Spec §10 lists exactly three failure rules and direct-auto recall is not among
    /// them. It is recorded so a transitivity-masked loss is visible, not so it fails the gate.</summary>
    [Fact]
    public void DirectAutoTruePairsIsRecordedButNotGated()
    {
        var comparison = CorpusAuditBaseline.Compare(Make(directAuto: 140), Make(directAuto: 10), false, 0);

        Assert.False(comparison.Refused);
        Assert.Empty(comparison.Failures);
    }

    /// <summary>Exactly 0.5pp must NOT fail — the rule is "more than 0.5pp". 100/1000 = 10.00%,
    /// 95/1000 = 9.50%. Computed with BigInteger cross-multiplication, so this boundary cannot
    /// depend on floating-point rounding.</summary>
    [Fact]
    public void StratumRecallDropOfExactlyHalfAPointPasses()
    {
        var baseline = Make(strata: [(Stratum.S4WeakOverlap, 1000, 100)]);
        var current = Make(strata: [(Stratum.S4WeakOverlap, 1000, 95)]);
        Assert.Empty(CorpusAuditBaseline.Compare(baseline, current, false, 0).Failures);
    }

    [Fact]
    public void StratumRecallDropJustOverHalfAPointFails()
    {
        var baseline = Make(strata: [(Stratum.S4WeakOverlap, 10000, 1000)]);   // 10.00%
        var current = Make(strata: [(Stratum.S4WeakOverlap, 10000, 949)]);     // 9.49%, a 0.51pp drop
        Assert.Contains(CorpusAuditBaseline.Compare(baseline, current, false, 0).Failures,
            f => f.Contains("S4WeakOverlap", StringComparison.Ordinal));
    }

    /// <summary>Pins the exact tick either side of the boundary at ONE scale: 950/10000 is a
    /// 0.50pp drop and passes, 949/10000 is 0.51pp and fails. The two tests above use different
    /// denominators, so on their own they are also satisfied by a naive "fail iff (b.tp - c.tp)
    /// exceeds 5" rule; this test rules that mutant out.</summary>
    [Fact]
    public void StratumRecallBoundaryIsStraddledAtASingleScale()
    {
        var baseline = Make(strata: [(Stratum.S4WeakOverlap, 10000, 1000)]);

        Assert.Empty(CorpusAuditBaseline.Compare(
            baseline, Make(strata: [(Stratum.S4WeakOverlap, 10000, 950)]), false, 0).Failures);
        Assert.NotEmpty(CorpusAuditBaseline.Compare(
            baseline, Make(strata: [(Stratum.S4WeakOverlap, 10000, 949)]), false, 0).Failures);
    }

    [Fact]
    public void StratumRecallImprovementNeverFails()
    {
        var baseline = Make(strata: [(Stratum.S4WeakOverlap, 1000, 100)]);
        var current = Make(strata: [(Stratum.S4WeakOverlap, 1000, 900)]);
        Assert.Empty(CorpusAuditBaseline.Compare(baseline, current, false, 0).Failures);
    }

    [Fact]
    public void EmptyStratumIsComparedWithoutDivideByZero()
    {
        var baseline = Make(strata: [(Stratum.S5Disjoint, 0, 0)]);
        var current = Make(strata: [(Stratum.S5Disjoint, 0, 0)]);
        var comparison = CorpusAuditBaseline.Compare(baseline, current, false, 0);
        Assert.False(comparison.Refused);
        Assert.Empty(comparison.Failures);
    }

    /// <summary>An empty stratum has no recall to render — "n/a", never 0.0, which would read
    /// as a total miss.</summary>
    [Fact]
    public void EmptyStratumRecallRendersAsNotApplicable()
    {
        var empty = new BaselineStratum(Stratum.S5Disjoint, 0, 0, 0, 0, 0, 0, 0);
        Assert.Null(empty.PostClusterPairwiseRecall);
        Assert.Equal("n/a", empty.RecallDisplay, StringComparer.Ordinal);

        var populated = Row(Stratum.S4WeakOverlap, 1000, 95);
        Assert.Equal(0.095, populated.PostClusterPairwiseRecall!.Value, 12);
    }

    [Fact]
    public void ZeroPredictedPositiveIsTreatedAsPrecisionRegression()
    {
        // No merges at all: precision is undefined. Treat as a regression rather than a pass,
        // because "merged nothing" must never look like "precision held".
        var comparison = CorpusAuditBaseline.Compare(Make(tp: 100, pp: 100), Make(tp: 0, pp: 0), false, 0);
        Assert.Contains(comparison.Failures, f => f.Contains("precision", StringComparison.OrdinalIgnoreCase));
    }

    // ---- artifact ----

    [Fact]
    public void RoundTripsThroughJson()
    {
        var original = Make();
        var restored = CorpusAuditBaseline.FromJson(CorpusAuditBaseline.ToJson(original));

        // Compare never inspects Records, TruePairs, CandidatePairs, ActualPositive,
        // DirectAutoTruePairs, CreatedUtc or the per-stratum outcome breakdown, so asserting
        // round-trip fidelity through Compare alone would miss a serializer dropping any of them.
        Assert.Equal(original.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(original.CreatedUtc, restored.CreatedUtc, StringComparer.Ordinal);
        Assert.Equal(original.Inputs, restored.Inputs);
        Assert.Equal(original.Counts, restored.Counts);
        Assert.Equal(original.Strata, restored.Strata);

        var comparison = CorpusAuditBaseline.Compare(original, restored, false, 0);
        Assert.False(comparison.Refused);
        Assert.Empty(comparison.Failures);
    }

    [Fact]
    public void RejectsAnUnsupportedSchemaVersion()
    {
        var json = CorpusAuditBaseline.ToJson(Make());
        var bumped = json.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 2", StringComparison.Ordinal);
        Assert.NotEqual(json, bumped);   // guards the test against a silent naming-policy change

        var ex = Assert.Throws<ArgumentException>(() => CorpusAuditBaseline.FromJson(bumped));
        Assert.Contains("schemaVersion", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Spec §6.1 requires all four named metrics from every run and §8 requires the full
    /// per-stratum outcome breakdown, so Create must carry every one of them onto the artifact.</summary>
    [Fact]
    public void CreateProjectsEveryRecordedQuantityOntoTheBaseline()
    {
        var result = new CorpusAuditResult(
            new CorpusAuditInputs(50, 0.41, 0.31, 0.75, []),
            new CorpusAuditCounts(1000, 0, 0, 200, 5000, 5200, 200, 100, 100, 150, 140, 3),
            new CorpusAuditMetrics(0.75, 0.7, 0.5, 1.0),
            new CorpusAuditClusterSummary(900, 4, 40, 860),
            [new CorpusStratumRow(Stratum.S1Identical, 200, 150, 100, 20, 25, 5, 100)],
            []);
        var inputs = new BaselineInputs("R", "T", "P", "C", "S", 50, 0.41, 0.31, 0.75);

        var baseline = CorpusAuditBaseline.Create(result, inputs, "2026-07-28T00:00:00Z");

        Assert.Equal(CorpusAuditBaseline.CurrentSchemaVersion, baseline.SchemaVersion);
        Assert.Equal("2026-07-28T00:00:00Z", baseline.CreatedUtc, StringComparer.Ordinal);
        Assert.Equal(inputs, baseline.Inputs);
        Assert.Equal(new BaselineCounts(1000, 200, 5000, 200, 100, 100, 150, 140), baseline.Counts);
        Assert.Equal(
            new BaselineStratum(Stratum.S1Identical, 200, 150, 100, 20, 25, 5, 100),
            Assert.Single(baseline.Strata));
    }

    // ---- immutability of the artifact ----

    [Fact]
    public void WriteAtomicRefusesToOverwriteAnExistingBaseline()
    {
        var dir = NewTempDirectory();
        try
        {
            CorpusAuditBaseline.WriteAtomic(dir, Make(), Strata, replace: false);
            var ex = Assert.Throws<InvalidOperationException>(
                () => CorpusAuditBaseline.WriteAtomic(dir, Make(reachable: 1), Strata, replace: false));
            Assert.Contains("--replace-baseline", ex.Message, StringComparison.Ordinal);

            // The refused write left the original artifact untouched.
            var onDisk = CorpusAuditBaseline.FromJson(File.ReadAllText(Path.Combine(dir, "baseline.json")));
            Assert.Equal(150, onDisk.Counts.ReachableTruePairs);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WriteAtomicOverwritesOnlyWhenReplaceIsRequested()
    {
        var dir = NewTempDirectory();
        try
        {
            CorpusAuditBaseline.WriteAtomic(dir, Make(), Strata, replace: false);
            CorpusAuditBaseline.WriteAtomic(dir, Make(reachable: 1), Strata, replace: true);

            var onDisk = CorpusAuditBaseline.FromJson(File.ReadAllText(Path.Combine(dir, "baseline.json")));
            Assert.Equal(1, onDisk.Counts.ReachableTruePairs);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WriteAtomicWritesBothArtifactsAndLeavesNoTempFiles()
    {
        var dir = NewTempDirectory();
        try
        {
            CorpusAuditBaseline.WriteAtomic(dir, Make(), Strata, replace: false);

            Assert.True(File.Exists(Path.Combine(dir, "baseline.json")));
            Assert.Equal(
                CorpusAuditBaseline.WriteStrataCsv(Strata),
                File.ReadAllText(Path.Combine(dir, "baseline-strata.csv")));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static readonly IReadOnlyList<FrozenStratumAssignment> Strata =
        [new("a", "b", Stratum.S4WeakOverlap)];

    private static string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkuity-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
