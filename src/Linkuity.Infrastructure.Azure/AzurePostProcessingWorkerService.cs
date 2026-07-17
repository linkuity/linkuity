using Azure.Messaging.ServiceBus;
using Linkuity.Pipeline;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Linkuity.Infrastructure.Azure;

internal sealed class AzurePostProcessingWorkerService : BackgroundService
{
    private readonly ServiceBusClient _sbClient;
    private readonly PostProcessingService _postProcessingService;
    private readonly ILogger<AzurePostProcessingWorkerService> _logger;
    private readonly string _queueName;

    public AzurePostProcessingWorkerService(
        ServiceBusClient sbClient,
        PostProcessingService postProcessingService,
        IOptions<ServiceBusOptions> options,
        ILogger<AzurePostProcessingWorkerService> logger)
    {
        _sbClient = sbClient;
        _postProcessingService = postProcessingService;
        _logger = logger;
        _queueName = options.Value.PostProcessingQueueName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var receiver = _sbClient.CreateReceiver(_queueName);
        _logger.LogInformation("Azure post-processing worker started, listening on {Queue}", _queueName);

        while (!stoppingToken.IsCancellationRequested)
        {
            ServiceBusReceivedMessage? message;
            try
            {
                message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (message == null) continue;

            var jobId = message.Body.ToString();
            _logger.LogInformation("Processing job {JobId}", jobId);
            try
            {
                await _postProcessingService.ProcessAsync(jobId, stoppingToken);
                await receiver.CompleteMessageAsync(message, stoppingToken);
                _logger.LogInformation("Completed job {JobId}", jobId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed job {JobId}", jobId);
                await receiver.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
            }
        }
    }
}
