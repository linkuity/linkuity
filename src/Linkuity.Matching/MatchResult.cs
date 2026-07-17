using Linkuity.Core.Models;
using Linkuity.Matching.Strategies;

namespace Linkuity.Matching;

public sealed record ScoredCandidate(EntityRecord Record, double Score, IReadOnlyList<ScoreContribution> Breakdown);

/// <summary>
/// The engine's decision for one record against a corpus: the top score, the
/// three-band decision, the scored candidate list (>= review threshold, ordered
/// by score descending), and the score breakdown for the top candidate.
/// </summary>
public sealed record MatchResult(
    double FinalScore,
    MatchDecision Decision,
    IReadOnlyList<ScoredCandidate> Candidates,
    IReadOnlyList<ScoreContribution> Breakdown);
