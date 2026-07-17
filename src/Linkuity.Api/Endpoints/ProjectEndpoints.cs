using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;

namespace Linkuity.Api.Endpoints;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
        app.MapPost("/projects", CreateProject);
        app.MapGet("/projects", ListProjects);
        app.MapGet("/projects/{projectId:guid}", GetProject);
        app.MapPut("/projects/{projectId:guid}/merge-policy", UpdateProjectMergePolicy);
        app.MapPost("/projects/{projectId:guid}/sources", CreateSource);
        app.MapGet("/projects/{projectId:guid}/sources", ListSources);
        app.MapPost("/projects/{projectId:guid}/sources/{sourceId:guid}/batches", CreateBatch);
        app.MapGet("/projects/{projectId:guid}/batches", ListBatches);
    }

    private static async Task<IResult> CreateProject(
        CreateProjectRequest request,
        IMetadataStore metadataStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest("Project name is required.");
        if (string.IsNullOrWhiteSpace(request.ContentType))
            return Results.BadRequest("Project contentType is required.");

        try
        {
            var project = await metadataStore.CreateProjectAsync(
                request.Name,
                request.ContentType,
                request.MergeConfiguration,
                DateTimeOffset.UtcNow,
                ct);
            return Results.Created($"/projects/{project.Id}", project);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
    }

    private static async Task<IResult> ListProjects(IMetadataStore metadataStore, CancellationToken ct)
        => Results.Ok(await metadataStore.ListProjectsAsync(ct));

    private static async Task<IResult> GetProject(Guid projectId, IMetadataStore metadataStore, CancellationToken ct)
    {
        var project = await metadataStore.GetProjectAsync(projectId, ct);
        return project is null ? Results.NotFound() : Results.Ok(project);
    }

    private static async Task<IResult> UpdateProjectMergePolicy(
        Guid projectId,
        UpdateMergePolicyRequest request,
        IMetadataStore metadataStore,
        CancellationToken ct)
    {
        try
        {
            var project = await metadataStore.UpdateProjectMergePolicyAsync(projectId, request.MergeConfiguration, ct);
            return Results.Ok(project);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Project not found", StringComparison.Ordinal))
        {
            return Results.NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
    }

    private static async Task<IResult> CreateSource(
        Guid projectId,
        CreateSourceRequest request,
        IMetadataStore metadataStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest("Source name is required.");

        try
        {
            var source = await metadataStore.CreateSourceAsync(projectId, request.Name, DateTimeOffset.UtcNow, ct);
            return Results.Created($"/projects/{projectId}/sources/{source.Id}", source);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Project not found", StringComparison.Ordinal))
        {
            return Results.NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> ListSources(Guid projectId, IMetadataStore metadataStore, CancellationToken ct)
    {
        if (await metadataStore.GetProjectAsync(projectId, ct) is null)
            return Results.NotFound();

        return Results.Ok(await metadataStore.ListSourcesAsync(projectId, ct));
    }

    private static async Task<IResult> CreateBatch(
        Guid projectId,
        Guid sourceId,
        CreateBatchRequest request,
        IMetadataStore metadataStore,
        CancellationToken ct)
    {
        try
        {
            var batch = await metadataStore.CreateIngestBatchAsync(
                projectId,
                sourceId,
                request.JobId,
                request.RecordCount,
                DateTimeOffset.UtcNow,
                ct);
            return Results.Created($"/projects/{projectId}/batches/{batch.Id}", batch);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith("Project not found", StringComparison.Ordinal) ||
            ex.Message.StartsWith("Source not found", StringComparison.Ordinal))
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> ListBatches(Guid projectId, IMetadataStore metadataStore, CancellationToken ct)
    {
        if (await metadataStore.GetProjectAsync(projectId, ct) is null)
            return Results.NotFound();

        return Results.Ok(await metadataStore.ListIngestBatchesAsync(projectId, ct));
    }

    private sealed record CreateProjectRequest(string Name, string ContentType, MergeConfiguration? MergeConfiguration);
    private sealed record UpdateMergePolicyRequest(MergeConfiguration? MergeConfiguration);
    private sealed record CreateSourceRequest(string Name);
    private sealed record CreateBatchRequest(Guid? JobId, int RecordCount);
}
