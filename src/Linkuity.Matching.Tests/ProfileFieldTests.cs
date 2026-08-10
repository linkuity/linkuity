using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Tests;

/// <summary>
/// <see cref="ProfileField.IsAbsent"/> is the single predicate every "is this field present"
/// check in the engine must share, so a sentinel cannot be honoured by one caller and missed by
/// another.
/// </summary>
public class ProfileFieldTests
{
    private static ProfileField Field(IReadOnlyList<string>? nullEquivalents = null) => new()
    {
        Name = "legal_form",
        SemanticType = SemanticFieldType.LegalForm,
        Roles = FieldRole.Matchable,
        NullEquivalents = nullEquivalents
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOrNullValue_IsAlwaysAbsent_RegardlessOfNullEquivalents(string? value)
        => Assert.True(Field(["8888"]).IsAbsent(value));

    [Fact]
    public void DeclaredSentinelValue_IsAbsent()
        => Assert.True(Field(["8888"]).IsAbsent("8888"));

    [Fact]
    public void UndeclaredValue_IsNotAbsent()
        => Assert.False(Field(["8888"]).IsAbsent("LLC"));

    [Fact]
    public void ComparisonIsCaseInsensitive()
        => Assert.True(Field(["unknown"]).IsAbsent("UNKNOWN"));

    [Fact]
    public void ComparisonIsTrimInsensitiveOnBothSides()
    {
        Assert.True(Field([" 8888 "]).IsAbsent("8888"));
        Assert.True(Field(["8888"]).IsAbsent(" 8888 "));
    }

    [Fact]
    public void NoNullEquivalentsDeclared_OnlyBlankIsAbsent()
    {
        var field = Field(nullEquivalents: null);
        Assert.False(field.IsAbsent("8888"));
        Assert.True(field.IsAbsent(""));
    }

    [Fact]
    public void EmptyNullEquivalentsList_BehavesLikeNull()
    {
        var field = Field(nullEquivalents: []);
        Assert.False(field.IsAbsent("8888"));
        Assert.True(field.IsAbsent(""));
    }

    [Fact]
    public void MultipleSentinels_AnyDeclaredValueIsAbsent()
    {
        var field = Field(["8888", "N/A", "UNKNOWN"]);
        Assert.True(field.IsAbsent("N/A"));
        Assert.True(field.IsAbsent("UNKNOWN"));
        Assert.False(field.IsAbsent("LLC"));
    }
}
