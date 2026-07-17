using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies;

public interface IBlockingStrategy
{
    string Name { get; }
    IReadOnlyList<string> GenerateKeys(EntityRecord record, MatchingProfile profile);
}
