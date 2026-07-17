using Linkuity.Core.Models;

namespace Linkuity.Matching.Profiles;

public sealed class DefaultMatchingProfileProvider : IMatchingProfileProvider
{
    private readonly IReadOnlyDictionary<string, MatchingProfile> _profiles;

    public DefaultMatchingProfileProvider(IEnumerable<MatchingProfile> profiles)
        : this(profiles, loaded: [])
    {
    }

    public DefaultMatchingProfileProvider(
        IEnumerable<MatchingProfile> builtIns,
        IEnumerable<MatchingProfile> loaded)
    {
        var map = new Dictionary<string, MatchingProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in builtIns)
        {
            if (!map.TryAdd(profile.ContentType, profile))
                throw new ArgumentException($"Duplicate built-in matching profile for content type: '{profile.ContentType}'.");
        }

        // A loaded profile silently overrides a built-in of the same content type;
        // two loaded profiles for the same content type is an authoring error.
        var loadedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in loaded)
        {
            if (!loadedSeen.Add(profile.ContentType))
                throw new ArgumentException($"Two loaded matching profiles declare content type: '{profile.ContentType}'.");
            map[profile.ContentType] = profile;
        }

        _profiles = map;
    }

    public MatchingProfile GetProfile(string contentType)
        => _profiles.TryGetValue(contentType, out var profile)
            ? profile
            : throw new KeyNotFoundException(
                $"No matching profile registered for content type '{contentType}'. " +
                $"Registered: {string.Join(", ", _profiles.Keys.Order(StringComparer.Ordinal))}.");

    public bool TryGetProfile(string contentType, out MatchingProfile? profile)
        => _profiles.TryGetValue(contentType, out profile);

    /// <summary>
    /// The default person profile. Maps each matchable field to a similarity
    /// evaluator and a weight, and selects the field-weighted similarity strategy
    /// plus the weighted, explainable scorer. Blocking, normalization, retrieval,
    /// clustering, and thresholds are unchanged from earlier milestones.
    /// </summary>
    /// <remarks>
    /// The <c>identifier-weighted</c> scorer applies a 0.80 review floor to every
    /// scored candidate, which is sound only when retrieval is blocking-gated so that
    /// each scored candidate already shares a blocking key. The durable ingest path
    /// always overrides retrieval with <c>blocking-linear</c>/<c>lucene</c> (see
    /// <c>FileMetadataStore</c>), so the <c>linear</c> default here is never reached in
    /// production. Callers that resolve this profile directly should set
    /// <see cref="MatchingProfile.CandidateRetrievalStrategy"/> to a blocking-gated
    /// strategy; pairing the floor scorer with ungated <c>linear</c> retrieval would
    /// floor every comparable corpus record into the review band.
    /// </remarks>
    public static MatchingProfile CreatePersonProfile() => new()
    {
        ContentType = "person",
        Fields =
        [
            new ProfileField { Name = "first_name", SemanticType = SemanticFieldType.FirstName, Roles = FieldRole.Searchable | FieldRole.Matchable, SimilarityEvaluator = "fuzzy", Weight = 1.0 },
            new ProfileField { Name = "last_name", SemanticType = SemanticFieldType.LastName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking, SimilarityEvaluator = "fuzzy", Weight = 2.0 },
            new ProfileField { Name = "full_name", SemanticType = SemanticFieldType.FullName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking, SimilarityEvaluator = "fuzzy", Weight = 1.5 },
            new ProfileField { Name = "name", SemanticType = SemanticFieldType.FullName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking, SimilarityEvaluator = "fuzzy", Weight = 1.5 },
            new ProfileField { Name = "email", SemanticType = SemanticFieldType.Email, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking | FieldRole.Identifier, SimilarityEvaluator = "exact", Weight = 3.0 },
            new ProfileField { Name = "phone", SemanticType = SemanticFieldType.Phone, Roles = FieldRole.Matchable | FieldRole.Blocking | FieldRole.Identifier, SimilarityEvaluator = "exact", Weight = 3.0 },
            new ProfileField { Name = "date_of_birth", SemanticType = SemanticFieldType.DateOfBirth, Roles = FieldRole.Matchable | FieldRole.Blocking | FieldRole.Identifier, SimilarityEvaluator = "date", Weight = 2.0 },
            new ProfileField { Name = "domain_name", SemanticType = SemanticFieldType.DomainName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking | FieldRole.Identifier, SimilarityEvaluator = "exact", Weight = 1.5 },
            new ProfileField { Name = "organization_name", SemanticType = SemanticFieldType.OrganizationName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking, SimilarityEvaluator = "fuzzy", Weight = 1.5 },
            new ProfileField { Name = "address_line", SemanticType = SemanticFieldType.AddressLine, Roles = FieldRole.Searchable | FieldRole.Matchable, SimilarityEvaluator = "jaccard", Weight = 1.0 },
            new ProfileField { Name = "postal_code", SemanticType = SemanticFieldType.PostalCode, Roles = FieldRole.Matchable, SimilarityEvaluator = "exact", Weight = 1.0 }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["exact-value", "token-name"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.90,
        ReviewThreshold = 0.75
    };

    /// <summary>
    /// The default organization profile: a faithful C# rendering of the canonical
    /// organization configuration. Mirrors the person durable strategy selections
    /// (identity normalization, field-weighted similarity, identifier-weighted
    /// scoring, exact-value + token-name blocking, 0.90/0.75 thresholds). Domain,
    /// email, and phone are the strong identifiers; <c>source</c> is a non-matching
    /// source identifier.
    /// </summary>
    public static MatchingProfile CreateOrganizationProfile() => new()
    {
        ContentType = "organization",
        Fields =
        [
            new ProfileField { Name = "source", SemanticType = SemanticFieldType.SourceIdentifier, Roles = FieldRole.None },
            new ProfileField { Name = "organization_name", SemanticType = SemanticFieldType.OrganizationName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking, SimilarityEvaluator = "fuzzy", Weight = 2.0 },
            new ProfileField { Name = "domain_name", SemanticType = SemanticFieldType.DomainName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking | FieldRole.Identifier, SimilarityEvaluator = "exact", Weight = 2.5 },
            new ProfileField { Name = "email", SemanticType = SemanticFieldType.Email, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking | FieldRole.Identifier, SimilarityEvaluator = "exact", Weight = 2.5 },
            new ProfileField { Name = "phone", SemanticType = SemanticFieldType.Phone, Roles = FieldRole.Matchable | FieldRole.Blocking | FieldRole.Identifier, SimilarityEvaluator = "exact", Weight = 2.0 },
            new ProfileField { Name = "address_line", SemanticType = SemanticFieldType.AddressLine, Roles = FieldRole.Searchable | FieldRole.Matchable, SimilarityEvaluator = "jaccard", Weight = 1.0 },
            new ProfileField { Name = "postal_code", SemanticType = SemanticFieldType.PostalCode, Roles = FieldRole.Matchable, SimilarityEvaluator = "exact", Weight = 1.0 }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["exact-value", "token-name"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.90,
        ReviewThreshold = 0.75
    };

    /// <summary>
    /// The core profiles shipped with the engine. Both resolve with zero
    /// configuration and are overridable by a loaded JSON profile of the same
    /// content type. This is the single source of truth for the built-in set,
    /// consumed by both the CLI and the API composition roots.
    /// </summary>
    public static IReadOnlyList<MatchingProfile> BuiltInProfiles() =>
        [CreatePersonProfile(), CreateOrganizationProfile()];

    /// <summary>
    /// The behavior-parity person profile: the pre-Milestone-13 default strategies
    /// ("default" similarity + scoring, unit weights, no per-field evaluators). It
    /// reproduces the durable matcher's 0.98 / 0.80 / 0 decisions and is the
    /// Milestone 16 baseline. Used by the engine↔durable parity tests.
    /// </summary>
    public static MatchingProfile CreateParityPersonProfile() => new()
    {
        ContentType = "person",
        Fields =
        [
            new ProfileField { Name = "first_name", SemanticType = SemanticFieldType.FirstName, Roles = FieldRole.Searchable | FieldRole.Matchable },
            new ProfileField { Name = "last_name", SemanticType = SemanticFieldType.LastName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "full_name", SemanticType = SemanticFieldType.FullName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "name", SemanticType = SemanticFieldType.FullName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "email", SemanticType = SemanticFieldType.Email, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "phone", SemanticType = SemanticFieldType.Phone, Roles = FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "date_of_birth", SemanticType = SemanticFieldType.DateOfBirth, Roles = FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "domain_name", SemanticType = SemanticFieldType.DomainName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "organization_name", SemanticType = SemanticFieldType.OrganizationName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "address_line", SemanticType = SemanticFieldType.AddressLine, Roles = FieldRole.Searchable | FieldRole.Matchable },
            new ProfileField { Name = "postal_code", SemanticType = SemanticFieldType.PostalCode, Roles = FieldRole.Matchable }
        ],
        NormalizationStrategy = "semantic-field",
        BlockingStrategies = ["exact-value", "token-name", "prefix", "ngram", "phonetic", "dob-lastname-phonetic"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "default",
        ScoringStrategy = "default",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.90,
        ReviewThreshold = 0.75
    };
}
