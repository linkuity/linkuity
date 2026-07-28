namespace Linkuity.Pipeline.Tests;

public class CorpusAuditBaselineTests
{
    private static CorpusAuditBaseline Make(
        string recordsHash = "R", string truthHash = "T", string profileHash = "P", string strataHash = "S",
        int? maxBlockSize = 50, double auto = 0.41,
        long tp = 100, long pp = 100, long ap = 200, long reachable = 150, long truePairs = 200,
        params (Stratum Id, long TruePairs, long PostClusterTruePositive)[] strata)
    {
        var rows = strata.Length > 0
            ? strata.Select(s => new BaselineStratum(s.Id, s.TruePairs, s.PostClusterTruePositive)).ToList()
            : [new BaselineStratum(Stratum.S1Identical, 200, 100)];
        return new CorpusAuditBaseline(1, "2026-07-28T00:00:00Z",
            new BaselineInputs(recordsHash, truthHash, profileHash, "C", strataHash, maxBlockSize, auto, 0.31, 0.75),
            new BaselineCounts(1000, truePairs, 5000, ap, pp, tp, reachable), rows);
    }

    [Theory]
    [InlineData("recordsSha256")]
    [InlineData("groundTruthSha256")]
    [InlineData("profileSha256")]
    [InlineData("strataSha256")]
    [InlineData("maxBlockSize")]
    [InlineData("autoMatchThreshold")]
    public void RefusesToCompareWhenAnInputDiffers(string changed)
    {
        var baseline = Make();
        var current = changed switch
        {
            "recordsSha256"     => Make(recordsHash: "X"),
            "groundTruthSha256" => Make(truthHash: "X"),
            "profileSha256"     => Make(profileHash: "X"),
            "strataSha256"      => Make(strataHash: "X"),
            "maxBlockSize"      => Make(maxBlockSize: 40),
            _                   => Make(auto: 0.50)
        };

        var comparison = CorpusAuditBaseline.Compare(baseline, current, acceptProfileChange: false);

        Assert.True(comparison.Refused);
        Assert.Contains(changed, comparison.RefusalReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The approved split: evaluation inputs always refuse; system-under-test configuration
    /// (profile, block size, thresholds) can be acknowledged with acceptProfileChange.
    /// </summary>
    [Theory]
    [InlineData("profileSha256")]
    [InlineData("maxBlockSize")]
    [InlineData("autoMatchThreshold")]
    public void AcceptProfileChangeSuppressesRefusalForConfigurationOnly(string changed)
    {
        var current = changed switch
        {
            "profileSha256" => Make(profileHash: "X"),
            "maxBlockSize"  => Make(maxBlockSize: 40),
            _               => Make(auto: 0.50)
        };

        var comparison = CorpusAuditBaseline.Compare(Make(), current, acceptProfileChange: true);

        Assert.False(comparison.Refused);
        Assert.Null(comparison.RefusalReason);
    }

    [Theory]
    [InlineData("recordsSha256")]
    [InlineData("groundTruthSha256")]
    [InlineData("strataSha256")]
    public void AcceptProfileChangeDoesNotSuppressRefusalForEvaluationInputs(string changed)
    {
        var current = changed switch
        {
            "recordsSha256"     => Make(recordsHash: "X"),
            "groundTruthSha256" => Make(truthHash: "X"),
            _                   => Make(strataHash: "X")
        };

        var comparison = CorpusAuditBaseline.Compare(Make(), current, acceptProfileChange: true);

        Assert.True(comparison.Refused);
        Assert.Contains(changed, comparison.RefusalReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesWhenStratumSetDiffers()
    {
        var baseline = Make(strata: [(Stratum.S1Identical, 200, 100)]);
        var current = Make(strata: [(Stratum.S1Identical, 200, 100), (Stratum.S5Disjoint, 0, 0)]);

        var comparison = CorpusAuditBaseline.Compare(baseline, current, acceptProfileChange: false);

        Assert.True(comparison.Refused);
        Assert.Contains("stratum", comparison.RefusalReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A refusal is not a failure: the two outcomes map to different CLI exit codes and
    /// must never be collapsed, or a refusal sends someone chasing a regression that does not exist.</summary>
    [Fact]
    public void RefusalCarriesNoFailures()
    {
        var comparison = CorpusAuditBaseline.Compare(
            Make(reachable: 150), Make(recordsHash: "X", reachable: 1), acceptProfileChange: false);

        Assert.True(comparison.Refused);
        Assert.Empty(comparison.Failures);
    }

    [Fact]
    public void FailsWhenPrecisionDecreases()
    {
        var comparison = CorpusAuditBaseline.Compare(Make(tp: 100, pp: 100), Make(tp: 100, pp: 101), false);
        Assert.False(comparison.Refused);
        Assert.Contains(comparison.Failures, f => f.Contains("precision", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PassesWhenPrecisionUnchangedAtDifferentScale()
    {
        var comparison = CorpusAuditBaseline.Compare(Make(tp: 100, pp: 200), Make(tp: 150, pp: 300), false);
        Assert.DoesNotContain(comparison.Failures, f => f.Contains("precision", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FailsWhenReachabilityDecreases()
        => Assert.Contains(
            CorpusAuditBaseline.Compare(Make(reachable: 150), Make(reachable: 149), false).Failures,
            f => f.Contains("reachability", StringComparison.OrdinalIgnoreCase));

    /// <summary>Exactly 0.5pp must NOT fail — the rule is "more than 0.5pp". 100/1000 = 10.00%,
    /// 95/1000 = 9.50%. Computed with BigInteger cross-multiplication, so this boundary cannot
    /// depend on floating-point rounding.</summary>
    [Fact]
    public void StratumRecallDropOfExactlyHalfAPointPasses()
    {
        var baseline = Make(strata: [(Stratum.S4WeakOverlap, 1000, 100)]);
        var current = Make(strata: [(Stratum.S4WeakOverlap, 1000, 95)]);
        Assert.Empty(CorpusAuditBaseline.Compare(baseline, current, false).Failures);
    }

    [Fact]
    public void StratumRecallDropJustOverHalfAPointFails()
    {
        var baseline = Make(strata: [(Stratum.S4WeakOverlap, 10000, 1000)]);   // 10.00%
        var current = Make(strata: [(Stratum.S4WeakOverlap, 10000, 949)]);     // 9.49%, a 0.51pp drop
        Assert.Contains(CorpusAuditBaseline.Compare(baseline, current, false).Failures,
            f => f.Contains("S4WeakOverlap", StringComparison.Ordinal));
    }

    /// <summary>Pins the exact tick either side of the boundary at one scale: 950/10000 is a
    /// 0.50pp drop and passes, 949/10000 is 0.51pp and fails. Without this pair the two boundary
    /// tests above could both sit on one side of the comparison.</summary>
    [Fact]
    public void StratumRecallBoundaryIsStraddledAtASingleScale()
    {
        var baseline = Make(strata: [(Stratum.S4WeakOverlap, 10000, 1000)]);

        Assert.Empty(CorpusAuditBaseline.Compare(
            baseline, Make(strata: [(Stratum.S4WeakOverlap, 10000, 950)]), false).Failures);
        Assert.NotEmpty(CorpusAuditBaseline.Compare(
            baseline, Make(strata: [(Stratum.S4WeakOverlap, 10000, 949)]), false).Failures);
    }

    [Fact]
    public void StratumRecallImprovementNeverFails()
    {
        var baseline = Make(strata: [(Stratum.S4WeakOverlap, 1000, 100)]);
        var current = Make(strata: [(Stratum.S4WeakOverlap, 1000, 900)]);
        Assert.Empty(CorpusAuditBaseline.Compare(baseline, current, false).Failures);
    }

    [Fact]
    public void EmptyStratumIsComparedWithoutDivideByZero()
    {
        var baseline = Make(strata: [(Stratum.S5Disjoint, 0, 0)]);
        var current = Make(strata: [(Stratum.S5Disjoint, 0, 0)]);
        var comparison = CorpusAuditBaseline.Compare(baseline, current, false);
        Assert.False(comparison.Refused);
        Assert.Empty(comparison.Failures);
    }

    /// <summary>An empty stratum has no recall to render — "n/a", never 0.0, which would read
    /// as a total miss.</summary>
    [Fact]
    public void EmptyStratumRecallRendersAsNotApplicable()
    {
        var empty = new BaselineStratum(Stratum.S5Disjoint, 0, 0);
        Assert.Null(empty.PostClusterPairwiseRecall);
        Assert.Equal("n/a", empty.RecallDisplay, StringComparer.Ordinal);

        var populated = new BaselineStratum(Stratum.S4WeakOverlap, 1000, 95);
        Assert.Equal(0.095, populated.PostClusterPairwiseRecall!.Value, 12);
    }

    [Fact]
    public void ZeroPredictedPositiveIsTreatedAsPrecisionRegression()
    {
        // No merges at all: precision is undefined. Treat as a regression rather than a pass,
        // because "merged nothing" must never look like "precision held".
        var comparison = CorpusAuditBaseline.Compare(Make(tp: 100, pp: 100), Make(tp: 0, pp: 0), false);
        Assert.Contains(comparison.Failures, f => f.Contains("precision", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        var original = Make();
        var restored = CorpusAuditBaseline.FromJson(CorpusAuditBaseline.ToJson(original));
        var comparison = CorpusAuditBaseline.Compare(original, restored, false);
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

    [Fact]
    public void CreateProjectsTheAuditResultOntoTheBaselineShape()
    {
        var result = new CorpusAuditResult(
            new CorpusAuditInputs(50, 0.41, 0.31, 0.75, []),
            new CorpusAuditCounts(1000, 0, 0, 200, 5000, 5200, 200, 100, 100, 150, 90, 3),
            new CorpusAuditMetrics(0.75, 0.45, 0.5, 1.0),
            new CorpusAuditClusterSummary(900, 4, 40, 860),
            [new CorpusStratumRow(Stratum.S1Identical, 200, 150, 100, 20, 30, 0, 100)],
            []);
        var inputs = new BaselineInputs("R", "T", "P", "C", "S", 50, 0.41, 0.31, 0.75);

        var baseline = CorpusAuditBaseline.Create(result, inputs, "2026-07-28T00:00:00Z");

        Assert.Equal(CorpusAuditBaseline.CurrentSchemaVersion, baseline.SchemaVersion);
        Assert.Equal(1000, baseline.Counts.Records);
        Assert.Equal(200, baseline.Counts.TruePairs);
        Assert.Equal(5000, baseline.Counts.CandidatePairs);
        Assert.Equal(200, baseline.Counts.ActualPositive);
        Assert.Equal(100, baseline.Counts.PredictedPositive);
        Assert.Equal(100, baseline.Counts.TruePositive);
        Assert.Equal(150, baseline.Counts.ReachableTruePairs);
        var row = Assert.Single(baseline.Strata);
        Assert.Equal(new BaselineStratum(Stratum.S1Identical, 200, 100), row);
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
