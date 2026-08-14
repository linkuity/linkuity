namespace Linkuity.Pipeline.Tests;

/// <summary>
/// Zero wrong merges, or the run failed. No threshold, and none configurable.
///
/// Two earlier shapes were both wrong. The baseline comparison gates precision RELATIVE to a
/// previous run, so the S1 configuration's 610,191 wrong merges passed comfortably against a
/// predecessor that made 496 million. Replacing it with a DECLARED floor was no better: a number
/// typed by whoever runs the report can be set to clear whatever result they just got, and
/// choosing one at all means deciding in advance how much silent damage is acceptable.
///
/// Records that cannot be told apart from the available fields are not an allowance to merge them
/// wrongly — they are a reason not to merge them at all. So corpus ambiguity never raises a
/// ceiling here, because there is no ceiling to raise.
/// </summary>
public class WrongMergeGateTests
{
    [Fact]
    public void NoWrongMerges_Passes()
    {
        var gate = new WrongMergeGate(PredictedPositive: 1000, TruePositive: 1000);

        Assert.True(gate.Passed);
        Assert.Equal(0, gate.WrongMerges);
        Assert.Null(gate.FailureMessage);
    }

    [Fact]
    public void OneWrongMerge_Fails()
    {
        // The whole point: there is no "nearly clean". 99.9% is a failure.
        var gate = new WrongMergeGate(PredictedPositive: 1000, TruePositive: 999);

        Assert.False(gate.Passed);
        Assert.Equal(1, gate.WrongMerges);
    }

    [Fact]
    public void MergingNothing_Passes()
    {
        // Merging nothing merges nothing wrongly. Refusing to merge is always safe; it is recall
        // that suffers, and recall is not what this gate is for.
        Assert.True(new WrongMergeGate(0, 0).Passed);
    }

    [Fact]
    public void FailureMessage_CountsPairs_NotARate()
    {
        // "99% precision" reads as reassuring. "9,900 pairs of unrelated companies were merged"
        // does not, and they are the same fact.
        var message = new WrongMergeGate(1_000_000, 990_000).FailureMessage;

        Assert.NotNull(message);
        Assert.Contains("10000 of 1000000", message, StringComparison.Ordinal);
        Assert.DoesNotContain("%", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheS1Configuration_Fails()
    {
        // 930,310 merged / 320,119 correct, from the S1 measurement. It passed the relative gate
        // and would have passed any floor someone was willing to type after seeing the result.
        var s1 = new WrongMergeGate(PredictedPositive: 930_310, TruePositive: 320_119);

        Assert.False(s1.Passed);
        Assert.Equal(610_191, s1.WrongMerges);
    }
}
