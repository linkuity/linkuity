namespace Linkuity.Matching.Strategies;

/// <summary>One line of the score breakdown: a signal and its weighted contribution.</summary>
public sealed record ScoreContribution(string Signal, double Value, double Weight, double Contribution);

/// <summary>The scorer's output: a final score plus an explainable per-signal breakdown.</summary>
public sealed record ScoreResult(double FinalScore, IReadOnlyList<ScoreContribution> Breakdown);
