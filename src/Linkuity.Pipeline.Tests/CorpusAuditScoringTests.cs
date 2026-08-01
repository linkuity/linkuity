using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline.Tests;

/// <summary>Returns 0.9 when left.SourceRecordId is ordinal-lower, 0.1 otherwise. Deliberately
/// asymmetric so ScorePair's max rule is actually discriminated — the shipped evaluator is
/// symmetric, so testing with it proves nothing.</summary>
internal sealed class AsymmetricSimilarityStrategy : ISimilarityStrategy
{
    public string Name => "asymmetric-test";

    // Emits one signal per profile field, so it pairs with the per-field scorers.
    public SignalShape Produces => SignalShape.PerField;

    public IReadOnlyList<SimilaritySignal> Evaluate(EntityRecord left, EntityRecord right, MatchingProfile profile)
        => [new SimilaritySignal("organization_name",
            string.CompareOrdinal(left.SourceRecordId, right.SourceRecordId) < 0 ? 0.9 : 0.1)];
}

public class CorpusAuditScoringTests
{
    private static EntityRecord[] Normalized(params (string Id, string Name)[] rows)
    {
        var registry = MatchingDefaults.CreateRegistry();
        var profile = CorpusAuditFixtures.Profile();
        var normalization = registry.Normalization[profile.NormalizationStrategy];
        return rows.Select(r => normalization.Normalize(CorpusAuditFixtures.Org(r.Id, r.Name), profile)).ToArray();
    }

    [Fact]
    public void UnionFind_MergesTransitively()
    {
        var uf = new CorpusAuditService.UnionFind(5);
        uf.Union(0, 1);
        uf.Union(1, 2);
        Assert.Equal(uf.Find(0), uf.Find(2));
        Assert.NotEqual(uf.Find(0), uf.Find(3));
    }

    [Fact]
    public void UnionFind_IsIdempotent()
    {
        var uf = new CorpusAuditService.UnionFind(3);
        uf.Union(0, 1);
        uf.Union(0, 1);
        uf.Union(1, 0);
        Assert.Equal(uf.Find(0), uf.Find(1));
        Assert.NotEqual(uf.Find(0), uf.Find(2));
    }

    /// <summary>
    /// The batch path takes the MAX of both directions (BatchMatchingService.cs:72). With an
    /// asymmetric strategy, (a,b) yields 0.9 and (b,a) yields 0.1; ScorePair must return the
    /// 0.9-derived score regardless of argument order. A symmetric evaluator cannot detect a
    /// broken max rule, which is why this test injects its own strategy.
    /// </summary>
    [Fact]
    public void ScorePair_TakesMaxOfBothDirections()
    {
        var profile = CorpusAuditFixtures.Profile();
        var scoring = MatchingDefaults.CreateRegistry().Scoring[profile.ScoringStrategy];
        var similarity = new AsymmetricSimilarityStrategy();
        var records = Normalized(("a", "ANYTHING"), ("b", "ANYTHING ELSE"));

        var forward = CorpusAuditService.ScorePair(records, 0, 1, similarity, scoring, profile, out _, out _);
        var reverse = CorpusAuditService.ScorePair(records, 1, 0, similarity, scoring, profile, out _, out _);

        Assert.Equal(0.9, forward, precision: 6);
        Assert.Equal(0.9, reverse, precision: 6);
    }

    [Fact]
    public void ScorePair_ReportsNonComparableWhenAFieldIsBlankOnOneSide()
    {
        var profile = CorpusAuditFixtures.Profile();
        var registry = MatchingDefaults.CreateRegistry();
        var records = Normalized(("a", "ACME WIDGETS INC"), ("b", ""));

        CorpusAuditService.ScorePair(records, 0, 1,
            registry.Similarity[profile.SimilarityStrategy], registry.Scoring[profile.ScoringStrategy],
            profile, out var comparable, out _);

        Assert.False(comparable);
    }

    /// <summary>
    /// The review floor lifts a score to 0.80 only when the weighted value is in [0.75, 0.80).
    /// Canonical tokens {ALPHA,BETA,GAMMA} vs {ALPHA,BETA,GAMMA,DELTA} give Jaccard 3/4 = 0.75,
    /// which is lifted to exactly 0.80. A similarity of 1.0 lifts NOTHING — max(0.80, 1.0) is
    /// 1.0 — which is why the fixture must sit in the narrow band.
    /// </summary>
    [Fact]
    public void ScorePair_FlagsFloorLiftedPairs()
    {
        var profile = CorpusAuditFixtures.Profile();
        var registry = MatchingDefaults.CreateRegistry();
        var records = Normalized(("a", "ALPHA BETA GAMMA CORP"), ("b", "ALPHA BETA GAMMA DELTA CORP"));

        var score = CorpusAuditService.ScorePair(records, 0, 1,
            registry.Similarity[profile.SimilarityStrategy], registry.Scoring[profile.ScoringStrategy],
            profile, out _, out var floorLifted);

        Assert.Equal(0.80, score, precision: 6);
        Assert.True(floorLifted);
    }

    [Fact]
    public void ScorePair_DoesNotFlagLiftWhenSimilarityIsOne()
    {
        var profile = CorpusAuditFixtures.Profile();
        var registry = MatchingDefaults.CreateRegistry();
        var records = Normalized(("a", "ACME WIDGETS INC"), ("b", "ACME WIDGETS"));

        var score = CorpusAuditService.ScorePair(records, 0, 1,
            registry.Similarity[profile.SimilarityStrategy], registry.Scoring[profile.ScoringStrategy],
            profile, out _, out var floorLifted);

        Assert.Equal(1.0, score, precision: 6);
        Assert.False(floorLifted);
    }

    [Theory]
    [InlineData(0.50, CorpusBand.Auto)]
    [InlineData(0.41, CorpusBand.Auto)]
    [InlineData(0.35, CorpusBand.Review)]
    [InlineData(0.31, CorpusBand.Review)]
    [InlineData(0.10, CorpusBand.NoMatch)]
    public void BandOf_UsesInclusiveLowerBounds(double score, CorpusBand expected)
        => Assert.Equal(expected, CorpusAuditService.BandOf(score, comparable: true, CorpusAuditFixtures.Profile()));

    [Fact]
    public void BandOf_NonComparableBeatsAnyScore()
        => Assert.Equal(CorpusBand.NonComparable,
            CorpusAuditService.BandOf(0.99, comparable: false, CorpusAuditFixtures.Profile()));
}
