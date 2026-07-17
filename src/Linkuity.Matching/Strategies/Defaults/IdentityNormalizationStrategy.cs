using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Identity normalization: returns the record unchanged. The durable path stores
/// raw field values and the field evaluators normalize internally (exact via
/// MatchKey.Normalize, date by parsing), so applying semantic normalization to only
/// the incoming record would compare a normalized value against a raw corpus value
/// (e.g. an E.164 phone against a raw phone) and miss exact-identifier matches. This
/// keeps both sides in the same raw form, reproducing the durable matcher's behavior.
/// </summary>
public sealed class IdentityNormalizationStrategy : INormalizationStrategy
{
    public string Name => "identity";
    public EntityRecord Normalize(EntityRecord record, MatchingProfile profile) => record;
}
