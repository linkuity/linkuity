using Linkuity.Core.Models;
using Linkuity.Core.Normalization;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Matching;

/// <summary>
/// Orchestrates the pipeline: normalization -> blocking -> candidate retrieval ->
/// similarity -> scoring -> decision, selecting each strategy from the registry by
/// the names on the profile. Returns a MatchResult with score, decision, ordered
/// candidates, and breakdown. Clustering is exposed via the registry but not driven
/// by Resolve (it operates on accepted pairs in the durable path; see Milestone 16).
/// </summary>
public sealed class MatchingEngine : IMatchingEngine
{
    private readonly IStrategyRegistry _registry;

    public MatchingEngine(IStrategyRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>
    /// Resolves <paramref name="record"/> against <paramref name="corpus"/> using the
    /// strategies named by <paramref name="profile"/>. The engine normalizes only the
    /// incoming record; corpus records are assumed to be already normalized (as durable
    /// records are). When the Milestone 16 integration feeds the durable corpus in, it
    /// must pass already-normalized records so exact-identifier matching stays consistent.
    /// </summary>
    public MatchResult Resolve(EntityRecord record, IReadOnlyCollection<EntityRecord> corpus, MatchingProfile profile)
        => Resolve(record, corpus, profile, captureAll: false, out _);

    public MatchResult Resolve(EntityRecord record, IReadOnlyCollection<EntityRecord> corpus, MatchingProfile profile, out IReadOnlyList<ScoredCandidate> allComparisons)
        => Resolve(record, corpus, profile, captureAll: true, out allComparisons);

    // captureAll gates whether the below-review candidates are materialized at all. Every
    // existing caller goes through the 3-arg overload with captureAll: false, so it allocates
    // and evaluates exactly what it did before this method grew a second output — no ScoredCandidate
    // is built for a below-review candidate unless a caller actually asked to see it.
    private MatchResult Resolve(EntityRecord record, IReadOnlyCollection<EntityRecord> corpus, MatchingProfile profile, bool captureAll, out IReadOnlyList<ScoredCandidate> allComparisons)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(profile);

        var normalization = ProfileNormalization.Resolve(_registry, profile);
        var similarity = _registry.Similarity[profile.SimilarityStrategy];
        var scoring = _registry.Scoring[profile.ScoringStrategy];
        var decision = _registry.Decision[profile.DecisionStrategy];
        var retrieval = _registry.CandidateRetrieval[profile.CandidateRetrievalStrategy];

        var normalized = normalization.Normalize(record, profile);
        var resolved = EnsureBlockingKeys(normalized, profile);

        var candidates = retrieval.Retrieve(resolved, corpus, profile);

        var scored = new List<ScoredCandidate>();
        var all = captureAll ? new List<ScoredCandidate>() : null;
        foreach (var candidate in candidates)
        {
            var signals = similarity.Evaluate(resolved, candidate, profile);
            var score = scoring.Score(signals, profile);
            if (score.FinalScore >= profile.ReviewThreshold)
            {
                var scoredCandidate = new ScoredCandidate(candidate, score.FinalScore, score.Breakdown);
                scored.Add(scoredCandidate);
                all?.Add(scoredCandidate);
            }
            else if (all is not null)
            {
                all.Add(new ScoredCandidate(candidate, score.FinalScore, score.Breakdown));
            }
        }
        allComparisons = (IReadOnlyList<ScoredCandidate>?)all ?? [];

        // The durable path orders by score only; the ThenBy tiebreaker here is an
        // intentional, decision-neutral addition that makes Candidates[0]/Breakdown
        // deterministic. It cannot change decisions: the durable path edges every
        // qualifying candidate, so equal-score ordering is immaterial to outcomes.
        scored = scored
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Record.SourceRecordId, StringComparer.Ordinal)
            .ToList();

        var top = scored.Count > 0 ? scored[0] : null;
        var topScore = top?.Score ?? 0;
        return new MatchResult(topScore, decision.Decide(topScore, profile, scoring.Scale), scored, top?.Breakdown ?? []);
    }

    public ScoreScale ScaleOf(MatchingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _registry.Scoring[profile.ScoringStrategy].Scale;
    }

    public IReadOnlyList<string> GenerateBlockingKeys(EntityRecord record, MatchingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(profile);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in profile.BlockingStrategies)
        {
            foreach (var key in _registry.Blocking[name].GenerateKeys(record, profile))
                keys.Add(key);
        }
        return keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public EntityRecord PrepareForStorage(EntityRecord record, MatchingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(profile);

        // Normalization is idempotent, so applying it to input that was already normalized
        // upstream (the batch pipeline's normalized.csv, replayed through persist-batch) is a
        // no-op rather than a second transformation.
        var normalized = record with { Fields = RecordNormalizer.NormalizeFields(record.Fields, profile.NormalizationSettings()) };

        // Keys already present are honoured: a caller that computed them deliberately keeps them.
        return normalized.BlockingKeys.Count > 0
            ? normalized
            : normalized with { BlockingKeys = GenerateBlockingKeys(normalized, profile) };
    }

    private EntityRecord EnsureBlockingKeys(EntityRecord record, MatchingProfile profile)
    {
        if (record.BlockingKeys.Count > 0)
            return record;

        return new EntityRecord
        {
            Id = record.Id,
            ProjectId = record.ProjectId,
            SourceId = record.SourceId,
            IngestBatchId = record.IngestBatchId,
            SourceRecordId = record.SourceRecordId,
            Fields = record.Fields,
            BlockingKeys = GenerateBlockingKeys(record, profile),
            CreatedAt = record.CreatedAt
        };
    }
}
