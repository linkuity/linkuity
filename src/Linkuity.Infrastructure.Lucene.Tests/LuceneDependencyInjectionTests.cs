using Linkuity.Infrastructure.Lucene;
using Linkuity.Infrastructure.Lucene.DependencyInjection;
using Linkuity.Matching.DependencyInjection;
using Linkuity.Matching.Strategies;
using Microsoft.Extensions.DependencyInjection;

namespace Linkuity.Infrastructure.Lucene.Tests;

public class LuceneDependencyInjectionTests
{
    [Fact]
    public void AddLinkuityLuceneRetrieval_RegistersStrategyUnderLuceneKey()
    {
        var services = new ServiceCollection();
        services.AddLinkuityMatchingDefaults();
        services.AddLinkuityLuceneRetrieval(LuceneTestRecords.TempDir());

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IStrategyRegistry>();

        Assert.True(registry.CandidateRetrieval.ContainsKey("lucene"));
        Assert.True(registry.CandidateRetrieval.ContainsKey("linear")); // default left intact
        Assert.IsType<LuceneCandidateRetrieval>(registry.CandidateRetrieval["lucene"]);
    }

    [Fact]
    public void IndexedAndPlainStrategyResolveToSameSingleton()
    {
        var services = new ServiceCollection();
        services.AddLinkuityLuceneRetrieval(LuceneTestRecords.TempDir());

        using var provider = services.BuildServiceProvider();
        var indexed = provider.GetRequiredService<IIndexedCandidateRetrievalStrategy>();
        var plain = provider.GetRequiredService<ICandidateRetrievalStrategy>();

        Assert.Same(indexed, plain);
    }
}
