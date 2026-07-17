using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies;

public interface INormalizationStrategy
{
    string Name { get; }
    EntityRecord Normalize(EntityRecord record, MatchingProfile profile);
}
