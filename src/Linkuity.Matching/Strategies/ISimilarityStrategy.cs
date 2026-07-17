using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies;

public interface ISimilarityStrategy
{
    string Name { get; }
    IReadOnlyList<SimilaritySignal> Evaluate(EntityRecord left, EntityRecord right, MatchingProfile profile);
}
