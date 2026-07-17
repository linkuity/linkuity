using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Infrastructure.Lucene.DependencyInjection;
using Linkuity.Matching;
using Linkuity.Matching.DependencyInjection;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Microsoft.Extensions.DependencyInjection;

namespace Linkuity.Infrastructure.Lucene.Tests;

public class LuceneEngineIntegrationTests
{
    // A person profile whose only change is selecting Lucene retrieval; everything
    // else (weighted similarity + scoring + thresholds) is the real default.
    private static MatchingProfile LuceneProfile()
    {
        var baseline = DefaultMatchingProfileProvider.CreatePersonProfile();
        return new MatchingProfile
        {
            ContentType = baseline.ContentType,
            Fields = baseline.Fields,
            NormalizationStrategy = baseline.NormalizationStrategy,
            BlockingStrategies = baseline.BlockingStrategies,
            CandidateRetrievalStrategy = "lucene",
            SimilarityStrategy = baseline.SimilarityStrategy,
            ScoringStrategy = baseline.ScoringStrategy,
            DecisionStrategy = baseline.DecisionStrategy,
            ClusteringStrategy = baseline.ClusteringStrategy,
            AutoMatchThreshold = baseline.AutoMatchThreshold,
            ReviewThreshold = baseline.ReviewThreshold,
            ReviewFloorGate = baseline.ReviewFloorGate
        };
    }

    [Fact]
    public void Engine_WithLuceneRetrieval_ScoresFromTheScorer_NotLuceneRelevance()
    {
        var services = new ServiceCollection();
        services.AddLinkuityMatchingDefaults();
        services.AddLinkuityLuceneRetrieval(LuceneTestRecords.TempDir());
        using var provider = services.BuildServiceProvider();

        var engine = provider.GetRequiredService<IMatchingEngine>();
        var index = provider.GetRequiredService<IIndexedCandidateRetrievalStrategy>();
        var profile = LuceneProfile();

        var existing = LuceneTestRecords.Person("a", new Dictionary<string, string>
        {
            ["first_name"] = "Alice", ["last_name"] = "Smith", ["email"] = "alice@example.com"
        });
        index.Index(existing);
        index.Commit();

        var incoming = new EntityRecord
        {
            Id = Guid.NewGuid(), ProjectId = existing.ProjectId, SourceId = existing.SourceId,
            IngestBatchId = existing.IngestBatchId, SourceRecordId = "c",
            Fields = new Dictionary<string, string> { ["first_name"] = "Alice", ["last_name"] = "Smith", ["email"] = "alice@example.com" },
            CreatedAt = existing.CreatedAt
        };

        // Empty corpus: candidates can only come from the Lucene index, proving retrieval is index-driven.
        var result = engine.Resolve(incoming, corpus: [], profile);

        Assert.NotEmpty(result.Candidates);
        Assert.Contains(result.Candidates, c => c.Record.Id == existing.Id);
        // The score is a normalized scorer output in [0,1], never a raw Lucene relevance score (which exceeds 1 here).
        Assert.InRange(result.FinalScore, 0.0, 1.0);
        Assert.NotEqual(MatchDecision.NoMatch, result.Decision);
    }
}
