using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Reproduces FileMetadataStore.Score exactly: 0 when no shared blocking key,
/// 0.98 on any shared exact identifier, otherwise max(0.80, token-Jaccard).
/// Milestone 13 replaces this with weighted, per-field explainable scoring.
/// </summary>
public sealed class DefaultScoringStrategy : IScoringStrategy
{
    private const double ExactScore = 0.98;
    private const double TokenFloor = 0.80;

    public string Name => "default";

    public ScoreResult Score(IReadOnlyList<SimilaritySignal> signals, MatchingProfile profile)
    {
        var sharedKeys = Signal(signals, "shared-blocking-keys");
        if (sharedKeys <= 0)
            return new ScoreResult(0, [new ScoreContribution("shared-blocking-keys", 0, 1, 0)]);

        var exact = signals.FirstOrDefault(s => s.Name.StartsWith("exact:", StringComparison.Ordinal) && s.Value == 1.0);
        if (exact is not null)
            return new ScoreResult(ExactScore, [new ScoreContribution(exact.Name, exact.Value, 1, ExactScore)]);

        var token = Signal(signals, "token-jaccard");
        var final = Math.Max(TokenFloor, token);
        return new ScoreResult(final, [new ScoreContribution("token-jaccard", token, 1, final)]);
    }

    private static double Signal(IReadOnlyList<SimilaritySignal> signals, string name)
        => signals.FirstOrDefault(s => s.Name == name)?.Value ?? 0;
}
