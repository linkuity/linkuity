using System.Net;
using System.Net.Http.Json;
using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Linkuity.Api.Tests.Endpoints;

public class ProjectEndpointsTests
{
    [Fact]
    public async Task ProjectSourceAndBatchEndpoints_PersistMetadata()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var createProject = await client.PostAsJsonAsync(
            "/projects",
            new
            {
                name = "Customer MDM",
                contentType = "person",
                mergeConfiguration = new
                {
                    mergeFields = new[]
                    {
                        new { fieldName = "email", sourcePriority = new[] { "CRM", "Marketing" } }
                    }
                }
            });
        Assert.Equal(HttpStatusCode.Created, createProject.StatusCode);
        var project = await createProject.Content.ReadFromJsonAsync<Project>();
        Assert.NotNull(project);
        Assert.Equal("email", project.MergeConfiguration!.MergeFields[0].FieldName);

        var projects = await client.GetFromJsonAsync<Project[]>("/projects");
        Assert.Equal(project.Id, Assert.Single(projects!).Id);

        var fetchedProject = await client.GetFromJsonAsync<Project>($"/projects/{project.Id}");
        Assert.NotNull(fetchedProject);
        Assert.Equal(["CRM", "Marketing"], fetchedProject.MergeConfiguration!.MergeFields[0].SourcePriority);

        var createSource = await client.PostAsJsonAsync($"/projects/{project.Id}/sources", new { name = "CRM" });
        Assert.Equal(HttpStatusCode.Created, createSource.StatusCode);
        var source = await createSource.Content.ReadFromJsonAsync<Source>();
        Assert.NotNull(source);
        Assert.Equal(project.Id, source.ProjectId);

        var createBatch = await client.PostAsJsonAsync(
            $"/projects/{project.Id}/sources/{source.Id}/batches",
            new { jobId = Guid.NewGuid(), recordCount = 2 });
        Assert.Equal(HttpStatusCode.Created, createBatch.StatusCode);
        var batch = await createBatch.Content.ReadFromJsonAsync<IngestBatch>();
        Assert.NotNull(batch);
        Assert.Equal(source.Id, batch.SourceId);

        var batches = await client.GetFromJsonAsync<IngestBatch[]>($"/projects/{project.Id}/batches");
        Assert.Equal(batch.Id, Assert.Single(batches!).Id);
    }

    [Fact]
    public async Task UpdateProjectMergePolicy_PersistsPolicyAndMissingProjectReturnsNotFound()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var createProject = await client.PostAsJsonAsync("/projects", new { name = "Customer MDM", contentType = "person" });
        var project = await createProject.Content.ReadFromJsonAsync<Project>();
        Assert.NotNull(project);

        var update = await client.PutAsJsonAsync(
            $"/projects/{project.Id}/merge-policy",
            new
            {
                mergeConfiguration = new
                {
                    mergeFields = new[]
                    {
                        new { fieldName = "phone", sourcePriority = new[] { "Support", "CRM" } }
                    }
                }
            });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<Project>();
        Assert.NotNull(updated);
        Assert.Equal("phone", updated.MergeConfiguration!.MergeFields[0].FieldName);
        Assert.Equal(["Support", "CRM"], updated.MergeConfiguration.MergeFields[0].SourcePriority);

        var missing = await client.PutAsJsonAsync(
            $"/projects/{Guid.NewGuid()}/merge-policy",
            new { mergeConfiguration = (object?)null });

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task CreateProject_WhenMergePolicyHasDuplicateFields_ReturnsBadRequest()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/projects",
            new
            {
                name = "Customer MDM",
                contentType = "person",
                mergeConfiguration = new
                {
                    mergeFields = new[]
                    {
                        new { fieldName = "email", sourcePriority = new[] { "CRM" } },
                        new { fieldName = "EMAIL", sourcePriority = new[] { "Marketing" } }
                    }
                }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateSource_WhenProjectMissing_ReturnsNotFound()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/projects/{Guid.NewGuid()}/sources", new { name = "CRM" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"linkuity-api-metadata-{Guid.NewGuid():N}.json");
        return new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataStore>();
                services.AddSingleton<IMetadataStore>(_ =>
                    new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = databasePath }));
            });
        });
    }
}
