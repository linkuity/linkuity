using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Field-level similarity: emits one SimilaritySignal per Matchable profile field, always —
/// never skipped. When both records have the field non-blank, the field's evaluator
/// (ProfileField.SimilarityEvaluator, defaulting to "exact") runs and the signal carries the
/// evaluator's value with outcome Compared, or NotComparable (value 0) if the evaluator
/// declines to judge. When the field is blank on one or both sides, the signal carries value 0
/// with outcome MissingOneSide or MissingBoth. The scorer decides what each outcome means;
/// this strategy only reports which one occurred. Pairs with <see cref="WeightedScoringStrategy"/>.
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

    public SignalShape Produces => SignalShape.PerField;

    public IReadOnlyList<SimilaritySignal> Evaluate(EntityRecord left, EntityRecord right, MatchingProfile profile)
    {
        var signals = new List<SimilaritySignal>();
        foreach (var field in profile.Fields)
        {
            if (!field.Roles.HasFlag(FieldRole.Matchable))
                continue;

            var hasLeft = left.Fields.TryGetValue(field.Name, out var leftValue) && !string.IsNullOrWhiteSpace(leftValue);
            var hasRight = right.Fields.TryGetValue(field.Name, out var rightValue) && !string.IsNullOrWhiteSpace(rightValue);

            if (!hasLeft || !hasRight)
            {
                signals.Add(new SimilaritySignal(field.Name, 0,
                    hasLeft || hasRight ? ComparisonOutcome.MissingOneSide : ComparisonOutcome.MissingBoth));
                continue;
            }

            var evaluatorName = field.SimilarityEvaluator ?? DefaultEvaluator;
            if (!_evaluators.TryGetValue(evaluatorName, out var evaluator))
                throw new KeyNotFoundException($"No similarity evaluator named '{evaluatorName}' is registered (field '{field.Name}').");

            var value = evaluator.Evaluate(leftValue!, rightValue!, field);
            signals.Add(value is null
                ? new SimilaritySignal(field.Name, 0, ComparisonOutcome.NotComparable)
                : new SimilaritySignal(field.Name, value.Value));
        }
        return signals;
    }
}
