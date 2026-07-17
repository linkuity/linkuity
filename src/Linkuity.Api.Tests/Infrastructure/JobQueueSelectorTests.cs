using Linkuity.Infrastructure.Azure;

namespace Linkuity.Api.Tests.Infrastructure;

public class JobQueueSelectorTests
{
    private static JobQueueSelector Build(int threshold = 10_000) => new(new ServiceBusOptions
    {
        ConnectionString = "fake",
        SmallJobQueueName = "jobs-small",
        LargeJobQueueName = "jobs-large",
        LargeJobThreshold = threshold
    });

    [Fact]
    public void GetQueueName_RecordCountBelowThreshold_ReturnsSmallQueue()
    {
        var selector = Build(threshold: 10_000);

        Assert.Equal("jobs-small", selector.GetQueueName(9_999));
    }

    [Fact]
    public void GetQueueName_RecordCountAtThreshold_ReturnsLargeQueue()
    {
        var selector = Build(threshold: 10_000);

        Assert.Equal("jobs-large", selector.GetQueueName(10_000));
    }

    [Fact]
    public void GetQueueName_RecordCountAboveThreshold_ReturnsLargeQueue()
    {
        var selector = Build(threshold: 10_000);

        Assert.Equal("jobs-large", selector.GetQueueName(50_000));
    }
}
