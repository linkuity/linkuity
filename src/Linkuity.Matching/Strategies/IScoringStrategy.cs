using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies;

public interface IScoringStrategy
{
    string Name { get; }

    /// <summary>Shape of the signals this strategy expects. Must match the similarity strategy's.</summary>
    SignalShape Consumes { get; }

    /// <summary>
    /// Scale of the score this strategy returns. Thresholds are validated against it, so a
    /// scorer that stops producing [0,1] must say so rather than silently reinterpreting
    /// every configured threshold.
    /// </summary>
    ScoreScale Scale { get; }
    ScoreResult Score(IReadOnlyList<SimilaritySignal> signals, MatchingProfile profile);
}
