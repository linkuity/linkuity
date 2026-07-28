namespace Linkuity.Pipeline.Tests;

public class CorpusAuditFrozenStrataTests
{
    private static readonly IReadOnlyList<TruePairOutcome> CurrentRun =
    [
        new("a", "b", Stratum.S5Disjoint, true, CorpusBand.NoMatch, 0.1, SameCluster: false),
        new("c", "d", Stratum.S4WeakOverlap, true, CorpusBand.Auto, 0.9, SameCluster: true)
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

    /// <summary>Spec §8 requires per stratum: true pairs, reachability, and each reachable pair's
    /// outcome. A frozen cohort is a stratum row like any other and must carry the same breakdown,
    /// counted the same way — non-comparable never folded into no-match.</summary>
    [Fact]
    public void FrozenCohortCarriesTheFullOutcomeBreakdown()
    {
        IReadOnlyList<TruePairOutcome> run =
        [
            new("a", "b", Stratum.S4WeakOverlap, true, CorpusBand.Auto, 0.9, SameCluster: true),
            new("c", "d", Stratum.S4WeakOverlap, true, CorpusBand.Review, 0.35, SameCluster: false),
            new("e", "f", Stratum.S4WeakOverlap, true, CorpusBand.NoMatch, 0.1, SameCluster: false),
            new("g", "h", Stratum.S4WeakOverlap, true, CorpusBand.NonComparable, null, SameCluster: false),
            new("i", "j", Stratum.S4WeakOverlap, false, null, null, SameCluster: false)
        ];
        IReadOnlyList<FrozenStratumAssignment> frozen =
        [
            new("a", "b", Stratum.S4WeakOverlap), new("c", "d", Stratum.S4WeakOverlap),
            new("e", "f", Stratum.S4WeakOverlap), new("g", "h", Stratum.S4WeakOverlap),
            new("i", "j", Stratum.S4WeakOverlap)
        ];

        var s4 = CorpusAuditBaseline.AggregateByFrozenStratum(run, frozen)
            .Single(r => r.Id == Stratum.S4WeakOverlap);

        Assert.Equal(new BaselineStratum(Stratum.S4WeakOverlap,
            TruePairs: 5, Reachable: 4, Auto: 1, Review: 1, NoMatch: 1, NonComparable: 1,
            PostClusterTruePositive: 1), s4);
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

        // Assert on the parenthesized pair form, not the bare id: "c" also occurs in "current"
        // and "changed" in the same message, so a bare-substring assertion would pass even if the
        // wrong pair were named.
        Assert.Contains("(c, d)", ex.Message, StringComparison.Ordinal);
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
    /// hash is an integrity input, so an unstable ordering would fail verification spuriously.</summary>
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

    /// <summary>A headerless file would otherwise lose its first pair to the unconditional
    /// line-0 skip — silently removing a true pair from the frozen set and un-gating it.</summary>
    [Fact]
    public void HeaderlessStrataFileIsRejectedRatherThanLosingItsFirstPair()
    {
        var headerless = "a,b,S4WeakOverlap\nc,d,S4WeakOverlap\n";

        var ex = Assert.Throws<ArgumentException>(() => CorpusAuditBaseline.ReadStrataCsv(headerless));
        Assert.Contains("header", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyStrataFileIsRejected()
        => Assert.Throws<ArgumentException>(() => CorpusAuditBaseline.ReadStrataCsv(string.Empty));

    // ---- sidecar integrity ----

    /// <summary>
    /// strataSha256 means "the sidecar on disk is still the one this baseline was written
    /// against", NOT "the current run's strata match the baseline's". The latter is impossible:
    /// §8.1 says reclassification is reported rather than gated, so the current run's
    /// classification is expected to differ and comparing the two would refuse every informative
    /// run. Integrity is the only coherent reading, and it matters because the corpus directory is
    /// not version-controlled.
    /// </summary>
    [Fact]
    public void StrataFileMatchingItsRecordedHashIsAccepted()
    {
        var csv = CorpusAuditBaseline.WriteStrataCsv(Frozen);

        var restored = CorpusAuditBaseline.ReadStrataCsv(csv, CorpusAuditBaseline.Sha256Of(csv));

        Assert.Equal(Frozen, restored);
    }

    [Fact]
    public void EditedStrataFileIsRejectedByItsRecordedHash()
    {
        var original = CorpusAuditBaseline.WriteStrataCsv(Frozen);
        var recordedHash = CorpusAuditBaseline.Sha256Of(original);

        // One pair quietly reassigned out of the gated cohort into excluded S5 — exactly the
        // edit the frozen sidecar exists to prevent, and undetectable without the hash.
        var tampered = original.Replace("c,d,S4WeakOverlap", "c,d,S5Disjoint", StringComparison.Ordinal);
        Assert.NotEqual(original, tampered);

        var ex = Assert.Throws<InvalidOperationException>(
            () => CorpusAuditBaseline.ReadStrataCsv(tampered, recordedHash));
        Assert.Contains(recordedHash, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReclassificationCountIsZeroWhenNothingMoved()
    {
        IReadOnlyList<FrozenStratumAssignment> unchanged =
            [new("a", "b", Stratum.S5Disjoint), new("c", "d", Stratum.S4WeakOverlap)];
        Assert.Equal(0, CorpusAuditBaseline.CountReclassified(CurrentRun, unchanged));
    }
}
