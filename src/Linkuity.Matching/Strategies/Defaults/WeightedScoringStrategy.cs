using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Per-field weighted, explainable scoring. The final score is the weight-normalized
/// average of the per-field similarity signals (weights come from the matching
/// profile's fields, defaulting to 1.0). Each signal becomes one ScoreContribution
/// whose Contribution is weight·value / Σweight, so the breakdown's contributions
/// sum to the final score. Pairs with <see cref="WeightedFieldSimilarityStrategy"/>.
/// </summary>
public sealed class WeightedScoringStrategy : IScoringStrategy
{
    public string Name => "weighted";

    public SignalShape Consumes => SignalShape.PerField;
    public ScoreScale Scale => ScoreScale.UnitInterval;

    public ScoreResult Score(IReadOnlyList<SimilaritySignal> signals, MatchingProfile profile)
    {
        // This scorer predates ComparisonOutcome and was written against "one signal per field
        // that was actually compared" — the similarity strategy now emits one per matchable
        // field regardless. Filtering to Compared here reproduces the old input shape exactly,
        // which is what keeps the frozen corpus baseline's arithmetic unchanged.
        signals = [.. signals.Where(s => s.Outcome == ComparisonOutcome.Compared)];

        if (signals.Count == 0)
            return new ScoreResult(0, []);

        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var field in profile.Fields)
            weights[field.Name] = field.Weight;

        double totalWeight = 0;
        foreach (var signal in signals)
            totalWeight += WeightFor(weights, signal.Name);
        if (totalWeight <= 0)
            return new ScoreResult(0, []);

        var breakdown = new List<ScoreContribution>(signals.Count);
        double finalScore = 0;
        foreach (var signal in signals)
        {
            var weight = WeightFor(weights, signal.Name);
            var contribution = weight * signal.Value / totalWeight;
            finalScore += contribution;
            breakdown.Add(new ScoreContribution(signal.Name, signal.Value, weight, contribution));
        }

        return new ScoreResult(finalScore, breakdown);
    }

    private static double WeightFor(IReadOnlyDictionary<string, double> weights, string name)
        => weights.TryGetValue(name, out var weight) ? weight : 1.0;
}
