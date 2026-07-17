using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Computes the raw signals the current durable Score consumes: shared
/// blocking-key count, shared-exact identifier flags, and token-Jaccard overlap.
/// The combination into a final score lives in <see cref="DefaultScoringStrategy"/>.
/// </summary>
public sealed class DefaultSimilarityStrategy : ISimilarityStrategy
{
    internal static readonly string[] ExactFields = ["email", "phone", "domain_name", "date_of_birth"];

    public string Name => "default";

    public IReadOnlyList<SimilaritySignal> Evaluate(EntityRecord left, EntityRecord right, MatchingProfile profile)
    {
        var signals = new List<SimilaritySignal>();

        var sharedKeys = left.BlockingKeys.Intersect(right.BlockingKeys, StringComparer.OrdinalIgnoreCase).Count();
        signals.Add(new SimilaritySignal("shared-blocking-keys", sharedKeys));

        foreach (var field in ExactFields)
        {
            if (MatchKey.SharedExact(left.Fields, right.Fields, field))
                signals.Add(new SimilaritySignal($"exact:{field}", 1.0));
        }

        signals.Add(new SimilaritySignal("token-jaccard", MatchKey.TokenSimilarity(left.Fields, right.Fields)));
        return signals;
    }
}
