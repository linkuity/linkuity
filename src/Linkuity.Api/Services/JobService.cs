using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;
using Linkuity.Pipeline;

namespace Linkuity.Api.Services;

public class JobService
{
    private const long MaxUploadBytes = 50L * 1024 * 1024;

    private readonly IBlobStore _blobs;
    private readonly CsvNormalizationService _normalization;
    private readonly IJobDispatcher _dispatcher;

    public JobService(IBlobStore blobs, CsvNormalizationService normalization, IJobDispatcher dispatcher)
    {
        _blobs = blobs;
        _normalization = normalization;
        _dispatcher = dispatcher;
    }

    public async Task<Job> CreateAsync(CreateJobRequest request, CancellationToken ct = default)
    {
        ValidateConfiguration(request.Configuration);

        var job = new Job
        {
            Id = Guid.NewGuid(),
            State = JobState.Open,
            CreatedAt = DateTimeOffset.UtcNow,
            Configuration = request.Configuration,
            AutoStart = request.AutoStart,
            MergeConfiguration = request.MergeConfiguration
        };
        await _blobs.WriteJsonAsync(MetadataPath(job.Id), job, ct);
        return job;
    }

    private static void ValidateConfiguration(MatchConfiguration config)
    {
        if (!config.Fields.Any(f => f.ParticipatesInMatching))
            throw new ArgumentException(
                "MatchConfiguration must declare at least one field with participatesInMatching=true.",
                nameof(config));

        var duplicates = config.Fields
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        if (duplicates.Length > 0)
            throw new ArgumentException(
                $"MatchConfiguration field names must be unique. Duplicates: {string.Join(", ", duplicates)}.",
                nameof(config));

        var badSourceField = config.Fields.FirstOrDefault(f =>
            f.SemanticType == SemanticFieldType.SourceIdentifier && f.ParticipatesInMatching);
        if (badSourceField is not null)
            throw new ArgumentException(
                $"Field '{badSourceField.Name}' has semanticType=source_identifier; it must declare participatesInMatching=false.",
                nameof(config));
    }

    public async Task StartUploadAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await RequireJobAsync(jobId, ct);
        if (job.State != JobState.Open)
            throw new InvalidOperationException($"Cannot start upload: expected Open, got {job.State}.");
        job.State = JobState.Ingesting;
        await _blobs.WriteJsonAsync(MetadataPath(jobId), job, ct);
    }

    public async Task StoreDataAsync(Guid jobId, Stream data, CancellationToken ct = default)
    {
        var job = await RequireJobAsync(jobId, ct);
        if (job.State != JobState.Ingesting)
            throw new InvalidOperationException($"Cannot store data: expected Ingesting, got {job.State}.");
        if (data.CanSeek && data.Length > MaxUploadBytes)
            throw new InvalidOperationException("Upload exceeds 50 MB limit.");
        await _blobs.UploadAsync(DataPath(jobId), data, "text/csv", ct);
    }

    public async Task<Job> CompleteUploadAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await RequireJobAsync(jobId, ct);
        if (job.State != JobState.Ingesting)
            throw new InvalidOperationException($"Cannot complete upload: expected Ingesting, got {job.State}.");
        job.State = JobState.UploadComplete;
        await _blobs.WriteJsonAsync(MetadataPath(jobId), job, ct);
        if (job.AutoStart)
            return await StartProcessingAsync(jobId, ct);
        return job;
    }

    public async Task<Job> StartProcessingAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await RequireJobAsync(jobId, ct);
        if (job.State != JobState.UploadComplete)
            throw new InvalidOperationException($"Cannot start processing: expected UploadComplete, got {job.State}.");
        job.State = JobState.Processing;
        // Write state=Processing before normalization so a crash leaves an observable job state.
        await _blobs.WriteJsonAsync(MetadataPath(jobId), job, ct);
        job.RecordCount = await _normalization.NormalizeAsync(jobId, job.Configuration, ct);
        // Write RecordCount after normalization and before dispatch.
        await _blobs.WriteJsonAsync(MetadataPath(jobId), job, ct);
        await _dispatcher.DispatchAsync(job, ct);
        return job;
    }

    public Task<Job?> GetAsync(Guid jobId, CancellationToken ct = default)
        => _blobs.ReadJsonAsync<Job>(MetadataPath(jobId), ct);

    public async Task<GoldenRecordsResult> OpenGoldenRecordsAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await GetAsync(jobId, ct);
        if (job is null) return new GoldenRecordsResult.JobNotFound();
        if (job.State != JobState.Complete) return new GoldenRecordsResult.NotReady(job.State);
        var stream = await _blobs.DownloadAsync(GoldenRecordsPath(jobId), ct);
        return new GoldenRecordsResult.Ready(stream);
    }

    private async Task<Job> RequireJobAsync(Guid jobId, CancellationToken ct)
        => await GetAsync(jobId, ct) ?? throw new InvalidOperationException($"Job {jobId} not found.");

    private static string MetadataPath(Guid jobId) => $"{jobId}/metadata.json";
    private static string DataPath(Guid jobId) => $"{jobId}/input.csv";
    private static string GoldenRecordsPath(Guid jobId) => $"{jobId}/golden_records.csv";
}

public abstract record GoldenRecordsResult
{
    public sealed record JobNotFound : GoldenRecordsResult;
    public sealed record NotReady(JobState State) : GoldenRecordsResult;
    public sealed record Ready(Stream Content) : GoldenRecordsResult;
}
