using Linkuity.Core.Interfaces;
using Linkuity.Core.Runtime;
using Linkuity.Infrastructure.Azure;
using Linkuity.Infrastructure.Local;
using Linkuity.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var runtimeMode = builder.Configuration.GetValue<RuntimeMode?>("Linkuity:RuntimeMode") ?? RuntimeMode.Local;
if (runtimeMode == RuntimeMode.Azure)
{
    builder.Services.AddAzurePostProcessingWorker(builder.Configuration);
}
else
{
    var artifactRoot = builder.Configuration.GetValue<string>("ArtifactStorage:RootPath") ?? ".linkuity/jobs";
    builder.Services.AddSingleton<IArtifactStore>(_ =>
        new FileSystemArtifactStore(new FileSystemArtifactStoreOptions
        {
            RootPath = artifactRoot
        }));
}
builder.Services.AddSingleton<GraphService>();
builder.Services.AddSingleton<GoldenRecordService>();
builder.Services.AddSingleton<PostProcessingService>();

var host = builder.Build();

await host.RunAsync();
