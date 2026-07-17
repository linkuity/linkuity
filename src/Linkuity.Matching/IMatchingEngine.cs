using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching;

public interface IMatchingEngine
{
    MatchResult Resolve(EntityRecord record, IReadOnlyCollection<EntityRecord> corpus, MatchingProfile profile);
    IReadOnlyList<string> GenerateBlockingKeys(EntityRecord record, MatchingProfile profile);
}
