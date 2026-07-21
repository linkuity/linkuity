var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("linkuity-storage")
    .RunAsEmulator(emulator => emulator.WithBlobPort(10000));
var blobs = storage.AddBlobs("linkuity-blobs");

builder.AddProject<Projects.Linkuity_Api>("linkuity-api")
    .WaitFor(storage)
    .WithEnvironment("Linkuity__RuntimeMode", "Azure")
    .WithEnvironment("BlobStorage__ConnectionString", blobs);

builder.Build().Run();
