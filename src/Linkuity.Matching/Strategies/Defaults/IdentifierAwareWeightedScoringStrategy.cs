using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Identifier-aware weighted scoring. Reproduces the durable matcher's decision
/// structure while adding weighted, explainable similarity:
/// <list type="bullet">
/// <item>a field declared <see cref="Profiles.FieldRole.Identifier"/> matching at 1.0 floors the
/// score to the auto band (0.98) — but only when the weighted per-field similarity independently
/// reaches the profile's identifier corroboration gate (<c>IdentifierFloorGate</c>, default 0.35).
/// A shared exact identifier is strong MDM evidence even when other fields differ across sources,
/// yet many profiles mark fields that are NOT unique to one entity (date of birth, a shared
/// switchboard phone) as identifiers; the gate stops such a coincidence from fusing two records
/// that agree on nothing else. An identifier match promotes a plausible pair to auto; it does not
/// rescue an implausible one. Below the gate the identifier floor does not apply and the pair
/// falls through to the review-floor logic;</item>
/// <item>otherwise, when the weighted per-field similarity reaches the profile's review-floor gate
/// (<c>ReviewFloorGate</c>, default 0.75), the score is max(0.80 review floor, weighted average); below the gate
/// a shared blocking key alone does NOT reach the review band — the raw weighted score stands
/// (typically a NoMatch), which keeps common blocking keys from flooding the review queue;</item>
/// <item>no comparable signals -> 0.</item>
/// </list>
/// The two floors are mutually exclusive and the identifier floor is tried first, so a pair that
/// clears both gates scores 0.98, never 0.80.
/// MUST be paired with a blocking-gated retrieval (blocking-linear / lucene): the
/// review floor assumes every scored candidate already shares a blocking key. The
/// breakdown carries the per-field weighted contributions; when a floor applies
/// (a corroborated identifier match, or weighted similarity clearing the review-floor gate) the
/// final score is max(floor, weighted), so the contributions explain the similarity even when
/// a floor sets the final band — but when no floor applies the raw weighted score stands as the
/// final score.
/// </summary>
public sealed class IdentifierAwareWeightedScoringStrategy : IScoringStrategy
{
    private const double IdentifierFloor = 0.98;
    private const double ReviewFloor = 0.80;

    public string Name => "identifier-weighted";

    public SignalShape Consumes => SignalShape.PerField;
    public ScoreScale Scale => ScoreScale.UnitInterval;

    public ScoreResult Score(IReadOnlyList<SimilaritySignal> signals, MatchingProfile profile)
    {
        if (signals.Count == 0)
            return new ScoreResult(0, []);

        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        var identifierFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in profile.Fields)
        {
            weights[field.Name] = field.Weight;
            if (field.Roles.HasFlag(FieldRole.Identifier))
                identifierFields.Add(field.Name);
        }

        double totalWeight = 0;
        foreach (var signal in signals)
            totalWeight += WeightFor(weights, signal.Name);
        if (totalWeight <= 0)
            return new ScoreResult(0, []);

        var breakdown = new List<ScoreContribution>(signals.Count);
        double weighted = 0;
        var identifierMatched = false;
        foreach (var signal in signals)
        {
            var weight = WeightFor(weights, signal.Name);
            var contribution = weight * signal.Value / totalWeight;
            weighted += contribution;
            breakdown.Add(new ScoreContribution(signal.Name, signal.Value, weight, contribution));
            if (signal.Value >= 1.0 && identifierFields.Contains(signal.Name))
                identifierMatched = true;
        }

        // Corroboration gate: an identifier match promotes a PLAUSIBLE pair to auto, it does not
        // rescue an implausible one. Without the gate any single Identifier field at 1.0 floored
        // the pair to 0.98 consulting nothing else — sound for a value unique to one entity
        // (email, GTIN), wrong for date_of_birth and phone, which many profiles mark Identifier.
        // A blocked pair falls through to the ordinary review-floor logic below.
        if (identifierMatched && weighted >= profile.IdentifierFloorGate)
            return new ScoreResult(Math.Max(IdentifierFloor, weighted), breakdown);

        // Gate the review floor on real similarity: a shared blocking key alone (weighted below the
        // gate) is NOT promoted into the review band; the pair keeps its raw weighted score, which
        // falls below the review threshold and becomes a NoMatch.
        if (weighted >= profile.ReviewFloorGate)
            return new ScoreResult(Math.Max(ReviewFloor, weighted), breakdown);

        return new ScoreResult(weighted, breakdown);
    }

    private static double WeightFor(IReadOnlyDictionary<string, double> weights, string name)
        => weights.TryGetValue(name, out var weight) ? weight : 1.0;
}
