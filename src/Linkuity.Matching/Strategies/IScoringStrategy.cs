using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies;

public interface IScoringStrategy
{
    string Name { get; }
    ScoreResult Score(IReadOnlyList<SimilaritySignal> signals, MatchingProfile profile);
}
