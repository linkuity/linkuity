using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies;

/// <summary>
/// A field-level similarity evaluator: compares two field values and returns a
/// similarity in [0,1], or <c>null</c> when the values are not comparable (for
/// example an unparseable numeric or date), so the weighted scorer can skip the
/// field rather than penalize it. Selected per field via
/// <see cref="ProfileField.SimilarityEvaluator"/>.
/// </summary>
public interface ISimilarityEvaluator
{
    string Name { get; }
    double? Evaluate(string left, string right, ProfileField field);
}
