using Linkuity.Infrastructure.Lucene;

namespace Linkuity.Infrastructure.Lucene.Tests;

public class LuceneOptionsTests
{
    [Fact]
    public void Options_HaveExpectedDefaults()
    {
        var options = new LuceneCandidateRetrievalOptions { IndexDirectory = "x" };

        Assert.Equal(50, options.MaxCandidates);
        Assert.Equal(4f, options.BlockingKeyBoost);
        Assert.Equal(2f, options.PhoneticBoost);
        Assert.Equal(1f, options.FuzzyBoost);
        Assert.Equal(2, options.FuzzyMaxEdits);
    }
}
