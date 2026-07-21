using System.Text.Json;
using System.Text.Json.Serialization;
using Linkuity.Core.Models;
using Linkuity.Core.Validation;
using Linkuity.Pipeline;

namespace Linkuity.Api.Endpoints;

public static class RunEndpoints
{
    // Initial conservative cap; finalize via the benchmark in Task 8.
    public const long MaxInputBytes = 10L * 1024 * 1024;

    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static void MapRunEndpoints(this WebApplication app)
        => app.MapPost("/run", Run).DisableAntiforgery();

    private static async Task<IResult> Run(
        IFormFile file,
        [Microsoft.AspNetCore.Mvc.FromForm] string config,
        BatchRunService runService,
        Linkuity.Core.Interfaces.IArtifactStore store,
        CancellationToken ct)
    {
        if (file.ContentType is not "text/csv")
            return Results.BadRequest("File must be text/csv.");
        if (file.Length > MaxInputBytes)
            return Results.BadRequest(
                $"Input exceeds the {MaxInputBytes / (1024 * 1024)} MB synchronous limit. Use the CLI for larger inputs.");

        CreateJobRequest request;
        try
        {
            request = JsonSerializer.Deserialize<CreateJobRequest>(config, ConfigJson)
                ?? throw new JsonException("config is empty");
        }
        catch (JsonException ex)
        {
            return Results.BadRequest($"Invalid config JSON: {ex.Message}");
        }

        if (MatchConfigurationValidator.Validate(request.Configuration) is ValidationResult.InvalidContentType invalid)
            return Results.BadRequest($"contentType must be one of: {string.Join(", ", invalid.Accepted)}");

        BatchRunResult result;
        await using (var input = file.OpenReadStream())
            result = await runService.RunAsync(request, input, ct);

        var golden = await store.DownloadAsync($"{result.JobId}/golden_records.csv", ct);
        return Results.Stream(golden, "text/csv", "golden-records.csv");
    }
}
