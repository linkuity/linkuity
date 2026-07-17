using Azure.Messaging.ServiceBus;
using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;
using Microsoft.Extensions.Options;

namespace Linkuity.Infrastructure.Azure;

public class AzureServiceBusJobDispatcher : IJobDispatcher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly JobQueueSelector _queueSelector;

    public AzureServiceBusJobDispatcher(IOptions<ServiceBusOptions> options)
    {
        var opts = options.Value;
        _client = new ServiceBusClient(opts.ConnectionString);
        _queueSelector = new JobQueueSelector(opts);
    }

    public async Task DispatchAsync(Job job, CancellationToken ct = default)
    {
        var queueName = _queueSelector.GetQueueName(job.RecordCount);
        var sender = _client.CreateSender(queueName);
        await using var _ = sender;
        await sender.SendMessageAsync(new ServiceBusMessage(job.Id.ToString()), ct);
    }

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();
}
