using Linkuity.Api.Services;
using Linkuity.Core.Models;
using Linkuity.Core.Validation;

namespace Linkuity.Api.Endpoints;

public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        app.MapPost("/jobs", CreateJob);
        app.MapPost("/jobs/{id}/upload", UploadData).DisableAntiforgery();
        app.MapPost("/jobs/{id}/upload-complete", CompleteUpload);
        app.MapPost("/jobs/{id}/start", StartProcessing);
        app.MapGet("/jobs/{id}", GetJob);
        app.MapGet("/jobs/{id}/golden-records", GetGoldenRecords);
        app.MapGet("/jobs/{id}/neo4j-export", GetNeo4jExport);
    }

    private static async Task<IResult> CreateJob(
        CreateJobRequest request,
        JobService service,
        CancellationToken ct)
    {
        if (MatchConfigurationValidator.Validate(request.Configuration) is ValidationResult.InvalidContentType invalid)
            return Results.BadRequest(
                $"contentType must be one of: {string.Join(", ", invalid.Accepted)}");

        var job = await service.CreateAsync(request, ct);
        return Results.Created($"/jobs/{job.Id}", job);
    }

    private static async Task<IResult> UploadData(
        Guid id,
        IFormFile file,
        JobService service,
        CancellationToken ct)
    {
        if (file.ContentType is not "text/csv")
            return Results.BadRequest("File must be text/csv.");

        const long MaxBytes = 50L * 1024 * 1024;
        if (file.Length > MaxBytes)
            return Results.BadRequest("Upload exceeds 50 MB limit.");

        try
        {
            await service.StartUploadAsync(id, ct);
            using var stream = file.OpenReadStream();
            await service.StoreDataAsync(id, stream, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(ex.Message);
        }

        return Results.Ok();
    }

    private static async Task<IResult> CompleteUpload(
        Guid id,
        JobService service,
        CancellationToken ct)
    {
        try
        {
            var job = await service.CompleteUploadAsync(id, ct);
            return Results.Ok(job);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(ex.Message);
        }
    }

    private static async Task<IResult> StartProcessing(
        Guid id,
        JobService service,
        CancellationToken ct)
    {
        try
        {
            var job = await service.StartProcessingAsync(id, ct);
            return Results.Ok(job);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(ex.Message);
        }
    }

    private static async Task<IResult> GetJob(
        Guid id,
        JobService service,
        CancellationToken ct)
    {
        var job = await service.GetAsync(id, ct);
        return job is null ? Results.NotFound() : Results.Ok(job);
    }

    private static async Task<IResult> GetGoldenRecords(
        Guid id,
        JobService service,
        CancellationToken ct)
    {
        var result = await service.OpenGoldenRecordsAsync(id, ct);
        return result switch
        {
            GoldenRecordsResult.JobNotFound => Results.NotFound(),
            GoldenRecordsResult.NotReady nr when nr.State == JobState.Failed
                => Results.Conflict(new { error = "Job failed; no results available", state = nr.State.ToString() }),
            GoldenRecordsResult.NotReady nr
                => Results.Conflict(new { error = $"Job is in state {nr.State}; results not yet available", state = nr.State.ToString() }),
            GoldenRecordsResult.Ready r
                => Results.Stream(r.Content, "text/csv", $"golden-records-{id}.csv"),
            _ => Results.StatusCode(500)
        };
    }

    private static async Task<IResult> GetNeo4jExport(
        Guid id,
        Neo4jExportService service,
        CancellationToken ct)
    {
        var result = await service.OpenZipAsync(id, ct);
        return result switch
        {
            Neo4jExportResult.JobNotFound => Results.NotFound(),
            Neo4jExportResult.NotReady nr when nr.State == JobState.Failed
                => Results.Conflict(new { error = "Job failed; no results available", state = nr.State.ToString() }),
            Neo4jExportResult.NotReady nr
                => Results.Conflict(new { error = $"Job is in state {nr.State}; results not yet available", state = nr.State.ToString() }),
            Neo4jExportResult.Ready r
                => Results.Stream(r.Content, "application/zip", $"neo4j-export-{id}.zip"),
            _ => Results.StatusCode(500)
        };
    }
}
