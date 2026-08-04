using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching;

public interface IMatchingEngine
{
    MatchResult Resolve(EntityRecord record, IReadOnlyCollection<EntityRecord> corpus, MatchingProfile profile);

    /// <summary>
    /// As <see cref="Resolve(EntityRecord, IReadOnlyCollection{EntityRecord}, MatchingProfile)"/>,
    /// but also reports every comparison the engine made against <paramref name="corpus"/> —
    /// including ones scoring below <c>ReviewThreshold</c>, which never become a
    /// <see cref="ScoredCandidate"/> on <see cref="MatchResult.Candidates"/>. A caller that needs
    /// to know "compared and rejected" from "never compared" (cluster cohesion counting) has no
    /// other way to see that population: the three-arg overload's returned candidates are bounded
    /// by the review threshold by design, and that filter is not going away.
    /// </summary>
    MatchResult Resolve(EntityRecord record, IReadOnlyCollection<EntityRecord> corpus, MatchingProfile profile, out IReadOnlyList<ScoredCandidate> allComparisons);

    IReadOnlyList<string> GenerateBlockingKeys(EntityRecord record, MatchingProfile profile);

    /// <summary>
    /// The scale <paramref name="profile"/>'s resolved scoring strategy produces. <c>Resolve</c>
    /// already looks this up internally (it needs it to pass the right scale to the decision
    /// strategy); this exposes the same lookup to callers that build their own
    /// <see cref="MatchThresholds"/> outside <c>Resolve</c> — durable incremental ingest and batch
    /// matching both classify bands themselves rather than reading <c>MatchResult.Decision</c>, so
    /// each needs to ask this question independently rather than assuming
    /// <see cref="ScoreScale.UnitInterval"/> the way every one of them did before "evidence" shipped.
    /// </summary>
    ScoreScale ScaleOf(MatchingProfile profile);

    /// <summary>
    /// Prepares an incoming record for durable storage: normalizes its field values, then derives
    /// blocking keys from the normalized values.
    ///
    /// The order is the whole point. Blocking keys are computed once, at write time, and stored.
    /// Normalizing after that — or only at compare time — leaves retrieval keyed on raw text, so
    /// two records that differ only in formatting land in different blocks and are never compared
    /// at all. That is a retrieval failure, and no amount of scoring work can recover it.
    /// </summary>
    EntityRecord PrepareForStorage(EntityRecord record, MatchingProfile profile);
}
