var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("linkuity-storage")
    .RunAsEmulator(emulator => emulator
        .WithBlobPort(10000)
        .WithQueuePort(10001)
        .WithTablePort(10002));
var blobs = storage.AddBlobs("linkuity-blobs");

var serviceBus = builder.AddAzureServiceBus("linkuity-servicebus")
    .RunAsEmulator(emulator => emulator.WithHostPort(5672));
serviceBus.AddServiceBusQueue("linkuity-jobs-small");
serviceBus.AddServiceBusQueue("linkuity-jobs-large");
serviceBus.AddServiceBusQueue("linkuity-post-processing");

builder.AddProject<Projects.Linkuity_Api>("linkuity-api")
    .WaitFor(storage)
    .WaitFor(serviceBus)
    .WithEnvironment("Linkuity__RuntimeMode", "Azure")
    .WithEnvironment("BlobStorage__ConnectionString", blobs)
    .WithEnvironment("AzureServiceBus__ConnectionString", serviceBus);

builder.AddProject<Projects.Linkuity_Worker>("linkuity-worker")
    .WaitFor(storage)
    .WaitFor(serviceBus)
    .WithEnvironment("Linkuity__RuntimeMode", "Azure")
    .WithEnvironment("BlobStorage__ConnectionString", blobs)
    .WithEnvironment("BlobStorage__ContainerName", "linkuity-jobs")
    .WithEnvironment("AzureServiceBus__ConnectionString", serviceBus)
    .WithEnvironment("AzureServiceBus__PostProcessingQueueName", "linkuity-post-processing");

builder.Build().Run();
