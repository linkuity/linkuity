using System.Text.Json;
using Linkuity.Core.Models;
using Linkuity.Core.Normalization;
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

    /// <summary>
    /// Refuses a profile whose resolved scoring strategy produces scores on a scale live
    /// matching cannot carry yet. <see cref="Strategies.IDecisionStrategy.Decide"/>, and the
    /// threshold construction inside <c>IncrementalResolver</c> and <c>BatchMatchingService</c>,
    /// all build <see cref="MatchThresholds"/> on the default <see cref="ScoreScale.UnitInterval"/>
    /// with no way for a caller to supply a different one — giving them that way is an interface
    /// change (<c>IDecisionStrategy.Decide</c> does not receive the registry a scale would come
    /// from) reserved for a later stage, not this fix. Before "evidence" (<see cref="ScoreScale.LogOdds"/>)
    /// was registered, naming it in a profile failed at config time with "unknown scoring
    /// strategy". This restores that same clean, config-time failure for the paths that would
    /// otherwise crash on the first scored pair with a bare <see cref="ArgumentOutOfRangeException"/>.
    /// <para>
    /// Deliberately NOT called from <see cref="LoadFromJson"/>/<see cref="LoadFromFile"/>/
    /// <see cref="LoadFromDirectory"/> themselves: <c>CorpusAuditService</c> already threads the
    /// resolved scale through its own scoring (see its <c>BandOf</c>) and explicitly lists
    /// "evidence" as a scoring strategy it supports, measuring it is the whole point of stage 1a.
    /// Folding this check into the loader unconditionally would make that measurement path unable
    /// to load the very profiles it exists to measure. Call this only where a loaded profile is
    /// about to be handed to live matching (durable ingest, local batch matching) — see its call
    /// sites in <c>MatchingServiceCollectionExtensions</c> and <c>LocalBatchRunner</c>.
    /// </para>
    /// </summary>
    public static void RequireLiveMatchingScale(MatchingProfile profile, IStrategyRegistry registry, string source)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(registry);

        var scale = registry.Scoring[profile.ScoringStrategy].Scale;
        if (scale == ScoreScale.UnitInterval)
            return;

        throw new MatchingProfileConfigException(
            $"Matching profile '{source}' uses scoringStrategy '{profile.ScoringStrategy}', which produces " +
            $"scores on the {scale} scale. Live matching (durable incremental ingest, local batch matching, " +
            $"and threshold decisions) always classifies bands on the {ScoreScale.UnitInterval} scale and has " +
            $"no way yet to carry a different one, so autoMatchThreshold ({profile.AutoMatchThreshold}) / " +
            $"reviewThreshold ({profile.ReviewThreshold}) would throw at the first scored pair instead of " +
            "classifying anything. This profile can still be loaded for corpus-audit measurement, which does " +
            "carry its own scale; it cannot yet be used for live matching.");
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

        // Members of one alias group must be priced identically: they are the same fact, so
        // differing parameters mean the score depends on which spelling a source happened to use.
        foreach (var group in fields.Where(f => f.AliasGroup is not null).GroupBy(f => f.AliasGroup!, StringComparer.Ordinal))
        {
            var distinct = group.Select(f => f.Evidence).Distinct().Count();
            if (distinct > 1)
                throw new MatchingProfileConfigException(
                    $"Matching profile '{source}' alias group '{group.Key}' has fields with different " +
                    $"evidence parameters ({string.Join(", ", group.Select(f => f.Name))}). Members of an " +
                    "alias group are the same fact and must be priced the same.");
        }

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

        // A similarity strategy that emits whole-record aggregates and a scorer that expects one
        // signal per field will happily run together and produce a number. The scorer finds no
        // field named "shared-blocking-keys", weighs nothing, and returns a score unrelated to
        // the records. Rejecting the pairing here is the only place it can be caught before it
        // reaches a merge decision.
        var produces = registry.Similarity[similarity].Produces;
        var consumes = registry.Scoring[scoring].Consumes;
        if (produces != consumes)
            throw new MatchingProfileConfigException(
                $"Matching profile '{source}' pairs similarityStrategy '{similarity}' (emits {produces} signals) " +
                $"with scoringStrategy '{scoring}' (expects {consumes} signals). They cannot be combined: the " +
                "scorer would consume signals it cannot interpret and return a meaningless score.");

        var auto = RequireDouble(document.AutoMatchThreshold, "autoMatchThreshold", source);
        var review = RequireDouble(document.ReviewThreshold, "reviewThreshold", source);

        // Thresholds are validated against the scale the chosen scorer actually produces, not
        // against an assumed [0,1]. MatchThresholds owns those rules, including the requirement
        // that autoMatchThreshold be strictly greater than reviewThreshold.
        var scale = registry.Scoring[scoring].Scale;
        try
        {
            _ = new MatchThresholds(auto, review, scale);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            // Name the JSON keys, not MatchThresholds' parameter names: the reader has to edit
            // 'autoMatchThreshold' in a file, and 'autoMatch' does not appear there.
            throw new MatchingProfileConfigException(
                $"Matching profile '{source}' has invalid autoMatchThreshold ({auto}) / reviewThreshold ({review}) " +
                $"for scoringStrategy '{scoring}' on the {scale} scale. {ReasonOf(ex)}");
        }

        // The memo's acceptance criterion, enforced by the system rather than remembered by us:
        // a single rare descriptive agreement must not on its own exceed the merge threshold.
        // Load-time rather than a test, because a test only protects the profiles we ship.
        foreach (var evidenceField in fields.Where(f => f.Evidence is not null))
        {
            var cap = evidenceField.Evidence!.MaxAgreementBits;

            if (cap is null && !evidenceField.Roles.HasFlag(FieldRole.Identifier))
                throw new MatchingProfileConfigException(
                    $"Matching profile '{source}' field '{evidenceField.Name}' declares evidence with no " +
                    "maxAgreementBits. An uncapped field can carry a merge on its own, which is only " +
                    "appropriate for a verified identifier — declare the identifier role, or set a cap.");

            if (cap is { } bits && bits >= auto)
                throw new MatchingProfileConfigException(
                    $"Matching profile '{source}' field '{evidenceField.Name}' has maxAgreementBits {bits}, " +
                    $"which is not below autoMatchThreshold {auto}. A single agreement on one " +
                    "descriptive field would reach the auto-merge band unaided.");
        }

        // Optional (absent -> 0.75, preserving Milestone 27's default). Range-validated only; no
        // constraint relative to reviewThreshold (a free tuning knob — below reviewThreshold it
        // promotes strongly-evidenced sub-threshold pairs into review).
        var reviewFloorGate = document.ReviewFloorGate ?? 0.75;
        RequireRange(reviewFloorGate, "reviewFloorGate", source);

        // Optional corroboration gate for the identifier floor (absent -> 0.35, the measured
        // default). Range-validated only; it is deliberately unconstrained relative to the
        // thresholds — 0 restores the unconditional floor, higher values demand more
        // corroboration before a matching identifier may promote a pair to auto.
        var identifierFloorGate = document.IdentifierFloorGate ?? 0.35;
        RequireRange(identifierFloorGate, "identifierFloorGate", source);

        // Optional suppression threshold (2b). Absent -> path defaults (indexed: MaxCandidates;
        // linear: no suppression). Validated only for a sane lower bound.
        if (document.MaxBlockSize is { } maxBlockSize && maxBlockSize < 1)
            throw new MatchingProfileConfigException(
                $"Matching profile '{source}' value 'maxBlockSize' ({maxBlockSize}) must be at least 1.");

        // Region used to read phone numbers written without a country code. Validated as a shape
        // (two letters) rather than against a region list: libphonenumber owns that list, and an
        // unknown-but-well-formed code fails closed — the number stays un-normalized rather than
        // being assigned to the wrong country.
        // Parsed strictly rather than defaulted on a typo: silently reading a DayFirst feed as
        // MonthFirst mislabels every date before the thirteenth of a month and reports nothing.
        var dateOrder = DateFieldOrder.MonthFirst;
        if (document.DefaultDateOrder is { } orderText
            && !Enum.TryParse(orderText, ignoreCase: true, out dateOrder))
            throw new MatchingProfileConfigException(
                $"Matching profile '{source}' value 'defaultDateOrder' ('{orderText}') must be " +
                $"'{nameof(DateFieldOrder.MonthFirst)}' or '{nameof(DateFieldOrder.DayFirst)}'.");

        var phoneRegion = document.DefaultPhoneRegion ?? FieldNormalizer.DefaultPhoneRegion;
        if (phoneRegion.Length != 2 || !phoneRegion.All(char.IsAsciiLetterUpper))
            throw new MatchingProfileConfigException(
                $"Matching profile '{source}' value 'defaultPhoneRegion' ('{phoneRegion}') must be a " +
                "two-letter uppercase ISO 3166-1 region code, for example 'US' or 'GB'.");

        // Optional cohesion floor. Absent -> null, off (stage 1a ships with nothing moving; see
        // MatchingProfile.MinClusterCohesion). Validated only when present — no default is
        // substituted, mirroring maxAutoClusterSize below. It is a rate, so unlike
        // reviewFloorGate/identifierFloorGate it must additionally reject NaN/Infinity:
        // RequireRange's < / > comparisons let NaN through silently (every comparison against
        // NaN is false).
        if (document.MinClusterCohesion is { } minClusterCohesion &&
            (double.IsNaN(minClusterCohesion) || double.IsInfinity(minClusterCohesion) || minClusterCohesion is < 0.0 or > 1.0))
            throw new MatchingProfileConfigException(
                $"Matching profile '{source}' value 'minClusterCohesion' ({minClusterCohesion}) must be a finite number in [0, 1].");

        // Optional cluster-size backstop (absent -> null, off). A cluster cannot be smaller than
        // 2 records, so a limit below that would block every merge rather than bound the large ones.
        if (document.MaxAutoClusterSize is { } maxAutoClusterSize && maxAutoClusterSize < 2)
            throw new MatchingProfileConfigException(
                $"Matching profile '{source}' value 'maxAutoClusterSize' ({maxAutoClusterSize}) must be at least 2.");

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
            ReviewFloorGate = reviewFloorGate,
            IdentifierFloorGate = identifierFloorGate,
            MaxBlockSize = document.MaxBlockSize,
            DefaultPhoneRegion = phoneRegion,
            DefaultDateOrder = dateOrder,
            PlaceholderValues = document.PlaceholderValues ?? [],
            MinClusterCohesion = document.MinClusterCohesion,
            MaxAutoClusterSize = document.MaxAutoClusterSize
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

        // Weights divide the weighted average, so a negative or non-finite one does not merely
        // skew the score, it breaks the arithmetic: a negative weight can push a score above 1
        // and a NaN weight makes every comparison against a threshold false, landing the pair
        // silently in no-match. Zero is rejected too — a field that contributes nothing should
        // be removed from the profile, not weighted out of it, so the profile keeps saying what
        // it means.
        var weight = field.Weight ?? 1.0;
        if (double.IsNaN(weight) || double.IsInfinity(weight) || weight <= 0)
            throw new MatchingProfileConfigException(
                $"Matching profile '{source}' field '{name}' has weight {weight}; it must be a finite number " +
                "greater than zero. Remove the field rather than giving it zero weight.");

        FieldEvidence? evidence = null;
        if (field.Evidence is { } e)
        {
            if (e.SameEntityAgreement is not { } m || e.ChanceAgreement is not { } u)
                throw new MatchingProfileConfigException(
                    $"Matching profile '{source}' field '{name}' declares evidence but is missing " +
                    "sameEntityAgreement or chanceAgreement; both are required.");
            try
            {
                evidence = new FieldEvidence
                {
                    SameEntityAgreement = m,
                    ChanceAgreement = u,
                    MaxAgreementBits = e.MaxAgreementBits
                };
                _ = evidence.AgreementBits;   // forces the lazy m > u check at LOAD time
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                throw new MatchingProfileConfigException(
                    $"Matching profile '{source}' field '{name}' has invalid evidence: {ReasonOf(ex)}");
            }
        }

        return new ProfileField
        {
            Name = name,
            SemanticType = semanticType,
            Roles = roles,
            SimilarityEvaluator = field.SimilarityEvaluator,
            Weight = weight,
            EvaluatorOptions = field.EvaluatorOptions,
            Evidence = evidence,
            AliasGroup = field.AliasGroup
        };
    }

    /// <summary>
    /// The explanatory half of an argument exception, without the trailing "(Parameter 'x')"
    /// the runtime appends — that parameter name is an implementation detail here, and the
    /// caller is editing JSON keys rather than calling a constructor.
    /// </summary>
    private static string ReasonOf(Exception ex)
    {
        var message = ex.Message;
        var marker = message.IndexOf(" (Parameter", StringComparison.Ordinal);
        return marker < 0 ? message : message[..marker];
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
