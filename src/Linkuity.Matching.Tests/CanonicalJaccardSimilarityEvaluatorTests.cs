using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class CanonicalJaccardSimilarityEvaluatorTests
{
    private static ProfileField OrgField() => new()
    {
        Name = "organization_name",
        SemanticType = SemanticFieldType.OrganizationName,
        Roles = FieldRole.Matchable
    };

    private static ProfileField AddressField() => new()
    {
        Name = "address_line",
        SemanticType = SemanticFieldType.AddressLine,
        Roles = FieldRole.Matchable
    };

    // The scoring-audit baseline's stuck true pairs: legal-suffix/article variants
    // must reach full similarity once canonicalized.
    [Theory]
    [InlineData("THE BOEING COMPANY", "BOEING CO")]
    [InlineData("INTEL CORPORATION", "INTEL CORP")]
    [InlineData("3M COMPANY", "3M CO")]
    [InlineData("STARBUCKS CORPORATION", "STARBUCKS CORP")]
    [InlineData("TEXAS INSTRUMENTS INCORPORATED", "TEXAS INSTRUMENTS INC")]
    [InlineData("THE WALT DISNEY COMPANY", "Walt Disney Co")]
    public void SuffixAndArticleVariants_ScoreOne(string left, string right)
    {
        var evaluator = new CanonicalJaccardSimilarityEvaluator();
        Assert.Equal(1.0, evaluator.Evaluate(left, right, OrgField())!.Value, 10);
    }

    [Fact]
    public void SharedNoiseTokensOnly_ScoreZero()
    {
        // Raw Jaccard gives this FALSE pair 0.40 from shared THE/COMPANY;
        // canonical sets {WALT,DISNEY} vs {BOEING} share nothing.
        var evaluator = new CanonicalJaccardSimilarityEvaluator();
        Assert.Equal(0.0,
            evaluator.Evaluate("THE WALT DISNEY COMPANY", "THE BOEING COMPANY", OrgField())!.Value, 10);
    }

    [Fact]
    public void PartialOverlap_ScoresPlainJaccardFraction_NoContainmentBonus()
    {
        // {FORD, MOTOR} vs {FORD, TRUCKS} -> 1/3, not overlap-coefficient 1/2.
        var evaluator = new CanonicalJaccardSimilarityEvaluator();
        Assert.Equal(1.0 / 3.0,
            evaluator.Evaluate("FORD MOTOR CO", "FORD TRUCKS CO", OrgField())!.Value, 10);
    }

    [Fact]
    public void SpaceSplitNames_ScoreZero_KnownWalmartLimit()
    {
        // {WALMART} vs {WAL, MART, STORES}: no join variants in v1 (spec decision 2).
        var evaluator = new CanonicalJaccardSimilarityEvaluator();
        Assert.Equal(0.0,
            evaluator.Evaluate("WALMART INC.", "WAL MART STORES INC", OrgField())!.Value, 10);
    }

    [Fact]
    public void AmpersandInitials_CollapseToEqualCanonicalForms()
    {
        // Both canonicalize to {ATT}. (NOT "A T & T" - a leading standalone A
        // is dropped as an article, canonicalizing to {TT}.)
        var evaluator = new CanonicalJaccardSimilarityEvaluator();
        Assert.Equal(1.0, evaluator.Evaluate("AT&T INC", "AT & T CORP", OrgField())!.Value, 10);
    }

    [Fact]
    public void CasingAndPunctuation_DoNotMatter()
    {
        var evaluator = new CanonicalJaccardSimilarityEvaluator();
        Assert.Equal(1.0, evaluator.Evaluate("boeing co.", "THE BOEING COMPANY", OrgField())!.Value, 10);
    }

    [Theory]
    [InlineData("-", "BOEING CO")]
    [InlineData("BOEING CO", "   ")]
    [InlineData("...", "BOEING CO")]
    public void BlankOrPunctuationOnlySide_ReturnsNull(string left, string right)
    {
        var evaluator = new CanonicalJaccardSimilarityEvaluator();
        Assert.Null(evaluator.Evaluate(left, right, OrgField()));
    }

    [Fact]
    public void UnregisteredSemanticType_FallsBackToRawJaccard()
    {
        // AddressLine has no canonicalizer registered; behavior must equal the
        // plain jaccard evaluator on the same inputs.
        var canonical = new CanonicalJaccardSimilarityEvaluator();
        var raw = new JaccardSimilarityEvaluator();

        var field = AddressField();
        Assert.Equal(
            raw.Evaluate("100 Wall Street", "100 Wall St", field),
            canonical.Evaluate("100 Wall Street", "100 Wall St", field));
        Assert.Equal(
            raw.Evaluate("929 Long Bridge Drive", "2711 Centerville Road", field),
            canonical.Evaluate("929 Long Bridge Drive", "2711 Centerville Road", field));
        Assert.Null(canonical.Evaluate("...", "100 Wall Street", field));
    }

    [Fact]
    public void PeriodFusedNames_ScoreOneViaSquashEquality()
    {
        // The canonicalizer deletes periods, fusing AMAZON.COM into one token
        // AMAZONCOM; token-set Jaccard alone would score 0.0 against {AMAZON, COM}.
        // The squashed (concatenated) canonical forms are identical, so this is
        // the same name tokenized differently -> 1.0.
        var evaluator = new CanonicalJaccardSimilarityEvaluator();
        Assert.Equal(1.0, evaluator.Evaluate("AMAZON.COM, INC.", "AMAZON COM INC", OrgField())!.Value, 10);
    }

    [Fact]
    public void SquashEquality_IsOrderSensitive()
    {
        // {WALMART} vs {MART, WAL}: concatenations WALMART vs MARTWAL differ, so
        // the squash rule does not fire and Jaccard scores 0.
        var evaluator = new CanonicalJaccardSimilarityEvaluator();
        Assert.Equal(0.0, evaluator.Evaluate("WALMART", "MART WAL", OrgField())!.Value, 10);
    }

    [Fact]
    public void Name_IsCanonicalJaccard()
    {
        Assert.Equal("canonical-jaccard", new CanonicalJaccardSimilarityEvaluator().Name);
    }
}
