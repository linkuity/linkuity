using Linkuity.Core.Models;

namespace Linkuity.Matching.Profiles;

/// <summary>
/// Builds a <see cref="MatchingProfile"/> from a run's <see cref="MatchConfiguration"/>
/// for the one-shot batch matcher. Field roles, evaluators, and weights are copied from
/// the built-in profile of the same content type (single source of truth), remapped onto
/// the config's actual column names, and filtered to fields that participate in matching.
/// Retrieval is overridden to the blocking-gated strategy the durable path uses.
/// </summary>
public static class MatchConfigurationProfileFactory
{
    // The identifier-weighted scorer's review floor is only sound when each scored
    // candidate already shares a blocking key, so batch retrieval must be blocking-gated.
    private const string BatchRetrievalStrategy = "blocking-linear";

    public static MatchingProfile Create(MatchConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var baseProfile = BaseProfileFor(configuration.ContentType);
        var templateBySemanticType = baseProfile.Fields
            .Where(f => f.Roles != FieldRole.None)
            .GroupBy(f => f.SemanticType)
            .ToDictionary(g => g.Key, g => g.First());

        var fields = new List<ProfileField>();
        foreach (var field in configuration.Fields)
        {
            if (!field.ParticipatesInMatching) continue;
            if (field.SemanticType == SemanticFieldType.SourceIdentifier) continue;

            fields.Add(templateBySemanticType.TryGetValue(field.SemanticType, out var template)
                ? new ProfileField
                {
                    Name = field.Name,
                    SemanticType = field.SemanticType,
                    Roles = template.Roles,
                    SimilarityEvaluator = template.SimilarityEvaluator,
                    Weight = template.Weight,
                    EvaluatorOptions = template.EvaluatorOptions
                }
                : new ProfileField
                {
                    Name = field.Name,
                    SemanticType = field.SemanticType,
                    Roles = FieldRole.Searchable | FieldRole.Matchable,
                    SimilarityEvaluator = "exact",
                    Weight = 1.0
                });
        }

        if (fields.Count == 0)
            throw new ArgumentException(
                "MatchConfiguration has no fields that participate in matching.", nameof(configuration));

        return new MatchingProfile
        {
            ContentType = baseProfile.ContentType,
            Fields = fields,
            NormalizationStrategy = baseProfile.NormalizationStrategy,
            BlockingStrategies = baseProfile.BlockingStrategies,
            CandidateRetrievalStrategy = BatchRetrievalStrategy,
            SimilarityStrategy = baseProfile.SimilarityStrategy,
            ScoringStrategy = baseProfile.ScoringStrategy,
            DecisionStrategy = baseProfile.DecisionStrategy,
            ClusteringStrategy = baseProfile.ClusteringStrategy,
            AutoMatchThreshold = baseProfile.AutoMatchThreshold,
            ReviewThreshold = baseProfile.ReviewThreshold,
            ReviewFloorGate = baseProfile.ReviewFloorGate
        };
    }

    private static MatchingProfile BaseProfileFor(string contentType)
    {
        var provider = new DefaultMatchingProfileProvider(DefaultMatchingProfileProvider.BuiltInProfiles());
        return provider.GetProfile(contentType);
    }
}
