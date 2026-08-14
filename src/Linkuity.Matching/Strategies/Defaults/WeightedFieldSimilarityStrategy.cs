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
/// <para>
/// A field named by a <see cref="ProfileComparison"/> is the exception: it produces no signal of
/// its own, and the comparison produces one signal for the whole group. That is what makes the
/// comparison's "one fact, one contribution" rule true rather than aspirational — emitting both
/// would count the fact twice, which is the defect comparisons exist to remove.
/// </para>
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

        var consumedByComparison = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var comparison in profile.Comparisons)
            foreach (var member in comparison.Fields)
                consumedByComparison.Add(member);

        foreach (var field in profile.Fields)
        {
            if (!field.Roles.HasFlag(FieldRole.Matchable))
                continue;
            if (consumedByComparison.Contains(field.Name))
                continue;

            var (hasLeft, hasRight, similarity) = Compare(field, left, right);

            if (!hasLeft || !hasRight)
            {
                signals.Add(new SimilaritySignal(field.Name, 0,
                    hasLeft || hasRight ? ComparisonOutcome.MissingOneSide : ComparisonOutcome.MissingBoth));
                continue;
            }

            signals.Add(similarity is null
                ? new SimilaritySignal(field.Name, 0, ComparisonOutcome.NotComparable)
                : new SimilaritySignal(field.Name, similarity.Value));
        }

        var byName = profile.Fields.ToDictionary(f => f.Name, f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var comparison in profile.Comparisons)
            signals.Add(EvaluateComparison(comparison, byName, left, right));

        return signals;
    }

    private SimilaritySignal EvaluateComparison(
        ProfileComparison comparison,
        IReadOnlyDictionary<string, ProfileField> byName,
        EntityRecord left,
        EntityRecord right)
    {
        var similarities = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        bool anyLeft = false, anyRight = false, anyBoth = false;

        foreach (var member in comparison.Fields)
        {
            if (!byName.TryGetValue(member, out var field))
                throw new KeyNotFoundException(
                    $"Comparison '{comparison.Name}' names field '{member}', which the profile does not declare.");

            var (hasLeft, hasRight, similarity) = Compare(field, left, right);
            anyLeft |= hasLeft;
            anyRight |= hasRight;

            if (hasLeft && hasRight && similarity is not null)
            {
                anyBoth = true;
                similarities[member] = similarity.Value;
            }
        }

        // Nothing on both sides means nothing was compared. A pair cannot be said to disagree on
        // an address neither record carries, so the ladder is not walked at all — walking it would
        // land every such pair in the bottom level and charge it that level's negative evidence.
        if (!anyBoth)
        {
            return new SimilaritySignal(comparison.Name, 0,
                anyLeft || anyRight ? ComparisonOutcome.MissingOneSide : ComparisonOutcome.MissingBoth);
        }

        for (var i = 0; i < comparison.Levels.Count; i++)
        {
            var level = comparison.Levels[i];
            var met = level.Requirements.All(r =>
                similarities.TryGetValue(r.Field, out var s) && s >= r.MinSimilarity);

            if (!met)
                continue;

            // Value ranks the level rather than measuring anything: 1.0 for the most specific
            // level down to 0.0 for the catch-all. The scorer prices from the level's own m and u
            // and never reads this; it exists so a score breakdown shows how specific the match
            // was without the reader having to know the ladder's shape.
            var rank = comparison.Levels.Count == 1
                ? 1.0
                : (comparison.Levels.Count - 1 - i) / (double)(comparison.Levels.Count - 1);

            return new SimilaritySignal(comparison.Name, rank, ComparisonOutcome.Compared, level.Name);
        }

        // Unreachable: the loader requires the last level to carry no requirements, so it always
        // matches. Kept as a throw rather than a silent zero because a comparison that fell off
        // the end of its own ladder is a profile the loader should have rejected.
        throw new InvalidOperationException(
            $"Comparison '{comparison.Name}' matched no level. Its last level must have no requirements.");
    }

    private (bool HasLeft, bool HasRight, double? Similarity) Compare(
        ProfileField field, EntityRecord left, EntityRecord right)
    {
        var hasLeft = left.Fields.TryGetValue(field.Name, out var leftValue) && !field.IsAbsent(leftValue);
        var hasRight = right.Fields.TryGetValue(field.Name, out var rightValue) && !field.IsAbsent(rightValue);
        if (!hasLeft || !hasRight)
            return (hasLeft, hasRight, null);

        var evaluatorName = field.SimilarityEvaluator ?? DefaultEvaluator;
        if (!_evaluators.TryGetValue(evaluatorName, out var evaluator))
            throw new KeyNotFoundException($"No similarity evaluator named '{evaluatorName}' is registered (field '{field.Name}').");

        return (true, true, evaluator.Evaluate(leftValue!, rightValue!, field));
    }
}
