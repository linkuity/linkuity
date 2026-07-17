using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.DependencyInjection;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Microsoft.Extensions.DependencyInjection;

namespace Linkuity.Matching.Tests;

public class DependencyInjectionTests
{
    private static ServiceProvider BuildProvider()
        => new ServiceCollection().AddLinkuityMatchingDefaults().BuildServiceProvider();

    [Fact]
    public void Registry_HasAllSevenCategoriesPopulated()
    {
        using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IStrategyRegistry>();

        Assert.Contains("semantic-field", registry.Normalization.Keys);
        Assert.Contains("exact-value", registry.Blocking.Keys);
        Assert.Contains("token-name", registry.Blocking.Keys);
        Assert.Contains("prefix", registry.Blocking.Keys);
        Assert.Contains("ngram", registry.Blocking.Keys);
        Assert.Contains("phonetic", registry.Blocking.Keys);
        Assert.Contains("dob-lastname-phonetic", registry.Blocking.Keys);
        Assert.Contains("linear", registry.CandidateRetrieval.Keys);
        Assert.Contains("default", registry.Similarity.Keys);
        Assert.Contains("default", registry.Scoring.Keys);
        Assert.Contains("threshold", registry.Decision.Keys);
        Assert.Contains("union-find", registry.Clustering.Keys);
        Assert.Contains("field-weighted", registry.Similarity.Keys);
        Assert.Contains("weighted", registry.Scoring.Keys);
    }

    [Fact]
    public void Engine_ResolvesThroughResolvedProfile()
    {
        using var provider = BuildProvider();
        var engine = provider.GetRequiredService<IMatchingEngine>();
        var profile = provider.GetRequiredService<IMatchingProfileProvider>().GetProfile("person");

        var fields = new Dictionary<string, string> { ["email"] = "alice@example.com" };
        var template = TestRecords.Person("t", fields);
        var keys = engine.GenerateBlockingKeys(template, profile);
        var corpus = new[] { TestRecords.Person("a", fields, keys) };
        var incoming = TestRecords.Person("b", fields, keys);

        var result = engine.Resolve(incoming, corpus, profile);
        Assert.Equal(MatchDecision.AutoMatch, result.Decision);
    }

    [Fact]
    public void Registry_HasAllEvaluatorsRegistered()
    {
        using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IStrategyRegistry>();

        Assert.Contains("exact", registry.Evaluators.Keys);
        Assert.Contains("fuzzy", registry.Evaluators.Keys);
        Assert.Contains("jaccard", registry.Evaluators.Keys);
        Assert.Contains("ngram", registry.Evaluators.Keys);
        Assert.Contains("numeric", registry.Evaluators.Keys);
        Assert.Contains("date", registry.Evaluators.Keys);
    }
}
