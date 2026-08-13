namespace Linkuity.Pipeline.Tests;

/// <summary>
/// The gate that "better than last time" cannot express.
///
/// The baseline comparison gates precision RELATIVE to a previous run, which catches regressions
/// and cannot enforce a standard: the S1 configuration merged 610,191 pairs wrongly and passed
/// comfortably, because the run it was compared against merged 496 million wrongly. Under a rule
/// that merging must never be wrong, the question a gate has to answer is "is this good enough to
/// merge on", which only an absolute floor can ask.
/// </summary>
public class MergePrecisionGateTests
{
    [Fact]
    public void NoFloorDeclared_IsReportedAsNotEvaluated_NotAsPassing()
    {
        // The distinction the whole record exists to preserve. A gate nobody set has not been met,
        // and collapsing the two is how an ungated run comes to be cited as a safe one.
        var gate = new MergePrecisionGate(null, PredictedPositive: 1000, TruePositive: 1);

        Assert.False(gate.Evaluated);
        Assert.True(gate.Passed);
        Assert.Null(gate.FailureMessage);
    }

    [Fact]
    public void WrongMerges_AreCountedNotJustRated()
    {
        // 99% precision reads as reassuring until the raw count is spelled out.
        var gate = new MergePrecisionGate(0.99, PredictedPositive: 1_000_000, TruePositive: 990_000);

        Assert.Equal(10_000, gate.WrongMerges);
        Assert.Equal(0.99, gate.Precision!.Value, 10);
    }

    [Theory]
    [InlineData(0.99, 1000, 990, true)]    // exactly at the floor passes
    [InlineData(0.99, 1000, 989, false)]
    [InlineData(0.50, 1000, 500, true)]
    [InlineData(0.999, 1000, 990, false)]
    public void FloorIsEnforcedOnTheRatio(double floor, long predicted, long truePositive, bool expected)
        => Assert.Equal(expected, new MergePrecisionGate(floor, predicted, truePositive).Passed);

    [Fact]
    public void MergingNothing_PassesVacuously()
    {
        // Precision is undefined, not zero: merging nothing over-merges nothing. Failing here would
        // make the safest possible run the one that cannot satisfy the gate.
        var gate = new MergePrecisionGate(0.99, PredictedPositive: 0, TruePositive: 0);

        Assert.Null(gate.Precision);
        Assert.True(gate.Passed);
    }

    [Fact]
    public void FailureMessage_NamesTheFloor_ThePrecision_AndTheRawCount()
    {
        var message = new MergePrecisionGate(0.99, 1000, 800).FailureMessage;

        Assert.NotNull(message);
        Assert.Contains("99.0000%", message);      // the declared floor
        Assert.Contains("800/1000", message);      // what was achieved
        Assert.Contains("200 pair(s)", message);   // the wrong merges, in records not ratios
    }

    [Fact]
    public void TheRelativeGateItReplaces_WouldHavePassedTheS1Configuration()
    {
        // Numbers from the S1 measurement. Precision 34.41%: two thirds of merges wrong. The
        // baseline comparison only asks whether precision FELL, so against a worse predecessor
        // this passes; an absolute floor is what refuses it.
        var s1 = new MergePrecisionGate(0.99, PredictedPositive: 930_310, TruePositive: 320_119);

        Assert.False(s1.Passed);
        Assert.Equal(610_191, s1.WrongMerges);
    }
}
