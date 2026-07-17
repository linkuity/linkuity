namespace Linkuity.Infrastructure.Azure;

public class ServiceBusOptions
{
    public required string ConnectionString { get; init; }
    public required string SmallJobQueueName { get; init; }
    public required string LargeJobQueueName { get; init; }
    public int LargeJobThreshold { get; init; } = 10_000;
    public string PostProcessingQueueName { get; init; } = "linkuity-post-processing";
}
