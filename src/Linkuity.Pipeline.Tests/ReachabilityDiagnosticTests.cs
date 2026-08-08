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
    /// <summary>country is added to every record (Task 4): equal on every pair EXCEPT v0/v1,
    /// which deliberately differ (US/DE). Equal-country is safe to add to the A/B1/B2 pairs
    /// without changing their classification -- cause A short-circuits before any column
    /// equality check runs, and B1/B2 only need Count &gt; 0 on their OWN qualifying column, which
    /// is already true regardless of what else matches. v0/v1 must stay unequal, since an equal
    /// undeclared column there would reclassify the pair from B3 to B2.
    ///
    /// Record ids carry a "-17" suffix (chosen by brute-force search, not significant on its
    /// own) so that the control's StableHash(id)%n partner selection produces zero self-pairs and
    /// zero accidental true-pair collisions for this specific 12-record fixture -- i.e. the
    /// "typical" case the control walk is meant to hit most of the time. The dedicated
    /// AccidentalControlCollisionFixture below exercises the collision-counting path instead;
    /// this fixture stays clean so ControlSampleContainsNoTruePairs asserts something real rather
    /// than being satisfied by an accident of ordering.</summary>
    private static (IReadOnlyList<EntityRecord> Records, MatchingProfile Profile, IReadOnlyDictionary<string, string> GroundTruth) MixedCausesFixture()
    {
        var records = new List<EntityRecord>
        {
            Org("r0-17", "ACME TRADING LIMITED", ("country", "US")),
            Org("r1-17", "ACME TRADING LIMITED", ("country", "US")),
            Org("r2-17", "ACME TRADING LIMITED", ("country", "US")),
            Org("r3-17", "ACME TRADING LIMITED", ("country", "US")),

            Org("g0-17", "GLOBEX UNIQUE CORP", ("country", "US")),
            Org("g1-17", "GLOBEX UNIQUE CORP", ("country", "US")),

            Org("d0-17", "KAPPA VENTURES", ("address_line", "500 ELM ST"), ("country", "US")),
            Org("d1-17", "LYNX ENTERPRISES", ("address_line", "500 ELM ST"), ("country", "US")),

            Org("q0-17", "ORCHID METRICS", ("postal_code", "30003"), ("country", "US")),
            Org("q1-17", "TUNDRA ANALYTICS", ("postal_code", "30003"), ("country", "US")),

            Org("v0-17", "VELVET FOUNDRY", ("country", "US")),
            Org("v1-17", "CASCADE ORBIT", ("country", "DE")),
        };
        var groundTruth = new Dictionary<string, string>
        {
            ["r0-17"] = "acme", ["r1-17"] = "acme",
            ["g0-17"] = "globex", ["g1-17"] = "globex",
            ["d0-17"] = "kappa-lynx", ["d1-17"] = "kappa-lynx",
            ["q0-17"] = "orchid-tundra", ["q1-17"] = "orchid-tundra",
            ["v0-17"] = "velvet-cascade", ["v1-17"] = "velvet-cascade",
        };
        return (records, NameAndAddressProfile(), groundTruth);
    }

    /// <summary>Task 4: exercises the control walk's exclusion path directly, under hash-based
    /// partner selection. Record ids carry a "-11" suffix (found by brute-force search) so that
    /// StableHash(id) % 4 produces the permutation idx0-&gt;idx2, idx1-&gt;idx3, idx2-&gt;idx0,
    /// idx3-&gt;idx1 -- i.e. BOTH directions of the reciprocal pair (x0,x2) resolve to each other.
    /// x0/x2 are a ground-truthed true pair AND coincidentally share postal_code "11111"; because
    /// the hash pairing is reciprocal here, the collision is detected from BOTH x0's traversal
    /// and x2's traversal, so TruePairsAccidentallyIncluded is 2, not 1 -- this fixture also pins
    /// the documented "accept, don't dedupe" behaviour for reciprocal hits. x1/x3 are likewise a
    /// reciprocal pair, are NOT a true pair, and have distinct postal codes, so they contribute
    /// two clean (duplicate, by the same accept-not-dedupe rule) samples with no match. If the
    /// exclusion silently dropped rather than counted, or leaked x0/x2's postal_code into the
    /// aggregate, this test catches it.</summary>
    private static (IReadOnlyList<EntityRecord> Records, MatchingProfile Profile, IReadOnlyDictionary<string, string> GroundTruth) AccidentalControlCollisionFixture()
    {
        var records = new List<EntityRecord>
        {
            Org("x0-11", "SAME NAME ONE", ("postal_code", "11111")),
            Org("x1-11", "OTHER A", ("postal_code", "22222")),
            Org("x2-11", "SAME NAME ONE", ("postal_code", "11111")),
            Org("x3-11", "OTHER B", ("postal_code", "33333")),
        };
        var groundTruth = new Dictionary<string, string> { ["x0-11"] = "g1", ["x2-11"] = "g1" };
        return (records, NameOnlyProfile(), groundTruth);
    }

    /// <summary>Task 4: a column ("special_code") that co-occurs on the one unreachable true pair
    /// (m0/m2, equal "S1") but can NEVER co-occur in the control, by construction: m0 and m2 are
    /// the only two records carrying "S1", and they are exactly the ground-truthed true pair --
    /// so any control traversal that would land on the (m0,m2) pair is excluded as a true pair
    /// before it ever reaches the aggregate, regardless of which hash-derived partner each record
    /// gets. Control rate for special_code is therefore exactly 0, whatever partners m1/m3
    /// resolve to. Pins that Lift is null rather than +Infinity or a divide-by-zero exception in
    /// that case. (Incidentally, under StableHash, "m0" hashes to itself mod 4 in this record
    /// order -- a live self-pair case, also covered by SelfPartnerIsSkippedAndCounted below --
    /// which this test tolerates since it doesn't depend on m0 contributing a control sample.)
    /// m0's and m2's names ("VELVET FOUNDRY" / "CASCADE ORBIT") are the same pair already verified
    /// share-nothing in MixedCausesFixture, so this pair is genuinely unreachable (B2, via the
    /// shared undeclared special_code) rather than reachable by blocking.</summary>
    private static (IReadOnlyList<EntityRecord> Records, MatchingProfile Profile, IReadOnlyDictionary<string, string> GroundTruth) LiftIsNullFixture()
    {
        var records = new List<EntityRecord>
        {
            Org("m0", "VELVET FOUNDRY", ("special_code", "S1")),
            Org("m2", "CASCADE ORBIT", ("special_code", "S1")),
            Org("m1", "PRIMA FILLER", ("special_code", "S2")),
            Org("m3", "SECUNDA FILLER", ("special_code", "S3")),
        };
        var groundTruth = new Dictionary<string, string> { ["m0"] = "pair1", ["m2"] = "pair1" };
        return (records, NameOnlyProfile(), groundTruth);
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
    }

    [Fact]
    public void EveryTokenLostToSuffixStrippingIsALegalSuffixByConstruction()
    {
        // WHY THIS TEST EXISTS: NormalizationTally used to carry a LegalSuffixOnlyPairCount, and
        // the full-corpus run reported it at 100.00% (117,053/117,053) as if that were a finding.
        // It is a definition. OrganizationNameCanonicalizer.Core runs the SAME pipeline in both
        // modes and differs only by StripTrailingSuffixes, which pops only members of
        // LegalSuffixes -- so the difference between CanonicalizeKeepingSuffixes and Canonicalize
        // can contain nothing else. This test pins that invariant on the canonicalizer directly,
        // so if someone re-adds the sub-count they must first make this test fail.
        string[] names =
        [
            "ALPHA LIMITED", "BETA LIMITED", "ACME TRADING LIMITED", "ACME TRADING LTD",
            "THE BOREAL HOLDINGS SA", "ZENITH PLC", "GLOBEX SAB DE CV", "WAL-MART STORES INC",
            "AT & T CORP", "BANCO DE CHILE", "PROJECT 2000 GMBH", "SOLOWORD",
        ];
        var canonicalizer = new Linkuity.Matching.Canonicalization.OrganizationNameCanonicalizer();

        foreach (var name in names)
        {
            var kept = canonicalizer.CanonicalizeKeepingSuffixes(name).ToHashSet(StringComparer.Ordinal);
            var stripped = canonicalizer.Canonicalize(name).ToHashSet(StringComparer.Ordinal);
            var lost = kept.Except(stripped, StringComparer.Ordinal).ToList();

            Assert.All(lost, token => Assert.True(
                Linkuity.Matching.Canonicalization.OrganizationNameCanonicalizer.IsLegalSuffix(token),
                $"'{name}' lost non-suffix token '{token}' -- the two canonicalization modes now " +
                "differ by more than StripTrailingSuffixes, so a legal-suffix-only sub-count " +
                "would no longer be vacuous"));
        }
    }

    [Fact]
    public void DuplicateSourceRecordIdsFailTheRunRatherThanSilentlyDroppingARecord()
    {
        // bySource used to be `bySource[id] = i` -- last-wins, which drops the earlier record from
        // the pair walk with no counter moving. Every other skip in this service is counted or
        // asserted; this one was not.
        var records = new List<EntityRecord>
        {
            Org("dup", "ALPHA LIMITED"),
            Org("dup", "BETA LIMITED"),
        };
        var groundTruth = new Dictionary<string, string> { ["dup"] = "alpha-beta" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ReachabilityDiagnosticService(MatchingDefaults.CreateRegistry())
                .Diagnose(records, NameOnlyProfile(), groundTruth, maxBlockSize: null));
        Assert.Contains("duplicate SourceRecordId 'dup'", ex.Message);
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
    // Task 4: field co-occurrence and the non-pair control.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void ControlSampleIsDeterministic()
    {
        var a = Diagnose(MixedCausesFixture(), maxBlockSize: null).Control;
        var b = Diagnose(MixedCausesFixture(), maxBlockSize: null).Control;
        Assert.Equal(a.SampledPairCount, b.SampledPairCount);
        Assert.Equal(a.ByColumn["country"].SharedCount, b.ByColumn["country"].SharedCount);
    }

    [Fact]
    public void ControlSampleContainsNoTruePairs()
    {
        var result = Diagnose(MixedCausesFixture(), maxBlockSize: null);
        Assert.Equal(0, result.Control.TruePairsAccidentallyIncluded);
    }

    [Fact]
    public void RatesCarrySampleSizeAndInterval()
    {
        var result = Diagnose(MixedCausesFixture(), maxBlockSize: null);
        var country = result.Control.ByColumn["country"];
        Assert.True(country.SampleSize > 0);
        Assert.True(country.IntervalLow <= country.Rate && country.Rate <= country.IntervalHigh);
    }

    [Fact]
    public void LowCardinalityColumnsShowNoLiftOverTheControl()
    {
        // Not a correctness assertion about the engine -- it pins that lift is COMPUTED and
        // reported, so a reader cannot mistake a high co-occurrence rate for signal.
        var result = Diagnose(MixedCausesFixture(), maxBlockSize: null);
        Assert.True(result.Unreachable.ByColumn["country"].Lift.HasValue);
    }

    [Fact]
    public void ControlExcludesAccidentalTruePairsFromAggregatesAndCountsTheExclusion()
    {
        // Under hash-based partner selection (StableHash(id) % 4), x0/x2 and x1/x3 are BOTH
        // reciprocal pairs -- each side's traversal independently resolves to the other. x0/x2
        // is a ground-truthed true pair that also happens to share postal_code "11111", so BOTH
        // directions (x0's traversal and x2's traversal) detect and exclude it: the exclusion
        // count is 2, not 1. If the implementation silently dropped it instead of counting it, or
        // forgot to exclude its postal_code from the aggregate, this test would not catch a count
        // of 0 -- it specifically checks BOTH the counter AND that the excluded pair's values
        // never reached ByColumn. x1/x3 is a legitimate (also reciprocal, also double-counted per
        // the documented accept-don't-dedupe policy) control sample with distinct postal codes.
        var result = Diagnose(AccidentalControlCollisionFixture(), maxBlockSize: null);

        Assert.Equal(2, result.Control.TruePairsAccidentallyIncluded);
        Assert.Equal(2, result.Control.SampledPairCount);
        Assert.Equal(0, result.Control.SelfPairsSkipped);

        var postalCode = result.Control.ByColumn["postal_code"];
        Assert.Equal(2, postalCode.SampleSize);
        Assert.Equal(0, postalCode.SharedCount);
    }

    [Fact]
    public void LiftIsNullWhenControlRateIsZero()
    {
        // special_code co-occurs on the one unreachable true pair, but m0 and m2 -- the only two
        // records carrying it -- are exactly the ground-truthed true pair, so no control
        // traversal can ever land on them without being excluded first. The control's own rate
        // for that column is exactly 0 by construction. Division by the control rate would be a
        // divide-by-zero; Lift must be null instead.
        var result = Diagnose(LiftIsNullFixture(), maxBlockSize: null);

        var controlSpecialCode = result.Control.ByColumn["special_code"];
        Assert.Equal(0, controlSpecialCode.SharedCount);
        Assert.True(controlSpecialCode.SampleSize > 0);
        Assert.Equal(0.0, controlSpecialCode.Rate);

        var unreachableSpecialCode = result.Unreachable.ByColumn["special_code"];
        Assert.True(unreachableSpecialCode.SampleSize > 0);
        Assert.False(unreachableSpecialCode.Lift.HasValue);
    }

    [Fact]
    public void SelfPartnerIsSkippedAndCounted()
    {
        // "m0" is the first record in LiftIsNullFixture's 4-record list, and StableHash("m0") % 4
        // happens to equal 0 -- its own index. A self-referential partner must be skipped (it is
        // not a control pair; a record cannot be its own non-pair) AND counted, not silently
        // `continue`d. This is the same fixture LiftIsNullWhenControlRateIsZero uses, so that
        // test's "SampleSize > 0 despite one record contributing nothing" is this test's cause.
        var result = Diagnose(LiftIsNullFixture(), maxBlockSize: null);
        Assert.True(result.Control.SelfPairsSkipped >= 1);
    }

    [Fact]
    public void ControlAccountsForEveryIndexAsSampledExcludedOrSelfPaired()
    {
        // Every record index takes exactly one of three paths in the control walk: sampled,
        // excluded as an accidental true pair, or skipped as a self-partner. If any path
        // silently dropped a record instead of counting it, this sum would fall short of the
        // record count. Checked against three different fixtures (with, respectively, zero,
        // two, and at least one of the three outcomes triggered) so the identity is not merely
        // true by construction of one fixture's shape.
        foreach (var fixture in new[] { MixedCausesFixture(), AccidentalControlCollisionFixture(), LiftIsNullFixture() })
        {
            var result = Diagnose(fixture, maxBlockSize: null);
            Assert.Equal(fixture.Records.Count,
                result.Control.SampledPairCount + result.Control.TruePairsAccidentallyIncluded + result.Control.SelfPairsSkipped);
        }
    }

    [Fact]
    public void WilsonIntervalStaysInRangeAtRateZero()
    {
        var (low, high) = ReachabilityDiagnosticService.WilsonInterval(successes: 0, sampleSize: 20);
        Assert.True(low >= 0.0);
        Assert.True(high <= 1.0);
        Assert.Equal(0.0, low, precision: 10);
    }

    [Fact]
    public void WilsonIntervalStaysInRangeAtRateOne()
    {
        var (low, high) = ReachabilityDiagnosticService.WilsonInterval(successes: 20, sampleSize: 20);
        Assert.True(low >= 0.0);
        Assert.True(high <= 1.0);
        Assert.Equal(1.0, high, precision: 10);
    }

    [Fact]
    public void WilsonIntervalHandlesZeroSampleSizeWithoutDividingByZero()
    {
        var (low, high) = ReachabilityDiagnosticService.WilsonInterval(successes: 0, sampleSize: 0);
        Assert.Equal(0.0, low);
        Assert.Equal(1.0, high);
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
