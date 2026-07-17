using Linkuity.Core.Models;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class CandidateRetrievalTests
{
    [Fact]
    public void Linear_ReturnsEntireCorpus()
    {
        ICandidateRetrievalStrategy strategy = new LinearCandidateRetrievalStrategy();
        var corpus = new[]
        {
            TestRecords.Person("a", new Dictionary<string, string> { ["email"] = "a@x.com" }),
            TestRecords.Person("b", new Dictionary<string, string> { ["email"] = "b@y.com" })
        };
        var record = TestRecords.Person("c", new Dictionary<string, string> { ["email"] = "c@z.com" });

        var result = strategy.Retrieve(record, corpus, TestProfiles.Person);

        Assert.Equal(2, result.Count);
        Assert.Equal(corpus.Select(r => r.Id), result.Select(r => r.Id));
    }

    [Fact]
    public void Linear_ReturnsEmptyForEmptyCorpus()
    {
        ICandidateRetrievalStrategy strategy = new LinearCandidateRetrievalStrategy();
        var record = TestRecords.Person("c", new Dictionary<string, string> { ["email"] = "c@z.com" });
        Assert.Empty(strategy.Retrieve(record, [], TestProfiles.Person));
    }
}
