using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class SimilarityEvaluatorTests
{
    private static ProfileField Field(string name, IReadOnlyDictionary<string, string>? options = null)
        => new()
        {
            Name = name,
            SemanticType = SemanticFieldType.FullName,
            Roles = FieldRole.Matchable,
            EvaluatorOptions = options
        };

    [Fact]
    public void Exact_ReturnsOneOnNormalizedEqualityAndZeroOtherwise()
    {
        var exact = new ExactSimilarityEvaluator();
        Assert.Equal(1.0, exact.Evaluate("Alice@Example.com", "alice@example.com", Field("email")));
        Assert.Equal(0.0, exact.Evaluate("alice@example.com", "bob@example.com", Field("email")));
    }

    [Fact]
    public void Exact_ReturnsNullWhenEitherSideNormalizesToEmpty()
    {
        var exact = new ExactSimilarityEvaluator();
        Assert.Null(exact.Evaluate("   ", "alice", Field("email")));
        Assert.Null(exact.Evaluate("alice", "...", Field("email")));
    }

    [Fact]
    public void Fuzzy_ScoresTyposHighAndUnrelatedLow()
    {
        var fuzzy = new FuzzyTextSimilarityEvaluator();
        Assert.True(fuzzy.Evaluate("Jonathon", "Johnathon", Field("first_name")) > 0.8);
        Assert.True(fuzzy.Evaluate("Catherine", "Kathryn", Field("first_name")) < 0.8);
    }

    [Fact]
    public void Fuzzy_GivesTransposedTokensFullCreditViaTokenSet()
    {
        var fuzzy = new FuzzyTextSimilarityEvaluator();
        Assert.True(fuzzy.Evaluate("John Smith", "Smith John", Field("full_name")) >= 0.99);
    }

    [Fact]
    public void Fuzzy_ReturnsNullWhenEitherSideBlank()
    {
        var fuzzy = new FuzzyTextSimilarityEvaluator();
        Assert.Null(fuzzy.Evaluate("  ", "John", Field("first_name")));
    }

    [Fact]
    public void Jaccard_IsIntersectionOverUnionOfTokens()
    {
        var jaccard = new JaccardSimilarityEvaluator();
        // {jane,doe} vs {jane,smith} -> 1/3
        Assert.Equal(1.0 / 3.0, jaccard.Evaluate("Jane Doe", "Jane Smith", Field("full_name"))!.Value, 10);
        Assert.Equal(1.0, jaccard.Evaluate("Jane Doe", "doe jane", Field("full_name"))!.Value, 10);
    }

    [Fact]
    public void Jaccard_ReturnsNullWhenEitherSideHasNoTokens()
    {
        var jaccard = new JaccardSimilarityEvaluator();
        Assert.Null(jaccard.Evaluate("...", "Jane", Field("full_name")));
    }

    [Fact]
    public void NGram_GivesHighDiceForOneCharacterTypo()
    {
        var ngram = new NGramSimilarityEvaluator();
        var score = ngram.Evaluate("Catherine", "Catharine", Field("first_name"))!.Value;
        Assert.True(score > 0.5);
        Assert.True(score < 1.0);
    }

    [Fact]
    public void NGram_IsOneForIdenticalNormalizedValues()
    {
        var ngram = new NGramSimilarityEvaluator();
        Assert.Equal(1.0, ngram.Evaluate("Smith", "smith", Field("last_name"))!.Value, 10);
    }

    [Fact]
    public void NGram_HonorsConfiguredSize()
    {
        var ngram = new NGramSimilarityEvaluator();
        var bigram = Field("last_name", new Dictionary<string, string> { ["ngram.size"] = "2" });
        Assert.True(ngram.Evaluate("Smith", "Smyth", bigram)!.Value > 0);
    }

    [Fact]
    public void NGram_ReturnsNullWhenEitherSideNormalizesToEmpty()
    {
        var ngram = new NGramSimilarityEvaluator();
        Assert.Null(ngram.Evaluate("   ", "Smith", Field("last_name")));
    }

    [Fact]
    public void Numeric_IsOneWhenEqualAndNullWhenUnparseable()
    {
        var numeric = new NumericSimilarityEvaluator();
        Assert.Equal(1.0, numeric.Evaluate("100", "100", Field("amount"))!.Value);
        Assert.Null(numeric.Evaluate("abc", "100", Field("amount")));
    }

    [Fact]
    public void Numeric_DecaysLinearlyWithinAbsoluteTolerance()
    {
        var numeric = new NumericSimilarityEvaluator();
        var field = Field("amount", new Dictionary<string, string> { ["numeric.tolerance"] = "10" });
        Assert.Equal(0.5, numeric.Evaluate("100", "105", field)!.Value, 10);
        Assert.Equal(0.0, numeric.Evaluate("100", "110", field)!.Value, 10);
        Assert.Equal(0.0, numeric.Evaluate("100", "200", field)!.Value, 10);
    }

    [Fact]
    public void Numeric_FallsBackToRelativeDifferenceWithoutOptions()
    {
        var numeric = new NumericSimilarityEvaluator();
        // |100-110| / max(100,110) = 0.0909..., similarity = 0.909...
        Assert.Equal(1.0 - 10.0 / 110.0, numeric.Evaluate("100", "110", Field("amount"))!.Value, 10);
    }

    [Fact]
    public void Date_IsOneForSameDayAndNullForUnparseable()
    {
        var date = new DateSimilarityEvaluator();
        Assert.Equal(1.0, date.Evaluate("1990-01-02", "1990-01-02", Field("date_of_birth"))!.Value);
        Assert.Null(date.Evaluate("not-a-date", "1990-01-02", Field("date_of_birth")));
    }

    [Fact]
    public void Date_DecaysLinearlyOverConfiguredWindow()
    {
        var date = new DateSimilarityEvaluator();
        var field = Field("date_of_birth", new Dictionary<string, string> { ["date.maxDays"] = "10" });
        Assert.Equal(0.5, date.Evaluate("1990-01-01", "1990-01-06", field)!.Value, 10);
        Assert.Equal(0.0, date.Evaluate("1990-01-01", "1990-01-11", field)!.Value, 10);
        Assert.Equal(0.0, date.Evaluate("1990-01-01", "1991-01-01", field)!.Value, 10);
    }
}
