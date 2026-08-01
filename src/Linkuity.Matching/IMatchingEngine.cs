using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching;

public interface IMatchingEngine
{
    MatchResult Resolve(EntityRecord record, IReadOnlyCollection<EntityRecord> corpus, MatchingProfile profile);
    IReadOnlyList<string> GenerateBlockingKeys(EntityRecord record, MatchingProfile profile);

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
