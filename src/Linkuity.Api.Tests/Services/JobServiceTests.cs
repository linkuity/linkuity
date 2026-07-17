using System.Text;
using Linkuity.Api.Services;
using Linkuity.Api.Tests.TestDoubles;
using Linkuity.Core.Models;

namespace Linkuity.Api.Tests.Services;

public class JobServiceTests
{
    private static readonly MatchConfiguration DefaultConfig = new()
    {
        ContentType = "person",
        Fields = new[] { new Field { Name = "email", SemanticType = SemanticFieldType.Email } }
    };

    private static (JobService service, InMemoryBlobStore blobs, CapturingJobDispatcher dispatcher) Build()
    {
        var blobs = new InMemoryBlobStore();
        var normalization = new CsvNormalizationService(blobs);
        var dispatcher = new CapturingJobDispatcher();
        return (new JobService(blobs, normalization, dispatcher), blobs, dispatcher);
    }

    [Fact]
    public async Task CreateAsync_PersistedWithOpenState_AndMetadataBlobExists()
    {
        var (service, blobs, _) = Build();

        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig });

        Assert.Equal(JobState.Open, job.State);
        Assert.True(await blobs.ExistsAsync($"{job.Id}/metadata.json"));
    }

    [Fact]
    public async Task StartUploadAsync_TransitionsFromOpenToIngesting()
    {
        var (service, _, _) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig });

        await service.StartUploadAsync(job.Id);

        var updated = await service.GetAsync(job.Id);
        Assert.Equal(JobState.Ingesting, updated!.State);
    }

    [Fact]
    public async Task StartUploadAsync_OnNonOpenJob_ThrowsInvalidOperationException()
    {
        var (service, _, _) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig });
        await service.StartUploadAsync(job.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartUploadAsync(job.Id));
    }

    [Fact]
    public async Task StoreDataAsync_WritesInputCsvBlob()
    {
        var (service, blobs, _) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig });
        await service.StartUploadAsync(job.Id);

        using var stream = new MemoryStream("id,email\n1,a@b.com"u8.ToArray());
        await service.StoreDataAsync(job.Id, stream);

        Assert.True(await blobs.ExistsAsync($"{job.Id}/input.csv"));
    }

    [Fact]
    public async Task StoreDataAsync_StreamOver50MB_ThrowsBeforeWriting()
    {
        var (service, blobs, _) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig });
        await service.StartUploadAsync(job.Id);

        var oversize = new FakeStream(51L * 1024 * 1024);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StoreDataAsync(job.Id, oversize));
        Assert.False(await blobs.ExistsAsync($"{job.Id}/input.csv"));
    }

    [Fact]
    public async Task CompleteUploadAsync_AutoStartTrue_TransitionsToProcessingAndDispatches()
    {
        var (service, _, dispatcher) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig, AutoStart = true });
        await service.StartUploadAsync(job.Id);
        using var stream = new MemoryStream("id,email\n1,a@b.com"u8.ToArray());
        await service.StoreDataAsync(job.Id, stream);

        await service.CompleteUploadAsync(job.Id);

        var updated = await service.GetAsync(job.Id);
        Assert.Equal(JobState.Processing, updated!.State);
        Assert.Contains(job.Id, dispatcher.Dispatched);
    }

    [Fact]
    public async Task CompleteUploadAsync_AutoStartFalse_TransitionsToUploadCompleteAndDoesNotDispatch()
    {
        var (service, _, dispatcher) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig, AutoStart = false });
        await service.StartUploadAsync(job.Id);
        using var stream = new MemoryStream("id,email\n1,a@b.com"u8.ToArray());
        await service.StoreDataAsync(job.Id, stream);

        await service.CompleteUploadAsync(job.Id);

        var updated = await service.GetAsync(job.Id);
        Assert.Equal(JobState.UploadComplete, updated!.State);
        Assert.Empty(dispatcher.Dispatched);
    }

    [Fact]
    public async Task StartProcessingAsync_OnUploadCompleteJob_TransitionsToProcessingAndDispatches()
    {
        var (service, _, dispatcher) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig, AutoStart = false });
        await service.StartUploadAsync(job.Id);
        using var stream = new MemoryStream("id,email\n1,a@b.com"u8.ToArray());
        await service.StoreDataAsync(job.Id, stream);
        await service.CompleteUploadAsync(job.Id);

        await service.StartProcessingAsync(job.Id);

        var updated = await service.GetAsync(job.Id);
        Assert.Equal(JobState.Processing, updated!.State);
        Assert.Contains(job.Id, dispatcher.Dispatched);
    }

    [Fact]
    public async Task StartProcessingAsync_OnNonUploadCompleteJob_ThrowsInvalidOperationException()
    {
        var (service, _, _) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartProcessingAsync(job.Id));
    }

    [Fact]
    public async Task GetAsync_ExistingJob_ReturnsJob()
    {
        var (service, _, _) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig });

        var result = await service.GetAsync(job.Id);

        Assert.NotNull(result);
        Assert.Equal(job.Id, result.Id);
    }

    [Fact]
    public async Task GetAsync_MissingJob_ReturnsNull()
    {
        var (service, _, _) = Build();

        var result = await service.GetAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task StartProcessingAsync_NormalizedCsvBlobExists()
    {
        var (service, blobs, _) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig, AutoStart = false });
        await service.StartUploadAsync(job.Id);
        using var stream = new MemoryStream("id,email\n1,a@b.com"u8.ToArray());
        await service.StoreDataAsync(job.Id, stream);
        await service.CompleteUploadAsync(job.Id);

        await service.StartProcessingAsync(job.Id);

        Assert.True(await blobs.ExistsAsync($"{job.Id}/normalized.csv"));
    }

    [Fact]
    public async Task StartProcessingAsync_UnparseablePhoneValue_JobReachesProcessingAndDispatchCalled()
    {
        var config = new MatchConfiguration
        {
            ContentType = "person",
            Fields = [new Field { Name = "phone", SemanticType = SemanticFieldType.Phone }]
        };
        var (service, _, dispatcher) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = config, AutoStart = false });
        await service.StartUploadAsync(job.Id);
        using var stream = new MemoryStream("id,phone\n1,not-a-phone"u8.ToArray());
        await service.StoreDataAsync(job.Id, stream);
        await service.CompleteUploadAsync(job.Id);

        await service.StartProcessingAsync(job.Id);

        var updated = await service.GetAsync(job.Id);
        Assert.Equal(JobState.Processing, updated!.State);
        Assert.Contains(job.Id, dispatcher.Dispatched);
    }

    [Fact]
    public async Task CreateAsync_WithMergeConfiguration_PersistsMergeConfiguration()
    {
        var (service, _, _) = Build();
        var mergeConfig = new MergeConfiguration
        {
            MergeFields = [new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing"] }]
        };

        var job = await service.CreateAsync(new CreateJobRequest
        {
            Configuration = DefaultConfig,
            MergeConfiguration = mergeConfig
        });

        var stored = await service.GetAsync(job.Id);
        Assert.NotNull(stored!.MergeConfiguration);
        Assert.Single(stored.MergeConfiguration!.MergeFields);
        Assert.Equal("email", stored.MergeConfiguration!.MergeFields[0].FieldName);
        Assert.Equal(new[] { "CRM", "Marketing" }, stored.MergeConfiguration!.MergeFields[0].SourcePriority);
    }

    [Fact]
    public async Task StartProcessingAsync_StoresRecordCountOnJob()
    {
        var (service, _, _) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig, AutoStart = false });
        await service.StartUploadAsync(job.Id);
        using var stream = new MemoryStream("id,email\n1,a@b.com\n2,c@d.com\n3,e@f.com"u8.ToArray());
        await service.StoreDataAsync(job.Id, stream);
        await service.CompleteUploadAsync(job.Id);

        await service.StartProcessingAsync(job.Id);

        var updated = await service.GetAsync(job.Id);
        Assert.Equal(3, updated!.RecordCount);
    }

    [Fact]
    public async Task OpenGoldenRecordsAsync_MissingJob_ReturnsJobNotFound()
    {
        var (service, _, _) = Build();

        var result = await service.OpenGoldenRecordsAsync(Guid.NewGuid());

        Assert.IsType<GoldenRecordsResult.JobNotFound>(result);
    }

    [Fact]
    public async Task OpenGoldenRecordsAsync_JobNotComplete_ReturnsNotReadyWithState()
    {
        var (service, blobs, _) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig });
        job.State = JobState.Processing;
        await blobs.WriteJsonAsync($"{job.Id}/metadata.json", job);

        var result = await service.OpenGoldenRecordsAsync(job.Id);

        var notReady = Assert.IsType<GoldenRecordsResult.NotReady>(result);
        Assert.Equal(JobState.Processing, notReady.State);
    }

    [Fact]
    public async Task OpenGoldenRecordsAsync_JobFailed_ReturnsNotReadyWithFailedState()
    {
        var (service, blobs, _) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig });
        job.State = JobState.Failed;
        await blobs.WriteJsonAsync($"{job.Id}/metadata.json", job);

        var result = await service.OpenGoldenRecordsAsync(job.Id);

        var notReady = Assert.IsType<GoldenRecordsResult.NotReady>(result);
        Assert.Equal(JobState.Failed, notReady.State);
    }

    [Fact]
    public async Task OpenGoldenRecordsAsync_JobComplete_ReturnsReadyWithBlobContents()
    {
        var (service, blobs, _) = Build();
        var job = await service.CreateAsync(new CreateJobRequest { Configuration = DefaultConfig });
        job.State = JobState.Complete;
        await blobs.WriteJsonAsync($"{job.Id}/metadata.json", job);
        var csvBytes = "cluster_id,record_count,member_ids,email\nabc,1,1,a@b.com\n"u8.ToArray();
        using (var seed = new MemoryStream(csvBytes))
            await blobs.UploadAsync($"{job.Id}/golden_records.csv", seed, "text/csv");

        var result = await service.OpenGoldenRecordsAsync(job.Id);

        var ready = Assert.IsType<GoldenRecordsResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        Assert.Equal(Encoding.UTF8.GetString(csvBytes), await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task CreateAsync_ConfigWithNoMatchingFields_ThrowsArgumentException()
    {
        var (service, _, _) = Build();
        var config = new MatchConfiguration
        {
            ContentType = "person",
            Fields = [
                new Field { Name = "email", SemanticType = SemanticFieldType.Email, ParticipatesInMatching = false }
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateAsync(new CreateJobRequest { Configuration = config }));
    }

    [Fact]
    public async Task CreateAsync_ConfigWithDuplicateFieldNames_ThrowsArgumentException()
    {
        var (service, _, _) = Build();
        var config = new MatchConfiguration
        {
            ContentType = "person",
            Fields = [
                new Field { Name = "email", SemanticType = SemanticFieldType.Email },
                new Field { Name = "email", SemanticType = SemanticFieldType.FirstName }
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateAsync(new CreateJobRequest { Configuration = config }));
    }

    [Fact]
    public async Task CreateAsync_SourceIdentifierWithParticipatesInMatchingTrue_ThrowsArgumentException()
    {
        var (service, _, _) = Build();
        var config = new MatchConfiguration
        {
            ContentType = "person",
            Fields = [
                new Field { Name = "email", SemanticType = SemanticFieldType.Email },
                new Field { Name = "source", SemanticType = SemanticFieldType.SourceIdentifier, ParticipatesInMatching = true }
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateAsync(new CreateJobRequest { Configuration = config }));
    }
}

internal sealed class FakeStream : Stream
{
    public FakeStream(long length) => Length = length;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }
    public override long Position { get; set; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => 0;
    public override long Seek(long offset, SeekOrigin origin) => Position;
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
