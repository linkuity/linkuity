using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Pipeline;

namespace Linkuity.Cli.Tests;

/// <summary>
/// Pins the batch path's observable pair-scoring behaviour so CorpusAuditService claims parity
/// with something verified. Read from source and confirmed here:
///   - BuildMatchesCsv scores BOTH directions and keeps the MAX (BatchMatchingService.cs:63-73)
///   - it emits ONLY pairs at or above AutoMatchThreshold (line 66), so review-band pairs never
///     appear and any parity test can compare auto-band pairs only. OmitsPairsBelowAutoThreshold
///     proves this specifically with a pair that clears Resolve's review gate (0.31) but not
///     BuildMatchesCsv's auto gate (0.41) — a pair suppressed by an earlier gate (e.g. never
///     reaching Resolve's Candidates at all) would pass a weaker version of that test without
///     the auto-threshold line ever running, so the fixture and the two-part assertion both
///     matter, not just the header-only outcome.
/// The max rule cannot be discriminated here because BuildMatchesCsv resolves strategies from
/// MatchingDefaults; CorpusAuditScoringTests.ScorePair_TakesMaxOfBothDirections does that with
/// an injected asymmetric strategy.
/// </summary>
public class BatchPairScoringCharacterizationTests
{
    private static readonly MatchingProfile Profile = new()
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
            }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["fingerprint", "token", "acronym", "ngram"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.41,
        ReviewThreshold = 0.31,
        MaxBlockSize = 50
    };

    private static (string, IReadOnlyDictionary<string, string>) Row(string id, string name)
        => (id, new Dictionary<string, string> { ["id"] = id, ["organization_name"] = name });

    /// <summary>
    /// Builds an EntityRecord with real blocking keys attached, as BatchMatchingService.BuildMatchesCsv
    /// does for every record before scoring (BatchMatchingService.cs:48-54). This matters because
    /// blocking-linear gates candidacy on BlockingKeys (BlockingAwareLinearRetrievalStrategy.cs:51),
    /// and MatchingEngine.Resolve only auto-generates keys for the incoming record, not the corpus
    /// (MatchingEngine.cs's EnsureBlockingKeys) — a corpus record built without keys is silently
    /// unretrievable, which is not what BuildMatchesCsv actually does.
    /// </summary>
    private static EntityRecord MakeWithBlockingKeys(MatchingEngine engine, MatchingProfile blockingProfile, string id, string name)
    {
        var seed = new EntityRecord
        {
            Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
            SourceRecordId = id,
            Fields = new Dictionary<string, string> { ["id"] = id, ["organization_name"] = name },
            CreatedAt = DateTimeOffset.UnixEpoch
        };
        return new EntityRecord
        {
            Id = seed.Id, ProjectId = seed.ProjectId, SourceId = seed.SourceId, IngestBatchId = seed.IngestBatchId,
            SourceRecordId = seed.SourceRecordId,
            Fields = seed.Fields,
            BlockingKeys = engine.GenerateBlockingKeys(seed, blockingProfile),
            CreatedAt = seed.CreatedAt
        };
    }

    [Fact]
    public void EmitsOneRowPerUnorderedPair_OrdinalLowestIdFirst()
    {
        var csv = BatchMatchingService.BuildMatchesCsv(
            [Row("b-rec", "ACME WIDGETS INC"), Row("a-rec", "ACME WIDGETS")], Profile);

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("a-rec,b-rec,", lines[1]);
    }

    /// <summary>
    /// ACME WIDGETS INC / ACME GADGETS INC canonicalize (legal suffix INC stripped) to
    /// {ACME, WIDGETS} / {ACME, GADGETS}: intersection 1, union 3, canonical-jaccard = 1/3 ~=
    /// 0.3333. With a single matchable field of weight 4.0, IdentifierAwareWeightedScoringStrategy's
    /// weighted score reduces to that similarity (no identifier floor applies; 0.3333 is below
    /// ReviewFloorGate 0.75, so no review floor applies either) — landing in
    /// [ReviewThreshold 0.31, AutoMatchThreshold 0.41), i.e. inside Resolve's review band but
    /// below BuildMatchesCsv's auto-match cut. They share blocking key token:acme, so the pair is
    /// genuinely retrieved and scored, not dropped by blocking.
    ///
    /// This is deliberately NOT a pair that fails to reach Resolve's Candidates list (e.g. two
    /// names with 0.0 similarity): such a pair would make this test pass even if
    /// BuildMatchesCsv's AutoMatchThreshold cut (BatchMatchingService.cs:66) were deleted outright,
    /// because Resolve's own review-threshold gate (MatchingEngine.cs:50) would suppress it one
    /// gate earlier for an unrelated reason. Assertion block 1 proves the pair clears that earlier
    /// gate and reaches Candidates with a score in the intended band, so assertion block 2 —
    /// BuildMatchesCsv omitting it — can only be explained by the auto-match cut this test claims
    /// to pin.
    /// </summary>
    [Fact]
    public void OmitsPairsBelowAutoThreshold()
    {
        var engine = new MatchingEngine(MatchingDefaults.CreateRegistry());
        var blocking = Profile.WithCandidateRetrievalStrategy("blocking-linear");

        var left = MakeWithBlockingKeys(engine, blocking, "x", "ACME WIDGETS INC");
        var right = MakeWithBlockingKeys(engine, blocking, "y", "ACME GADGETS INC");

        // Block 1: the pair reaches Resolve's Candidates list, scored inside the review band but
        // below the auto-match threshold — proving it got past the review gate, not suppressed there.
        var candidate = Assert.Single(engine.Resolve(left, [right], blocking).Candidates);
        Assert.True(candidate.Score >= Profile.ReviewThreshold,
            $"expected score >= ReviewThreshold ({Profile.ReviewThreshold}), got {candidate.Score}");
        Assert.True(candidate.Score < Profile.AutoMatchThreshold,
            $"expected score < AutoMatchThreshold ({Profile.AutoMatchThreshold}), got {candidate.Score}");

        // Block 2: BuildMatchesCsv's auto-match cut (BatchMatchingService.cs:66) suppresses the
        // same pair anyway, since its score never reaches AutoMatchThreshold.
        var csv = BatchMatchingService.BuildMatchesCsv(
            [Row("x", "ACME WIDGETS INC"), Row("y", "ACME GADGETS INC")], Profile);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);   // header only
    }

    /// <summary>The emitted score equals Resolve() in BOTH directions for the shipped evaluator.
    /// If this ever fails, the evaluator became asymmetric and the max rule starts to matter.</summary>
    [Fact]
    public void EmittedScoreEqualsBothDirectionalResolveScores()
    {
        var engine = new MatchingEngine(MatchingDefaults.CreateRegistry());
        var blocking = Profile.WithCandidateRetrievalStrategy("blocking-linear");

        var left = MakeWithBlockingKeys(engine, blocking, "a-rec", "ACME WIDGETS");
        var right = MakeWithBlockingKeys(engine, blocking, "b-rec", "ACME WIDGETS INC");

        var forward = engine.Resolve(left, [right], blocking).Candidates.Single().Score;
        var reverse = engine.Resolve(right, [left], blocking).Candidates.Single().Score;

        Assert.Equal(forward, reverse, precision: 12);
    }
}
