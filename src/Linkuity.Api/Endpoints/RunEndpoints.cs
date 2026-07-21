using System.Text.Json;
using System.Text.Json.Serialization;
using Linkuity.Core.Models;
using Linkuity.Core.Validation;
using Linkuity.Pipeline;

namespace Linkuity.Api.Endpoints;

public static class RunEndpoints
{
    // Measured cap (Task 8): within-batch matching in BatchMatchingService is O(n^2)-ish
    // (every record is resolved against the full "others" list). A local probe of
    // BatchRunService.RunAsync at synthetic person-record sizes showed run time scaling
    // roughly with rows^2 and worsening (per byte) for narrower CSV schemas, since more
    // rows fit in the same byte budget:
    //   9-field rows:  5,000 rows / ~598 KB  -> ~22.7s   10,000 rows / ~1.20 MB -> ~96.0s
    //   3-field rows:  5,000 rows / ~287 KB  -> ~8.5s     8,000 rows / ~460 KB  -> ~22.1s
    // The narrower (worse-per-byte) case is the binding one: it projects to ~17s at 400 KB
    // and would exceed the 20-25s synchronous budget by ~500 KB. 400 KiB keeps a synchronous
    // /run comfortably under 20s even for lean CSV schemas; larger inputs should use the CLI.
    public const long MaxInputBytes = 400L * 1024;

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
                $"Input exceeds the {MaxInputBytes / 1024} KB synchronous limit. Use the CLI for larger inputs.");

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
