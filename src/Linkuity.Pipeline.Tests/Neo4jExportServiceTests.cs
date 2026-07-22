using System.IO.Compression;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline.Tests;

public class Neo4jExportServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"linkuity-neo4jexport-{Guid.NewGuid():N}");

    private static readonly MatchingProfile DefaultProfile = BuildProfile("person",
        new ProfileField { Name = "email", SemanticType = SemanticFieldType.Email, Roles = FieldRole.Matchable });

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private (Neo4jExportService service, FileSystemArtifactStore blobs) Build()
    {
        var blobs = new FileSystemArtifactStore(new FileSystemArtifactStoreOptions { RootPath = _rootPath });
        return (new Neo4jExportService(blobs), blobs);
    }

    private static async Task SeedJobAsync(FileSystemArtifactStore blobs, Guid jobId, JobState state)
    {
        var job = new Job
        {
            Id = jobId,
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
            AutoStart = false
        };
        await blobs.WriteJsonAsync($"{jobId}/metadata.json", job);
    }

    private static MatchingProfile BuildProfile(string contentType, params ProfileField[] fields) => new()
    {
        ContentType = contentType,
        Fields = fields,
        NormalizationStrategy = "identity",
        BlockingStrategies = ["exact-value"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.90,
        ReviewThreshold = 0.75
    };

    [Fact]
    public async Task OpenAsync_MissingJob_ReturnsJobNotFound()
    {
        var (service, _) = Build();

        var result = await service.OpenAsync(Guid.NewGuid(), "entities", DefaultProfile);

        Assert.IsType<Neo4jFileResult.JobNotFound>(result);
    }

    [Fact]
    public async Task OpenAsync_JobNotComplete_ReturnsNotReady()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await SeedJobAsync(blobs, jobId, JobState.Processing);

        var result = await service.OpenAsync(jobId, "entities", DefaultProfile);

        var notReady = Assert.IsType<Neo4jFileResult.NotReady>(result);
        Assert.Equal(JobState.Processing, notReady.State);
    }

    [Fact]
    public async Task OpenAsync_UnknownFile_ReturnsUnknownFile()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await SeedJobAsync(blobs, jobId, JobState.Complete);

        var result = await service.OpenAsync(jobId, "bogus-file", DefaultProfile);

        Assert.IsType<Neo4jFileResult.UnknownFile>(result);
    }

    [Fact]
    public async Task OpenAsync_Entities_PassesThroughNormalizedCsv()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        var csv = "id,first_name,email\n1,Alice,a@x.com\n2,Bob,b@x.com\n"u8.ToArray();
        using (var seed = new MemoryStream(csv))
            await blobs.UploadAsync($"{jobId}/normalized.csv", seed, "text/csv");

        var result = await service.OpenAsync(jobId, "entities", DefaultProfile);

        var ready = Assert.IsType<Neo4jFileResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(csv), await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task OpenAsync_MatchedTo_PassesThroughMatchesCsv()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        var csv = "left_id,right_id,similarity,fuzzy_similarity\n1,2,0.95,0.91\n3,4,0.88,\n"u8.ToArray();
        using (var seed = new MemoryStream(csv))
            await blobs.UploadAsync($"{jobId}/matches.csv", seed, "text/csv");

        var result = await service.OpenAsync(jobId, "matched-to", DefaultProfile);

        var ready = Assert.IsType<Neo4jFileResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(csv), await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task OpenAsync_GoldenRecords_DropsMemberIdsColumn()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        var csv = "cluster_id,record_count,member_ids,email,first_name\n"
                + "abc,2,1|2,a@x.com,Alice\n"
                + "def,1,3,b@x.com,Bob\n";
        using (var seed = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv)))
            await blobs.UploadAsync($"{jobId}/golden_records.csv", seed, "text/csv");

        var result = await service.OpenAsync(jobId, "golden-records", DefaultProfile);

        var ready = Assert.IsType<Neo4jFileResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        var output = await reader.ReadToEndAsync();
        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("cluster_id,record_count,email,first_name", lines[0]);
        Assert.Equal("abc,2,a@x.com,Alice", lines[1]);
        Assert.Equal("def,1,b@x.com,Bob", lines[2]);
    }

    [Fact]
    public async Task OpenAsync_ResolvedTo_ExplodesMemberIds()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        var csv = "cluster_id,record_count,member_ids,email\n"
                + "abc,3,1|2|3,a@x.com\n"
                + "def,1,4,b@x.com\n";
        using (var seed = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv)))
            await blobs.UploadAsync($"{jobId}/golden_records.csv", seed, "text/csv");

        var result = await service.OpenAsync(jobId, "resolved-to", DefaultProfile);

        var ready = Assert.IsType<Neo4jFileResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        var output = await reader.ReadToEndAsync();
        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("entity_id,cluster_id,cluster_size", lines[0]);
        Assert.Equal("1,abc,3", lines[1]);
        Assert.Equal("2,abc,3", lines[2]);
        Assert.Equal("3,abc,3", lines[3]);
        Assert.Equal("4,def,1", lines[4]);
    }

    [Fact]
    public async Task OpenAsync_Emails_ReturnsDistinctNonEmptyValues()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        var csv = "id,email\n1,a@x.com\n2,b@x.com\n3,a@x.com\n4,\n"u8.ToArray();
        using (var seed = new MemoryStream(csv))
            await blobs.UploadAsync($"{jobId}/normalized.csv", seed, "text/csv");

        var result = await service.OpenAsync(jobId, "emails", DefaultProfile);

        var ready = Assert.IsType<Neo4jFileResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        var output = await reader.ReadToEndAsync();
        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("value", lines[0]);
        Assert.Contains("a@x.com", lines[1..]);
        Assert.Contains("b@x.com", lines[1..]);
        Assert.Equal(3, lines.Length); // header + 2 distinct values
    }

    [Fact]
    public async Task OpenAsync_Emails_NoEmailFieldConfigured_ReturnsHeaderOnly()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        var profileWithoutEmail = BuildProfile("person",
            new ProfileField { Name = "phone", SemanticType = SemanticFieldType.Phone, Roles = FieldRole.Matchable });
        await SeedJobAsync(blobs, jobId, JobState.Complete);

        var result = await service.OpenAsync(jobId, "emails", profileWithoutEmail);

        var ready = Assert.IsType<Neo4jFileResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        var output = await reader.ReadToEndAsync();
        Assert.Equal("value\r\n", output);
    }

    [Fact]
    public async Task OpenAsync_HasEmail_EmitsOneRowPerNonEmptyEntity()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        var csv = "id,email\n1,a@x.com\n2,\n3,c@x.com\n"u8.ToArray();
        using (var seed = new MemoryStream(csv))
            await blobs.UploadAsync($"{jobId}/normalized.csv", seed, "text/csv");

        var result = await service.OpenAsync(jobId, "has-email", DefaultProfile);

        var ready = Assert.IsType<Neo4jFileResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        var output = await reader.ReadToEndAsync();
        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("entity_id,email_value", lines[0]);
        Assert.Equal("1,a@x.com", lines[1]);
        Assert.Equal("3,c@x.com", lines[2]);
        Assert.Equal(3, lines.Length); // entity 2 (empty email) skipped
    }

    [Fact]
    public async Task OpenAsync_Phones_ReturnsDistinctNonEmptyValues()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        var profile = BuildProfile("person",
            new ProfileField { Name = "phone", SemanticType = SemanticFieldType.Phone, Roles = FieldRole.Matchable });
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        var csv = "id,phone\n1,+15551234567\n2,+15559999999\n3,+15551234567\n"u8.ToArray();
        using (var seed = new MemoryStream(csv))
            await blobs.UploadAsync($"{jobId}/normalized.csv", seed, "text/csv");

        var result = await service.OpenAsync(jobId, "phones", profile);

        var ready = Assert.IsType<Neo4jFileResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        var output = await reader.ReadToEndAsync();
        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("value", lines[0]);
        Assert.Equal(3, lines.Length); // header + 2 distinct
    }

    [Fact]
    public async Task OpenAsync_HasPhone_EmitsOneRowPerNonEmptyEntity()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        var profile = BuildProfile("person",
            new ProfileField { Name = "phone", SemanticType = SemanticFieldType.Phone, Roles = FieldRole.Matchable });
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        var csv = "id,phone\n1,+15551234567\n2,\n"u8.ToArray();
        using (var seed = new MemoryStream(csv))
            await blobs.UploadAsync($"{jobId}/normalized.csv", seed, "text/csv");

        var result = await service.OpenAsync(jobId, "has-phone", profile);

        var ready = Assert.IsType<Neo4jFileResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        var output = await reader.ReadToEndAsync();
        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("entity_id,phone_value", lines[0]);
        Assert.Equal("1,+15551234567", lines[1]);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task OpenAsync_Sources_NotConfigured_ReturnsHeaderOnly()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await SeedJobAsync(blobs, jobId, JobState.Complete); // DefaultProfile has no SourceIdentifier

        var result = await service.OpenAsync(jobId, "sources", DefaultProfile);

        var ready = Assert.IsType<Neo4jFileResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        var output = await reader.ReadToEndAsync();
        Assert.Equal("name\r\n", output);
    }

    [Fact]
    public async Task OpenAsync_Sources_Configured_ReturnsDistinctValues()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        var profile = BuildProfile("person",
            new ProfileField { Name = "email", SemanticType = SemanticFieldType.Email, Roles = FieldRole.Matchable },
            new ProfileField { Name = "source", SemanticType = SemanticFieldType.SourceIdentifier, Roles = FieldRole.None });
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        var csv = "id,email,source\n1,a@x.com,CRM\n2,b@x.com,Marketing\n3,c@x.com,CRM\n"u8.ToArray();
        using (var seed = new MemoryStream(csv))
            await blobs.UploadAsync($"{jobId}/normalized.csv", seed, "text/csv");

        var result = await service.OpenAsync(jobId, "sources", profile);

        var ready = Assert.IsType<Neo4jFileResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        var output = await reader.ReadToEndAsync();
        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("name", lines[0]);
        Assert.Equal(3, lines.Length); // header + CRM + Marketing
    }

    [Fact]
    public async Task OpenAsync_FromSource_EmitsOneRowPerNonEmptyEntity()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        var profile = BuildProfile("person",
            new ProfileField { Name = "email", SemanticType = SemanticFieldType.Email, Roles = FieldRole.Matchable },
            new ProfileField { Name = "source", SemanticType = SemanticFieldType.SourceIdentifier, Roles = FieldRole.None });
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        var csv = "id,email,source\n1,a@x.com,CRM\n2,b@x.com,\n"u8.ToArray();
        using (var seed = new MemoryStream(csv))
            await blobs.UploadAsync($"{jobId}/normalized.csv", seed, "text/csv");

        var result = await service.OpenAsync(jobId, "from-source", profile);

        var ready = Assert.IsType<Neo4jFileResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        var output = await reader.ReadToEndAsync();
        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("entity_id,source_name", lines[0]);
        Assert.Equal("1,CRM", lines[1]);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task OpenZipAsync_MissingJob_ReturnsJobNotFound()
    {
        var (service, _) = Build();

        var result = await service.OpenZipAsync(Guid.NewGuid(), DefaultProfile);

        Assert.IsType<Neo4jExportResult.JobNotFound>(result);
    }

    [Fact]
    public async Task OpenZipAsync_JobNotComplete_ReturnsNotReady()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await SeedJobAsync(blobs, jobId, JobState.Processing);

        var result = await service.OpenZipAsync(jobId, DefaultProfile);

        var notReady = Assert.IsType<Neo4jExportResult.NotReady>(result);
        Assert.Equal(JobState.Processing, notReady.State);
    }

    [Fact]
    public async Task OpenZipAsync_JobComplete_ReturnsZipWithAllElevenEntries()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await SeedJobAsync(blobs, jobId, JobState.Complete);

        var normalized = "id,email\n1,a@x.com\n"u8.ToArray();
        using (var s = new MemoryStream(normalized))
            await blobs.UploadAsync($"{jobId}/normalized.csv", s, "text/csv");
        var matches = "left_id,right_id,similarity,fuzzy_similarity\n"u8.ToArray();
        using (var s = new MemoryStream(matches))
            await blobs.UploadAsync($"{jobId}/matches.csv", s, "text/csv");
        var golden = "cluster_id,record_count,member_ids,email\nabc,1,1,a@x.com\n"u8.ToArray();
        using (var s = new MemoryStream(golden))
            await blobs.UploadAsync($"{jobId}/golden_records.csv", s, "text/csv");

        var result = await service.OpenZipAsync(jobId, DefaultProfile);

        var ready = Assert.IsType<Neo4jExportResult.Ready>(result);
        using var archive = new ZipArchive(ready.Content, ZipArchiveMode.Read);
        var entryNames = archive.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Equal(11, entryNames.Count);
        Assert.Contains("entities.csv", entryNames);
        Assert.Contains("golden-records.csv", entryNames);
        Assert.Contains("matched-to.csv", entryNames);
        Assert.Contains("resolved-to.csv", entryNames);
        Assert.Contains("sources.csv", entryNames);
        Assert.Contains("from-source.csv", entryNames);
        Assert.Contains("emails.csv", entryNames);
        Assert.Contains("has-email.csv", entryNames);
        Assert.Contains("phones.csv", entryNames);
        Assert.Contains("has-phone.csv", entryNames);
        Assert.Contains("load.cypher", entryNames);

        using (var entityReader = new StreamReader(archive.GetEntry("entities.csv")!.Open()))
            Assert.Equal("id,email\n1,a@x.com\n", await entityReader.ReadToEndAsync());

        using var cypherReader = new StreamReader(archive.GetEntry("load.cypher")!.Open());
        var cypherText = await cypherReader.ReadToEndAsync();
        Assert.Contains("LOAD CSV WITH HEADERS", cypherText);
        Assert.Contains("CREATE CONSTRAINT entity_id", cypherText);
    }

    [Fact]
    public async Task OpenAsync_Phones_PhoneFieldNonMatching_StillReturnsDistinctValues()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        var profile = BuildProfile("person",
            new ProfileField { Name = "phone", SemanticType = SemanticFieldType.Phone, Roles = FieldRole.None });
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        var csv = "id,phone\n1,+15551234567\n2,+15559999999\n"u8.ToArray();
        using (var seed = new MemoryStream(csv))
            await blobs.UploadAsync($"{jobId}/normalized.csv", seed, "text/csv");

        var result = await service.OpenAsync(jobId, "phones", profile);

        var ready = Assert.IsType<Neo4jFileResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        var output = await reader.ReadToEndAsync();
        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // header + 2 distinct
    }

    [Fact]
    public async Task OpenAsync_HasPhone_PhoneFieldNonMatching_StillEmitsRows()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        var profile = BuildProfile("person",
            new ProfileField { Name = "phone", SemanticType = SemanticFieldType.Phone, Roles = FieldRole.None });
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        var csv = "id,phone\n1,+15551234567\n2,\n"u8.ToArray();
        using (var seed = new MemoryStream(csv))
            await blobs.UploadAsync($"{jobId}/normalized.csv", seed, "text/csv");

        var result = await service.OpenAsync(jobId, "has-phone", profile);

        var ready = Assert.IsType<Neo4jFileResult.Ready>(result);
        using var reader = new StreamReader(ready.Content);
        var output = await reader.ReadToEndAsync();
        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("entity_id,phone_value", lines[0]);
        Assert.Equal("1,+15551234567", lines[1]);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task OpenZipAsync_PersonContentType_CypherUsesEntityPersonAndGoldenRecordPersonLabels()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        var profile = BuildProfile("person",
            new ProfileField { Name = "email", SemanticType = SemanticFieldType.Email, Roles = FieldRole.Matchable });
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        using (var s = new MemoryStream("id,email\n1,a@x.com\n"u8.ToArray()))
            await blobs.UploadAsync($"{jobId}/normalized.csv", s, "text/csv");
        using (var s = new MemoryStream("left_id,right_id,similarity,fuzzy_similarity\n"u8.ToArray()))
            await blobs.UploadAsync($"{jobId}/matches.csv", s, "text/csv");
        using (var s = new MemoryStream("cluster_id,record_count,member_ids,email\nabc,1,1,a@x.com\n"u8.ToArray()))
            await blobs.UploadAsync($"{jobId}/golden_records.csv", s, "text/csv");

        var result = await service.OpenZipAsync(jobId, profile);

        var ready = Assert.IsType<Neo4jExportResult.Ready>(result);
        using var archive = new ZipArchive(ready.Content, ZipArchiveMode.Read);
        using var cypherReader = new StreamReader(archive.GetEntry("load.cypher")!.Open());
        var cypherText = await cypherReader.ReadToEndAsync();
        Assert.Contains("MERGE (e:Entity:Person {id: row.id})", cypherText);
        Assert.Contains("MERGE (g:GoldenRecord:Person {cluster_id: row.cluster_id})", cypherText);
    }

    [Fact]
    public async Task OpenZipAsync_OrganizationContentType_CypherUsesEntityOrganizationAndGoldenRecordOrganizationLabels()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        var profile = BuildProfile("organization",
            new ProfileField { Name = "domain_name", SemanticType = SemanticFieldType.DomainName, Roles = FieldRole.Matchable });
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        using (var s = new MemoryStream("id,domain_name\n1,example.com\n"u8.ToArray()))
            await blobs.UploadAsync($"{jobId}/normalized.csv", s, "text/csv");
        using (var s = new MemoryStream("left_id,right_id,similarity,fuzzy_similarity\n"u8.ToArray()))
            await blobs.UploadAsync($"{jobId}/matches.csv", s, "text/csv");
        using (var s = new MemoryStream("cluster_id,record_count,member_ids,domain_name\nabc,1,1,example.com\n"u8.ToArray()))
            await blobs.UploadAsync($"{jobId}/golden_records.csv", s, "text/csv");

        var result = await service.OpenZipAsync(jobId, profile);

        var ready = Assert.IsType<Neo4jExportResult.Ready>(result);
        using var archive = new ZipArchive(ready.Content, ZipArchiveMode.Read);
        using var cypherReader = new StreamReader(archive.GetEntry("load.cypher")!.Open());
        var cypherText = await cypherReader.ReadToEndAsync();
        Assert.Contains("MERGE (e:Entity:Organization {id: row.id})", cypherText);
        Assert.Contains("MERGE (g:GoldenRecord:Organization {cluster_id: row.cluster_id})", cypherText);
    }

    [Fact]
    public async Task OpenZipAsync_UnrecognizedContentType_FallsBackToBareEntityAndGoldenRecordLabels()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        var profile = BuildProfile("spaceship",
            new ProfileField { Name = "email", SemanticType = SemanticFieldType.Email, Roles = FieldRole.Matchable });
        await SeedJobAsync(blobs, jobId, JobState.Complete);
        using (var s = new MemoryStream("id,email\n1,a@x.com\n"u8.ToArray()))
            await blobs.UploadAsync($"{jobId}/normalized.csv", s, "text/csv");
        using (var s = new MemoryStream("left_id,right_id,similarity,fuzzy_similarity\n"u8.ToArray()))
            await blobs.UploadAsync($"{jobId}/matches.csv", s, "text/csv");
        using (var s = new MemoryStream("cluster_id,record_count,member_ids,email\nabc,1,1,a@x.com\n"u8.ToArray()))
            await blobs.UploadAsync($"{jobId}/golden_records.csv", s, "text/csv");

        var result = await service.OpenZipAsync(jobId, profile);

        var ready = Assert.IsType<Neo4jExportResult.Ready>(result);
        using var archive = new ZipArchive(ready.Content, ZipArchiveMode.Read);
        using var cypherReader = new StreamReader(archive.GetEntry("load.cypher")!.Open());
        var cypherText = await cypherReader.ReadToEndAsync();
        Assert.Contains("MERGE (e:Entity {id: row.id})", cypherText);
        Assert.Contains("MERGE (g:GoldenRecord {cluster_id: row.cluster_id})", cypherText);
        Assert.DoesNotContain(":Spaceship", cypherText);
    }
}
