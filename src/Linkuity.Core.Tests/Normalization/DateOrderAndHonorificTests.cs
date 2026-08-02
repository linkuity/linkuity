using Linkuity.Core.Models;
using Linkuity.Core.Normalization;

namespace Linkuity.Core.Tests.Normalization;

/// <summary>
/// Two US/English assumptions that were baked into normalization. Both share the shape that
/// makes an assumption expensive: they do not fail on data that violates them, they quietly
/// produce a different answer.
/// </summary>
public class DateOrderAndHonorificTests
{
    private static string Date(string value, DateFieldOrder order)
        => FieldNormalizer.Normalize(value, SemanticFieldType.DateOfBirth, dateOrder: order);

    // ── Date order ───────────────────────────────────────────────────────────────

    [Fact]
    public void Default_IsMonthFirst_SoExistingBehaviourIsUnchanged()
        => Assert.Equal("1980-03-04", FieldNormalizer.Normalize("03/04/1980", SemanticFieldType.DateOfBirth));

    /// <summary>
    /// The same eight characters, two different real dates. Neither reading is wrong about the
    /// text; only the configuration can say which was meant.
    /// </summary>
    [Fact]
    public void AmbiguousDate_ReadsDifferentlyUnderEachOrder()
    {
        Assert.Equal("1980-03-04", Date("03/04/1980", DateFieldOrder.MonthFirst));
        Assert.Equal("1980-04-03", Date("03/04/1980", DateFieldOrder.DayFirst));
    }

    /// <summary>
    /// Why the wrong setting is quiet rather than loud: a day past the twelfth cannot be a
    /// month, so it fails to parse and passes through — while every date earlier in the month
    /// parses to a confidently wrong value. A feed read under the wrong order is therefore
    /// partly raw and partly mislabelled, with nothing distinguishing the two.
    /// </summary>
    [Fact]
    public void DayFirstDateReadAsMonthFirst_FailsSilentlyOnlyPastTheTwelfth()
    {
        Assert.Equal("25/12/1980", Date("25/12/1980", DateFieldOrder.MonthFirst));   // unparsed
        Assert.Equal("1980-12-25", Date("25/12/1980", DateFieldOrder.DayFirst));     // correct
    }

    [Theory]
    [InlineData("1980-03-04")]
    [InlineData("1980/03/04")]
    public void IsoDates_ReadIdenticallyUnderBothOrders(string value)
        => Assert.Equal(Date(value, DateFieldOrder.MonthFirst), Date(value, DateFieldOrder.DayFirst));

    [Fact]
    public void DayFirst_AcceptsDayFirstTextualDates()
        => Assert.Equal("1980-04-17", Date("17 Apr 1980", DateFieldOrder.DayFirst));

    // ── Honorifics ───────────────────────────────────────────────────────────────

    private static string Name(string value)
        => FieldNormalizer.Normalize(value, SemanticFieldType.FirstName);

    [Theory]
    [InlineData("Mr. Smith")]
    [InlineData("Mr Smith")]      // bare form: at least as common, previously not stripped
    [InlineData("Mrs Smith")]
    [InlineData("Ms Smith")]
    [InlineData("Dr Smith")]
    [InlineData("Prof Smith")]
    [InlineData("Miss Smith")]
    public void Honorifics_AreStrippedWithOrWithoutAPeriod(string value)
        => Assert.Equal("Smith", Name(value));

    /// <summary>
    /// The guard that makes bare honorifics safe: stripping requires whitespace after the
    /// prefix, so names merely beginning with those letters are untouched. Without it, "Drew"
    /// would become "ew" and "Mission" would become "ion".
    /// </summary>
    [Theory]
    [InlineData("Drew")]
    [InlineData("Mission")]
    [InlineData("Missy")]
    [InlineData("Profitt")]
    [InlineData("Msyzka")]
    public void NamesBeginningWithAnHonorific_AreNotStripped(string value)
        => Assert.Equal(value, Name(value));

    [Fact]
    public void HonorificAlone_IsLeftAlone()
        => Assert.Equal("Mr", Name("Mr"));
}
