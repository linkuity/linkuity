using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Linkuity.Matching.Profiles;

/// <summary>
/// A stable short hash of everything in a profile that can change a score or a band.
///
/// Recording a profile's name alongside a stored score is not enough to attribute it: "person"
/// names a family of configurations, not one, and the thresholds or weights inside it can be
/// edited without the name changing. Two edges written a month apart can carry the same profile
/// name and have been produced by materially different rules, and nothing in the store would
/// say so. The fingerprint is what makes "were these scored the same way?" answerable.
///
/// It deliberately covers the whole profile rather than a chosen subset. Guessing which settings
/// are score-relevant is how a fingerprint comes to certify a sameness that does not hold —
/// blocking strategy changes which pairs are ever compared, normalization changes the values
/// compared, and both alter outcomes without touching a threshold.
/// </summary>
public static class ProfileFingerprint
{
    /// <summary>
    /// Hex, truncated to 16 characters. Long enough that an accidental collision between the
    /// handful of profiles one deployment uses is not a practical concern, short enough to sit
    /// in a log line or a table column without dominating it. This is a change detector, not a
    /// security boundary.
    /// </summary>
    public static string Of(MatchingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var canonical = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        canonical.Append("content=").Append(profile.ContentType).Append('\n');
        canonical.Append("normalization=").Append(profile.NormalizationStrategy).Append('\n');
        // Blocking strategy order does not change the union of keys, so it is sorted: two
        // profiles differing only in the order they list strategies behave identically and
        // should not read as different rules.
        canonical.Append("blocking=")
            .Append(string.Join(",", profile.BlockingStrategies.OrderBy(b => b, StringComparer.Ordinal)))
            .Append('\n');
        canonical.Append("retrieval=").Append(profile.CandidateRetrievalStrategy).Append('\n');
        canonical.Append("similarity=").Append(profile.SimilarityStrategy).Append('\n');
        canonical.Append("scoring=").Append(profile.ScoringStrategy).Append('\n');
        canonical.Append("decision=").Append(profile.DecisionStrategy).Append('\n');
        canonical.Append("clustering=").Append(profile.ClusteringStrategy).Append('\n');
        canonical.Append("auto=").Append(profile.AutoMatchThreshold.ToString("R", inv)).Append('\n');
        canonical.Append("review=").Append(profile.ReviewThreshold.ToString("R", inv)).Append('\n');
        canonical.Append("reviewFloorGate=").Append(profile.ReviewFloorGate.ToString("R", inv)).Append('\n');
        canonical.Append("identifierFloorGate=").Append(profile.IdentifierFloorGate.ToString("R", inv)).Append('\n');
        canonical.Append("maxBlockSize=")
            .Append(profile.MaxBlockSize?.ToString(inv) ?? "none").Append('\n');
        canonical.Append("phoneRegion=").Append(profile.DefaultPhoneRegion).Append('\n');

        // Every key below is appended ONLY when the value is non-null/non-empty. A profile that
        // declares none of these five stage-1a settings must produce the EXACT byte sequence it
        // produced before they existed — see ProfileFingerprintTests — because the fingerprint is
        // stored provenance on the frozen baseline's existing edges, and an appended "none"/"off"
        // sentinel would change every one of those fingerprints for a setting that never applied
        // to them.
        if (profile.MinClusterCohesion is { } minClusterCohesion)
            canonical.Append("minClusterCohesion=").Append(minClusterCohesion.ToString("R", inv)).Append('\n');

        if (profile.MaxAutoClusterSize is { } maxAutoClusterSize)
            canonical.Append("maxAutoClusterSize=").Append(maxAutoClusterSize.ToString(inv)).Append('\n');

        if (profile.PlaceholderValues.Count > 0)
        {
            // Sorted for the same reason BlockingStrategies is above: nothing today reads this
            // list order-sensitively (stage 2 reserves it for rarity weighting), so two profiles
            // differing only in declaration order must fingerprint alike.
            canonical.Append("placeholderValues=")
                .Append(string.Join(",", profile.PlaceholderValues.OrderBy(v => v, StringComparer.Ordinal)))
                .Append('\n');
        }

        // Fields are sorted by name: declaration order does not affect scoring, so two profiles
        // that differ only in field order are the same rules and must fingerprint alike.
        foreach (var field in profile.Fields.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            canonical.Append("field=").Append(field.Name)
                .Append('|').Append(field.SemanticType)
                .Append('|').Append(field.Roles)
                .Append('|').Append(field.SimilarityEvaluator ?? "none")
                .Append('|').Append(field.Weight.ToString("R", inv));

            if (field.EvaluatorOptions is { Count: > 0 })
            {
                foreach (var (key, value) in field.EvaluatorOptions.OrderBy(o => o.Key, StringComparer.Ordinal))
                    canonical.Append('|').Append(key).Append('=').Append(value);
            }

            // AliasGroup/Evidence, same non-null-only rule as the profile-level settings above:
            // a field with neither must fingerprint identically to one from before this branch.
            if (field.AliasGroup is not null)
                canonical.Append('|').Append("alias=").Append(field.AliasGroup);

            if (field.Evidence is { } evidence)
            {
                canonical.Append('|').Append("evidence=")
                    .Append(evidence.SameEntityAgreement.ToString("R", inv)).Append(',')
                    .Append(evidence.ChanceAgreement.ToString("R", inv)).Append(',')
                    .Append(evidence.MaxAgreementBits?.ToString("R", inv) ?? "none");
            }

            canonical.Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
