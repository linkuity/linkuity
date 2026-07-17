using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Field-level similarity: for each Matchable profile field present and non-blank
/// on both records, selects the field's evaluator (ProfileField.SimilarityEvaluator,
/// defaulting to "exact") and emits one SimilaritySignal named after the field.
/// Fields whose evaluator reports non-comparable (null) are skipped so the weighted
/// scorer does not penalize them. Pairs with <see cref="WeightedScoringStrategy"/>.
/// </summary>
public sealed class WeightedFieldSimilarityStrategy : ISimilarityStrategy
{
    private const string DefaultEvaluator = "exact";
    private readonly IReadOnlyDictionary<string, ISimilarityEvaluator> _evaluators;

    public WeightedFieldSimilarityStrategy(IEnumerable<ISimilarityEvaluator> evaluators)
    {
        var map = new Dictionary<string, ISimilarityEvaluator>(StringComparer.Ordinal);
        foreach (var evaluator in evaluators)
            map.TryAdd(evaluator.Name, evaluator);
        _evaluators = map;
    }

    public string Name => "field-weighted";

    public IReadOnlyList<SimilaritySignal> Evaluate(EntityRecord left, EntityRecord right, MatchingProfile profile)
    {
        var signals = new List<SimilaritySignal>();
        foreach (var field in profile.Fields)
        {
            if (!field.Roles.HasFlag(FieldRole.Matchable))
                continue;
            if (!left.Fields.TryGetValue(field.Name, out var leftValue) || string.IsNullOrWhiteSpace(leftValue))
                continue;
            if (!right.Fields.TryGetValue(field.Name, out var rightValue) || string.IsNullOrWhiteSpace(rightValue))
                continue;

            var evaluatorName = field.SimilarityEvaluator ?? DefaultEvaluator;
            if (!_evaluators.TryGetValue(evaluatorName, out var evaluator))
                throw new KeyNotFoundException($"No similarity evaluator named '{evaluatorName}' is registered (field '{field.Name}').");

            var value = evaluator.Evaluate(leftValue, rightValue, field);
            if (value is null)
                continue;
            signals.Add(new SimilaritySignal(field.Name, value.Value));
        }
        return signals;
    }
}
