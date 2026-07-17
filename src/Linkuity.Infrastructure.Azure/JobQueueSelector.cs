namespace Linkuity.Infrastructure.Azure;

public class JobQueueSelector
{
    private readonly string _smallQueueName;
    private readonly string _largeQueueName;
    private readonly int _largeJobThreshold;

    public JobQueueSelector(ServiceBusOptions options)
    {
        _smallQueueName = options.SmallJobQueueName;
        _largeQueueName = options.LargeJobQueueName;
        _largeJobThreshold = options.LargeJobThreshold;
    }

    public string GetQueueName(int recordCount) =>
        recordCount < _largeJobThreshold ? _smallQueueName : _largeQueueName;
}
