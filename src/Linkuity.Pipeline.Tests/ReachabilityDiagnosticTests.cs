using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Xunit;

namespace Linkuity.Pipeline.Tests;

public class ReachabilityDiagnosticTests
{
    private static ReachabilityDiagnosticResult Diagnose(
        (IReadOnlyList<EntityRecord> Records, MatchingProfile Profile, IReadOnlyDictionary<string, string> GroundTruth) fixture,
        int? maxBlockSize)
        => new ReachabilityDiagnosticService(MatchingDefaults.CreateRegistry())
            .Diagnose(fixture.Records, fixture.Profile, fixture.GroundTruth, maxBlockSize);

    // ------------------------------------------------------------------------------------
    // Fixture builders. Each is annotated with WHY it produces the cause it claims -- see
    // task-3-report.md for the manual trace against the actual strategy implementations
    // (fingerprint/token/acronym canonicalization, ExactValueBlockingStrategy's type
    // predicate, etc.) that was used to verify each one before relying on it.
    // ------------------------------------------------------------------------------------

    private static EntityRecord Org(string id, string name, params (string Field, string Value)[] extra)
    {
        var fields = new Dictionary<string, string> { ["organization_name"] = name };
        foreach (var (field, value) in extra) fields[field] = value;
        return new EntityRecord
        {
            Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
            SourceRecordId = id, Fields = fields, CreatedAt = DateTimeOffset.UnixEpoch
        };
    }

    // organization_name only, usable by fingerprint/token/acronym. Matches CorpusAuditFixtures.Profile().
    private static MatchingProfile NameOnlyProfile() => CorpusAuditFixtures.Profile();

    // organization_name (usable) plus address_line declared Blocking-ONLY (no Identifier role),
    // SemanticType.AddressLine. No configured strategy can key AddressLine: fingerprint/token
    // require a registered TokenCanonicalizers entry (only OrganizationName is registered),
    // acronym restricts to OrganizationName, and exact-value requires an exact semantic type
    // (Email/Phone/DomainName/DateOfBirth) or the Identifier role, neither of which AddressLine
    // has here. This makes address_line a genuine capability gap regardless of which of the
    // three configured strategies is asked.
    private static MatchingProfile NameAndAddressProfile() => new()
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
                Name = "address_line",
                SemanticType = SemanticFieldType.AddressLine,
                Roles = FieldRole.Blocking,
                SimilarityEvaluator = "exact",
                Weight = 1.0
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

    /// <summary>Four records with the IDENTICAL name "ACME TRADING LIMITED": r0..r3. Every
    /// fingerprint/token/acronym key they emit is therefore shared by all four, giving each such
    /// key a block size of 4 (KeyCount = 4). Ground truth links ONLY r0/r1 (r2, r3 are present to
    /// inflate the block, not to create extra true pairs), so there is exactly one true pair.
    /// At maxBlockSize = 2, a block of size 4 has per-query frequency 3 (> 2), so every one of
    /// those shared keys is suppressed: SharedKeysIgnoringSuppression(r0, r1) is non-empty but
    /// SharedActiveKeys(r0, r1) is empty -- cause A, exactly once.</summary>
    private static (IReadOnlyList<EntityRecord> Records, MatchingProfile Profile, IReadOnlyDictionary<string, string> GroundTruth) SuppressedSharedKeyFixture()
    {
        var records = new List<EntityRecord>
        {
            Org("r0", "ACME TRADING LIMITED"),
            Org("r1", "ACME TRADING LIMITED"),
            Org("r2", "ACME TRADING LIMITED"),
            Org("r3", "ACME TRADING LIMITED"),
        };
        var groundTruth = new Dictionary<string, string> { ["r0"] = "acme", ["r1"] = "acme" };
        return (records, NameOnlyProfile(), groundTruth);
    }

    /// <summary>Names "ZEBRA CORP" / "QUASAR HOLDINGS" share no fingerprint/token/acronym key
    /// (CORP and HOLDINGS are both legal suffixes stripped before comparison, and the remaining
    /// tokens ZEBRA/QUASAR share nothing). Both carry postal_code "94105", equal and non-empty,
    /// in a column the profile does not declare at all. No shared key anywhere and no
    /// declared-but-unusable Blocking field exists (organization_name is fully usable) -- so this
    /// is B2, not B1 or B3.</summary>
    private static (IReadOnlyList<EntityRecord> Records, MatchingProfile Profile, IReadOnlyDictionary<string, string> GroundTruth) SharedPostcodeUndeclaredFixture()
    {
        var records = new List<EntityRecord>
        {
            Org("p0", "ZEBRA CORP", ("postal_code", "94105")),
            Org("p1", "QUASAR HOLDINGS", ("postal_code", "94105")),
        };
        var groundTruth = new Dictionary<string, string> { ["p0"] = "zebra-quasar", ["p1"] = "zebra-quasar" };
        return (records, NameOnlyProfile(), groundTruth);
    }

    /// <summary>"ORION SYSTEMS" / "NIMBUS DESIGNS" share no fingerprint/token/acronym key (no
    /// common tokens, no legal suffixes involved), and the extra "country" column present on both
    /// records deliberately differs (US vs DE) -- proving the equality check is exercised and
    /// correctly returns false, not merely that no other column exists to compare.</summary>
    private static (IReadOnlyList<EntityRecord> Records, MatchingProfile Profile, IReadOnlyDictionary<string, string> GroundTruth) NoSharedValueFixture()
    {
        var records = new List<EntityRecord>
        {
            Org("n0", "ORION SYSTEMS", ("country", "US")),
            Org("n1", "NIMBUS DESIGNS", ("country", "DE")),
        };
        var groundTruth = new Dictionary<string, string> { ["n0"] = "orion-nimbus", ["n1"] = "orion-nimbus" };
        return (records, NameOnlyProfile(), groundTruth);
    }

    /// <summary>"KAPPA VENTURES" / "LYNX ENTERPRISES" share no name-derived key. Both carry the
    /// SAME address_line ("500 ELM ST", declared Blocking-only, unusable per
    /// NameAndAddressProfile) AND the SAME undeclared postal_code ("20002"). This pair therefore
    /// qualifies for BOTH B1 (capability gap on address_line) and B2 (undeclared postal_code) --
    /// pinning that B1 wins.</summary>
    private static (IReadOnlyList<EntityRecord> Records, MatchingProfile Profile, IReadOnlyDictionary<string, string> GroundTruth) DeclaredUnusableAndUndeclaredFixture()
    {
        var records = new List<EntityRecord>
        {
            Org("d0", "KAPPA VENTURES", ("address_line", "500 ELM ST"), ("postal_code", "20002")),
            Org("d1", "LYNX ENTERPRISES", ("address_line", "500 ELM ST"), ("postal_code", "20002")),
        };
        var groundTruth = new Dictionary<string, string> { ["d0"] = "kappa-lynx", ["d1"] = "kappa-lynx" };
        return (records, NameAndAddressProfile(), groundTruth);
    }

    /// <summary>"ALPHA LIMITED" / "BETA LIMITED": the only raw token they share is "LIMITED", a
    /// legal suffix that Canonicalize() strips before fingerprint/token/acronym ever see it --
    /// leaving ALPHA vs BETA, which share nothing. No shared key, no other column, so this also
    /// lands in B3; the normalization-loss flag is orthogonal on top of that.</summary>
    private static (IReadOnlyList<EntityRecord> Records, MatchingProfile Profile, IReadOnlyDictionary<string, string> GroundTruth) SuffixOnlySharedTokenFixture()
    {
        var records = new List<EntityRecord>
        {
            Org("s0", "ALPHA LIMITED"),
            Org("s1", "BETA LIMITED"),
        };
        var groundTruth = new Dictionary<string, string> { ["s0"] = "alpha-beta", ["s1"] = "alpha-beta" };
        return (records, NameOnlyProfile(), groundTruth);
    }

    /// <summary>One pair of each cause, all in one corpus, all with entirely disjoint vocabulary
    /// so no sub-fixture's keys leak into another's block-size counts:
    ///   - r0..r3 "ACME TRADING LIMITED" (r0/r1 ground-truthed)      -> cause A at maxBlockSize=2
    ///   - g0/g1  "GLOBEX UNIQUE CORP" (identical, block size 2)     -> reachable
    ///   - d0/d1  "KAPPA VENTURES" / "LYNX ENTERPRISES" + shared
    ///            address_line (capability gap)                     -> cause B1
    ///   - q0/q1  "ORCHID METRICS" / "TUNDRA ANALYTICS" + shared
    ///            undeclared postal_code                            -> cause B2
    ///   - v0/v1  "VELVET FOUNDRY" / "CASCADE ORBIT", nothing shared -> cause B3
    /// </summary>
    private static (IReadOnlyList<EntityRecord> Records, MatchingProfile Profile, IReadOnlyDictionary<string, string> GroundTruth) MixedCausesFixture()
    {
        var records = new List<EntityRecord>
        {
            Org("r0", "ACME TRADING LIMITED"),
            Org("r1", "ACME TRADING LIMITED"),
            Org("r2", "ACME TRADING LIMITED"),
            Org("r3", "ACME TRADING LIMITED"),

            Org("g0", "GLOBEX UNIQUE CORP"),
            Org("g1", "GLOBEX UNIQUE CORP"),

            Org("d0", "KAPPA VENTURES", ("address_line", "500 ELM ST")),
            Org("d1", "LYNX ENTERPRISES", ("address_line", "500 ELM ST")),

            Org("q0", "ORCHID METRICS", ("postal_code", "30003")),
            Org("q1", "TUNDRA ANALYTICS", ("postal_code", "30003")),

            Org("v0", "VELVET FOUNDRY"),
            Org("v1", "CASCADE ORBIT"),
        };
        var groundTruth = new Dictionary<string, string>
        {
            ["r0"] = "acme", ["r1"] = "acme",
            ["g0"] = "globex", ["g1"] = "globex",
            ["d0"] = "kappa-lynx", ["d1"] = "kappa-lynx",
            ["q0"] = "orchid-tundra", ["q1"] = "orchid-tundra",
            ["v0"] = "velvet-cascade", ["v1"] = "velvet-cascade",
        };
        return (records, NameAndAddressProfile(), groundTruth);
    }

    // ------------------------------------------------------------------------------------

    [Fact]
    public void PairSharingOnlySuppressedKeysIsCauseA()
    {
        // Two records identical enough to share a key, plus enough clones of that name that
        // the block exceeds maxBlockSize and the shared key is suppressed.
        var result = Diagnose(SuppressedSharedKeyFixture(), maxBlockSize: 2);
        Assert.Equal(1, result.CauseA.PairCount);
        Assert.Equal(0, result.CauseB1.PairCount + result.CauseB2.PairCount + result.CauseB3.PairCount);
    }

    [Fact]
    public void CauseADetailCountsPairsNotSharedKeys()
    {
        // r0/r1 ("ACME TRADING LIMITED" clones) share SIX distinct keys at block size 4:
        // fp:"acme trading" (1 fingerprint key), token:acme + token:trading (2 token keys), and
        // acr:atl + acr:at + acr:acme (3 acronym keys). Before the fix, CauseADetail counted one
        // increment PER SHARED KEY, so this single true pair produced
        // {(fingerprint,4):1, (token,4):2, (acronym,4):3} -- summing to 6 for ONE pair. The fix
        // dedupes to (strategy, blockSize) buckets the pair actually touches, so each of the
        // three buckets must read exactly 1, and none may exceed CauseA.PairCount (1) for this
        // single-true-pair fixture.
        var result = Diagnose(SuppressedSharedKeyFixture(), maxBlockSize: 2);

        Assert.Equal(1, result.CauseA.PairCount);
        Assert.Equal(3, result.CauseADetail.Count);
        Assert.All(result.CauseADetail, d => Assert.Equal(1, d.PairCount));
        Assert.All(result.CauseADetail, d => Assert.Equal(4, d.BlockSize));
        Assert.Equal(
            new[] { "acronym", "fingerprint", "token" },
            result.CauseADetail.Select(d => d.Strategy).OrderBy(s => s, StringComparer.Ordinal).ToList());
        // The un-deduped bug would have summed to 6; the fixed total across buckets is 3, and no
        // single bucket may exceed the fixture's one true pair.
        Assert.Equal(3, result.CauseADetail.Sum(d => d.PairCount));
    }

    [Fact]
    public void PairSharingAnUndeclaredColumnValueIsCauseB2()
    {
        // Names share nothing; postal_code is equal; the profile does not declare postal_code.
        var result = Diagnose(SharedPostcodeUndeclaredFixture(), maxBlockSize: null);
        Assert.Equal(1, result.CauseB2.PairCount);
        Assert.Equal(0, result.CauseB1.PairCount);
        Assert.Contains("postal_code", result.CauseB2.ByColumn.Keys);
    }

    [Fact]
    public void PairSharingNothingIsCauseB3()
    {
        var result = Diagnose(NoSharedValueFixture(), maxBlockSize: null);
        Assert.Equal(1, result.CauseB3.PairCount);
    }

    [Fact]
    public void B1TakesPrecedenceOverB2()
    {
        // Shares a value in a DECLARED-but-unkeyable field AND in an undeclared column.
        var result = Diagnose(DeclaredUnusableAndUndeclaredFixture(), maxBlockSize: null);
        Assert.Equal(1, result.CauseB1.PairCount);
        Assert.Equal(0, result.CauseB2.PairCount);
    }

    [Fact]
    public void NormalizationLossIsFlaggedOrthogonally()
    {
        // Raw names share the token "LIMITED"; suffix stripping removes it; no active shared key.
        var result = Diagnose(SuffixOnlySharedTokenFixture(), maxBlockSize: null);
        Assert.True(result.NormalizationImplicated.PairCount >= 1);
        Assert.True(result.NormalizationImplicated.LegalSuffixOnlyPairCount >= 1);
    }

    [Fact]
    public void ReconciliationHoldsOrTheRunFails()
    {
        var result = Diagnose(MixedCausesFixture(), maxBlockSize: 2);

        Assert.Equal(result.UnreachablePairs,
            result.CauseA.PairCount + result.CauseB1.PairCount
            + result.CauseB2.PairCount + result.CauseB3.PairCount);
        Assert.Equal(result.TruePairs, result.ReachablePairs + result.UnreachablePairs);

        // The fixture is deliberately non-trivial: one of each cause, plus one reachable pair.
        Assert.Equal(5, result.TruePairs);
        Assert.Equal(1, result.ReachablePairs);
        Assert.Equal(1, result.CauseA.PairCount);
        Assert.Equal(1, result.CauseB1.PairCount);
        Assert.Equal(1, result.CauseB2.PairCount);
        Assert.Equal(1, result.CauseB3.PairCount);
    }

    [Fact]
    public void ADeliberatelyInconsistentTallyThrows()
    {
        Assert.Throws<InvalidOperationException>(
            () => ReachabilityDiagnosticService.AssertReconciles(
                truePairs: 10, reachable: 4, unreachable: 5,   // 4 + 5 != 10
                a: 2, b1: 1, b2: 1, b3: 1));
    }

    // ------------------------------------------------------------------------------------
    // SharedKeysIgnoringSuppression had zero test coverage after Task 1. The cause-A tests
    // above exercise it indirectly (through the service); this test pins it directly, since
    // everything downstream (cause A detection) depends on its exact semantics: it must
    // return the keys BOTH records carry regardless of suppression, not just the active ones.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void SharedKeysIgnoringSuppressionReturnsSharedKeysEvenWhenAllAreSuppressed()
    {
        var (records, profile, _) = SuppressedSharedKeyFixture();
        var index = BlockingKeyIndex.Build(records, profile, MatchingDefaults.CreateRegistry());
        var suppressed = BlockingKeyIndex.SuppressedKeys(index, maxBlockSize: 2);

        // r0 = index 0, r1 = index 1 (declaration order in the fixture).
        var ignoringSuppression = BlockingKeyIndex.SharedKeysIgnoringSuppression(index.RecordKeys[0], index.RecordKeys[1]);
        var active = BlockingKeyIndex.SharedActiveKeys(index.RecordKeys[0], index.RecordKeys[1], suppressed);

        Assert.NotEmpty(ignoringSuppression);
        Assert.Empty(active);
        // Every key SharedKeysIgnoringSuppression reports for this pair must actually be
        // suppressed -- otherwise it would show up in SharedActiveKeys too.
        Assert.All(ignoringSuppression, keyId => Assert.True(suppressed[keyId]));
    }

    [Fact]
    public void SharedKeysIgnoringSuppressionIsUnaffectedByTheSuppressionThreshold()
    {
        // With no cap at all, the suppressed array is all-false, so ignoring suppression and
        // restricting to active keys must agree exactly: this is what makes the DIFFERENCE
        // between them (used to detect cause A) meaningful only when a cap is actually applied.
        var (records, profile, _) = SuppressedSharedKeyFixture();
        var index = BlockingKeyIndex.Build(records, profile, MatchingDefaults.CreateRegistry());
        var noSuppression = BlockingKeyIndex.SuppressedKeys(index, maxBlockSize: null);

        var ignoringSuppression = BlockingKeyIndex.SharedKeysIgnoringSuppression(index.RecordKeys[0], index.RecordKeys[1]);
        var active = BlockingKeyIndex.SharedActiveKeys(index.RecordKeys[0], index.RecordKeys[1], noSuppression);

        Assert.Equal(ignoringSuppression.OrderBy(x => x), active.OrderBy(x => x));
    }
}
