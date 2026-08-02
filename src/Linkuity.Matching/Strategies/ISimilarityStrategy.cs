using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies;

public interface ISimilarityStrategy
{
    string Name { get; }

    /// <summary>Shape of the signals this strategy emits. See <see cref="SignalShape"/>.</summary>
    SignalShape Produces { get; }
    IReadOnlyList<SimilaritySignal> Evaluate(EntityRecord left, EntityRecord right, MatchingProfile profile);
}
