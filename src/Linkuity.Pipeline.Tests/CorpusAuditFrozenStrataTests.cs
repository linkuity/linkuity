namespace Linkuity.Pipeline.Tests;

public class CorpusAuditFrozenStrataTests
{
    private static readonly IReadOnlyList<TruePairOutcome> CurrentRun =
    [
        new("a", "b", Stratum.S5Disjoint, true, CorpusBand.NoMatch, 0.1, SameCluster: false, false),
        new("c", "d", Stratum.S4WeakOverlap, true, CorpusBand.Auto, 0.9, SameCluster: true, false)
    ];

    private static readonly IReadOnlyList<FrozenStratumAssignment> Frozen =
    [
        new("a", "b", Stratum.S4WeakOverlap),   // was S4, the current run now calls it S5
        new("c", "d", Stratum.S4WeakOverlap)
    ];

    /// <summary>
    /// The regression this exists to catch: a canonicalizer change moves a HARD pair out of S4
    /// into excluded S5. Bucketed by the CURRENT run, S4 would show 1/1 = 100% and look healthy.
    /// Bucketed by the BASELINE, S4 is 1/2 = 50% and the regression is visible.
    /// </summary>
    [Fact]
    public void PairMovingOutOfAStratumIsStillGatedInItsBaselineCohort()
    {
        var rows = CorpusAuditBaseline.AggregateByFrozenStratum(CurrentRun, Frozen);

        var s4 = rows.Single(r => r.Id == Stratum.S4WeakOverlap);
        Assert.Equal(2, s4.TruePairs);
        Assert.Equal(1, s4.PostClusterTruePositive);
        Assert.DoesNotContain(rows, r => r.Id == Stratum.S5Disjoint && r.TruePairs > 0);
    }

    /// <summary>Proves the frozen cohort is what makes the move visible: bucketing by the
    /// current run's own classification reports S4 at 1/1 and hides the regression.</summary>
    [Fact]
    public void BucketingByTheCurrentRunWouldHaveHiddenTheRegression()
    {
        var byCurrentRun = CurrentRun
            .GroupBy(o => o.Stratum)
            .ToDictionary(g => g.Key, g => (Pairs: g.LongCount(), Tp: g.LongCount(o => o.SameCluster)));

        Assert.Equal((1L, 1L), byCurrentRun[Stratum.S4WeakOverlap]);   // 100%, looks healthy

        var frozen = CorpusAuditBaseline.AggregateByFrozenStratum(CurrentRun, Frozen)
            .Single(r => r.Id == Stratum.S4WeakOverlap);
        Assert.Equal(0.5, frozen.PostClusterPairwiseRecall!.Value, 12);   // 50%, the truth
    }

    [Fact]
    public void ReclassificationIsCounted()
        => Assert.Equal(1, CorpusAuditBaseline.CountReclassified(CurrentRun, Frozen));

    [Fact]
    public void MissingFrozenPairIsRefused()
    {
        IReadOnlyList<TruePairOutcome> shorter = [CurrentRun[0]];
        var ex = Assert.Throws<ArgumentException>(
            () => CorpusAuditBaseline.AggregateByFrozenStratum(shorter, Frozen));
        Assert.Contains("c", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StrataFileRoundTripsDeterministically()
    {
        var csv = CorpusAuditBaseline.WriteStrataCsv(Frozen);
        var restored = CorpusAuditBaseline.ReadStrataCsv(csv);
        Assert.Equal(Frozen, restored);
        Assert.Equal(csv, CorpusAuditBaseline.WriteStrataCsv(restored));   // byte-stable
    }

    /// <summary>Ordering of the input must not change the bytes: the sidecar is hashed and the
    /// hash is a refusal input, so an unstable ordering would refuse every comparison.</summary>
    [Fact]
    public void StrataFileIsByteStableRegardlessOfInputOrder()
    {
        IReadOnlyList<FrozenStratumAssignment> reversed = [Frozen[1], Frozen[0]];
        Assert.Equal(CorpusAuditBaseline.WriteStrataCsv(Frozen), CorpusAuditBaseline.WriteStrataCsv(reversed));
    }

    [Fact]
    public void MalformedStrataRowIsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => CorpusAuditBaseline.ReadStrataCsv("left_id,right_id,stratum\na,b\n"));
        Assert.Contains("Malformed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReclassificationCountIsZeroWhenNothingMoved()
    {
        IReadOnlyList<FrozenStratumAssignment> unchanged =
            [new("a", "b", Stratum.S5Disjoint), new("c", "d", Stratum.S4WeakOverlap)];
        Assert.Equal(0, CorpusAuditBaseline.CountReclassified(CurrentRun, unchanged));
    }
}
