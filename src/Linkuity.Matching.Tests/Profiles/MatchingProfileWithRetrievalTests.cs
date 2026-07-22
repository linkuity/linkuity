using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Xunit;

namespace Linkuity.Matching.Tests.Profiles;

public sealed class MatchingProfileWithRetrievalTests
{
    [Fact]
    public void WithCandidateRetrievalStrategy_ReplacesStrategy_PreservesEverythingElse()
    {
        var original = DefaultMatchingProfileProvider.CreatePersonProfile();

        var updated = original.WithCandidateRetrievalStrategy("blocking-linear");

        Assert.Equal("blocking-linear", updated.CandidateRetrievalStrategy);
        Assert.Equal("linear", original.CandidateRetrievalStrategy); // original untouched
        Assert.Same(original.Fields, updated.Fields);
        Assert.Equal(original.ContentType, updated.ContentType);
        Assert.Equal(original.BlockingStrategies, updated.BlockingStrategies);
        Assert.Equal(original.AutoMatchThreshold, updated.AutoMatchThreshold);
        Assert.Equal(original.ReviewThreshold, updated.ReviewThreshold);
        Assert.Equal(original.ReviewFloorGate, updated.ReviewFloorGate);
    }
}
