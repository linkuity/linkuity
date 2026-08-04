namespace Linkuity.Matching.Strategies;

/// <summary>One line of the score breakdown: a signal and its weighted contribution.
/// The outcome is carried so an explanation can say WHY a field contributed nothing —
/// "address: right record has none" rather than the field vanishing from the breakdown.</summary>
public sealed record ScoreContribution(
    string Signal,
    double Value,
    double Weight,
    double Contribution,
    ComparisonOutcome Outcome = ComparisonOutcome.Compared);

/// <summary>The scorer's output: a final score plus an explainable per-signal breakdown.</summary>
public sealed record ScoreResult(double FinalScore, IReadOnlyList<ScoreContribution> Breakdown);
