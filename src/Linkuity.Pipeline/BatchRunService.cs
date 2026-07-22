using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline;

public sealed record BatchRunResult(Guid JobId);

public sealed class BatchRunService
{
    private readonly CsvNormalizationService _normalization;
    private readonly BatchMatchingService _matching;
    private readonly PostProcessingService _postProcessing;
    private readonly IArtifactStore _store;

    public BatchRunService(
        CsvNormalizationService normalization,
        BatchMatchingService matching,
        PostProcessingService postProcessing,
        IArtifactStore store)
    {
        _normalization = normalization;
        _matching = matching;
        _postProcessing = postProcessing;
        _store = store;
    }

    public async Task<BatchRunResult> RunAsync(
        MatchingProfile profile, MergeConfiguration? merge, Stream inputCsv, CancellationToken ct)
    {
        var jobId = Guid.NewGuid();
        var job = new Job
        {
            Id = jobId,
            State = JobState.Ingesting,
            CreatedAt = DateTimeOffset.UtcNow,
            AutoStart = true
        };
        var metadataPath = $"{jobId}/metadata.json";

        await _store.WriteJsonAsync(metadataPath, job, ct);
        await _store.UploadAsync($"{jobId}/input.csv", inputCsv, "text/csv", ct);

        job.State = JobState.Processing;
        await _store.WriteJsonAsync(metadataPath, job, ct);
        job.RecordCount = await _normalization.NormalizeAsync(jobId, profile, ct);
        await _store.WriteJsonAsync(metadataPath, job, ct);

        await _matching.RunAsync(jobId.ToString(), profile, ct);
        await _postProcessing.ProcessAsync(jobId.ToString(), profile, merge, ct);

        return new BatchRunResult(jobId);
    }
}
