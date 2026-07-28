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
///     appear and any parity test can compare auto-band pairs only.
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
    /// ALPHAONE / BRAVOTWO share the ngram key "ngram:one"? No — they share nothing but the
    /// suffix INC, which canonicalization strips before token/fingerprint keys are built. The
    /// ngram strategy DOES key raw tokens, so "ngram:inc" bridges them. Their canonical-jaccard
    /// is 0.0, far below 0.41, so the pair must not be emitted.
    /// </summary>
    [Fact]
    public void OmitsPairsBelowAutoThreshold()
    {
        var csv = BatchMatchingService.BuildMatchesCsv(
            [Row("x", "ALPHAONE INC"), Row("y", "BRAVOTWO INC")], Profile);

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
        var now = DateTimeOffset.UnixEpoch;

        EntityRecord Make(string id, string name)
        {
            var seed = new EntityRecord
            {
                Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
                SourceRecordId = id,
                Fields = new Dictionary<string, string> { ["id"] = id, ["organization_name"] = name },
                CreatedAt = now
            };
            // blocking-linear gates candidacy on BlockingKeys (BlockingAwareLinearRetrievalStrategy.cs:51);
            // Resolve() only auto-generates keys for the incoming record (MatchingEngine.cs's
            // EnsureBlockingKeys), not for corpus records, so corpus records must carry keys up front —
            // exactly as BatchMatchingService.BuildMatchesCsv does for every record before scoring.
            return new EntityRecord
            {
                Id = seed.Id, ProjectId = seed.ProjectId, SourceId = seed.SourceId, IngestBatchId = seed.IngestBatchId,
                SourceRecordId = seed.SourceRecordId,
                Fields = seed.Fields,
                BlockingKeys = engine.GenerateBlockingKeys(seed, blocking),
                CreatedAt = seed.CreatedAt
            };
        }

        var left = Make("a-rec", "ACME WIDGETS");
        var right = Make("b-rec", "ACME WIDGETS INC");

        var forward = engine.Resolve(left, [right], blocking).Candidates.Single().Score;
        var reverse = engine.Resolve(right, [left], blocking).Candidates.Single().Score;

        Assert.Equal(forward, reverse, precision: 12);
    }
}
