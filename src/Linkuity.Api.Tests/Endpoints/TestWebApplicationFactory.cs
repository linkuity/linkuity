using Linkuity.Api.Tests.TestDoubles;
using Linkuity.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Linkuity.Api.Tests.Endpoints;

public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore, InMemoryBlobStore>();
            services.RemoveAll<IJobDispatcher>();
            services.AddSingleton<IJobDispatcher, CapturingJobDispatcher>();
        });
    }
}
