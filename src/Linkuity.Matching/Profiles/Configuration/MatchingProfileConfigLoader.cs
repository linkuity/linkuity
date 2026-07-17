using System.Text.Json;
using Linkuity.Core.Models;
using Linkuity.Matching.Strategies;

namespace Linkuity.Matching.Profiles.Configuration;

/// <summary>
/// Loads a <see cref="MatchingProfile"/> from JSON configuration and validates
/// every strategy / evaluator / semantic-type / role name against a live
/// <see cref="IStrategyRegistry"/>. Authoring a new content-type profile is
/// therefore a pure data change: no engine code is touched.
/// </summary>
public sealed class MatchingProfileConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public MatchingProfile LoadFromJson(string json, IStrategyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(registry);

        MatchingProfileDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<MatchingProfileDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MatchingProfileConfigException($"Matching profile JSON is not valid: {ex.Message}", ex);
        }

        if (document is null)
            throw new MatchingProfileConfigException("Matching profile JSON deserialized to null.");

        return Build(document, registry, source: "<json>");
    }

    public MatchingProfile LoadFromFile(string path, IStrategyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(registry);
        if (!File.Exists(path))
            throw new MatchingProfileConfigException($"Matching profile file not found: '{path}'.");

        var json = File.ReadAllText(path);
        var source = Path.GetFileName(path);

        MatchingProfileDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<MatchingProfileDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MatchingProfileConfigException($"Matching profile '{source}' JSON is not valid: {ex.Message}", ex);
        }

        if (document is null)
            throw new MatchingProfileConfigException($"Matching profile '{source}' JSON deserialized to null.");

        return Build(document, registry, source);
    }

    public IReadOnlyList<MatchingProfile> LoadFromDirectory(string directory, IStrategyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(registry);
        if (!Directory.Exists(directory))
            throw new MatchingProfileConfigException($"Matching profile directory not found: '{directory}'.");

        return Directory.EnumerateFiles(directory, "*.profile.json")
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .Select(p => LoadFromFile(p, registry))
            .ToList();
    }

    private static MatchingProfile Build(MatchingProfileDocument document, IStrategyRegistry registry, string source)
    {
        var contentType = Require(document.ContentType, "contentType", source);

        if (document.Fields is null || document.Fields.Count == 0)
            throw new MatchingProfileConfigException($"Matching profile '{source}' must declare at least one field.");

        var fields = document.Fields.Select(f => BuildField(f, registry, source)).ToList();

        var duplicate = fields
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new MatchingProfileConfigException($"Matching profile '{source}' declares field '{duplicate.Key}' more than once.");

        var normalization = Require(document.NormalizationStrategy, "normalizationStrategy", source);
        RequireRegistered(registry.Normalization, normalization, "normalization strategy", source);

        var blocking = RequireList(document.BlockingStrategies, "blockingStrategies", source);
        foreach (var name in blocking)
            RequireRegistered(registry.Blocking, name, "blocking strategy", source);

        var retrieval = Require(document.CandidateRetrievalStrategy, "candidateRetrievalStrategy", source);
        RequireRegistered(registry.CandidateRetrieval, retrieval, "candidate-retrieval strategy", source);

        var similarity = Require(document.SimilarityStrategy, "similarityStrategy", source);
        RequireRegistered(registry.Similarity, similarity, "similarity strategy", source);

        var scoring = Require(document.ScoringStrategy, "scoringStrategy", source);
        RequireRegistered(registry.Scoring, scoring, "scoring strategy", source);

        var decision = Require(document.DecisionStrategy, "decisionStrategy", source);
        RequireRegistered(registry.Decision, decision, "decision strategy", source);

        var clustering = Require(document.ClusteringStrategy, "clusteringStrategy", source);
        RequireRegistered(registry.Clustering, clustering, "clustering strategy", source);

        var auto = RequireDouble(document.AutoMatchThreshold, "autoMatchThreshold", source);
        var review = RequireDouble(document.ReviewThreshold, "reviewThreshold", source);
        RequireRange(auto, "autoMatchThreshold", source);
        RequireRange(review, "reviewThreshold", source);
        // The durable store requires autoMatchThreshold strictly greater than
        // reviewThreshold; reject the equal boundary here so it fails at load time.
        if (auto <= review)
            throw new MatchingProfileConfigException(
                $"Matching profile '{source}' has autoMatchThreshold ({auto}) not greater than reviewThreshold ({review}).");

        // Optional (absent -> 0.75, preserving Milestone 27's default). Range-validated only; no
        // constraint relative to reviewThreshold (a free tuning knob — below reviewThreshold it
        // promotes strongly-evidenced sub-threshold pairs into review).
        var reviewFloorGate = document.ReviewFloorGate ?? 0.75;
        RequireRange(reviewFloorGate, "reviewFloorGate", source);

        return new MatchingProfile
        {
            ContentType = contentType,
            Fields = fields,
            NormalizationStrategy = normalization,
            BlockingStrategies = blocking,
            CandidateRetrievalStrategy = retrieval,
            SimilarityStrategy = similarity,
            ScoringStrategy = scoring,
            DecisionStrategy = decision,
            ClusteringStrategy = clustering,
            AutoMatchThreshold = auto,
            ReviewThreshold = review,
            ReviewFloorGate = reviewFloorGate
        };
    }

    private static ProfileField BuildField(MatchingProfileFieldDocument field, IStrategyRegistry registry, string source)
    {
        var name = Require(field.Name, "field.name", source);

        if (!Enum.TryParse<SemanticFieldType>(field.SemanticType, ignoreCase: true, out var semanticType))
            throw new MatchingProfileConfigException(
                $"Matching profile '{source}' field '{name}' has unknown semanticType '{field.SemanticType}'.");

        var roles = FieldRole.None;
        foreach (var role in field.Roles ?? [])
        {
            if (!Enum.TryParse<FieldRole>(role, ignoreCase: true, out var parsed))
                throw new MatchingProfileConfigException(
                    $"Matching profile '{source}' field '{name}' has unknown role '{role}'.");
            roles |= parsed;
        }

        if (field.SimilarityEvaluator is not null)
            RequireRegistered(registry.Evaluators, field.SimilarityEvaluator, "similarity evaluator", source);

        return new ProfileField
        {
            Name = name,
            SemanticType = semanticType,
            Roles = roles,
            SimilarityEvaluator = field.SimilarityEvaluator,
            Weight = field.Weight ?? 1.0,
            EvaluatorOptions = field.EvaluatorOptions
        };
    }

    private static void RequireRegistered<T>(IReadOnlyDictionary<string, T> registered, string name, string kind, string source)
    {
        if (!registered.ContainsKey(name))
            throw new MatchingProfileConfigException(
                $"Matching profile '{source}' references unknown {kind} '{name}'. Registered: {string.Join(", ", registered.Keys.Order(StringComparer.Ordinal))}.");
    }

    private static void RequireRange(double value, string field, string source)
    {
        if (value is < 0.0 or > 1.0)
            throw new MatchingProfileConfigException($"Matching profile '{source}' value '{field}' ({value}) must be in [0, 1].");
    }

    private static string Require(string? value, string field, string source)
        => string.IsNullOrWhiteSpace(value)
            ? throw new MatchingProfileConfigException($"Matching profile '{source}' is missing required value '{field}'.")
            : value;

    private static IReadOnlyList<string> RequireList(List<string>? value, string field, string source)
        => value is null || value.Count == 0
            ? throw new MatchingProfileConfigException($"Matching profile '{source}' is missing required list '{field}'.")
            : value;

    private static double RequireDouble(double? value, string field, string source)
        => value ?? throw new MatchingProfileConfigException($"Matching profile '{source}' is missing required value '{field}'.");
}
