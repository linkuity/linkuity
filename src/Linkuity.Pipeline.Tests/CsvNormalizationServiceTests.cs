using System.Text;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline.Tests;

public class CsvNormalizationServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"linkuity-csvnorm-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private (CsvNormalizationService service, FileSystemArtifactStore blobs) Build()
    {
        var blobs = new FileSystemArtifactStore(new FileSystemArtifactStoreOptions { RootPath = _rootPath });
        return (new CsvNormalizationService(blobs), blobs);
    }

    private static async Task WriteCsvAsync(FileSystemArtifactStore blobs, Guid jobId, string csv)
    {
        var bytes = Encoding.UTF8.GetBytes(csv);
        await blobs.UploadAsync($"{jobId}/input.csv", new MemoryStream(bytes), "text/csv");
    }

    private static async Task<string> ReadNormalizedAsync(FileSystemArtifactStore blobs, Guid jobId)
    {
        using var stream = await blobs.DownloadAsync($"{jobId}/normalized.csv");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    // NormalizeAsync only reads profile.Fields (name -> semantic type), so a minimal
    // single-field profile carries the same normalization intent the old single-field
    // config-based overload did. `roles` defaults to Matchable; pass FieldRole.None to
    // express "excluded from matching, still normalized".
    private static MatchingProfile SingleFieldProfile(
        string name, SemanticFieldType type, FieldRole roles = FieldRole.Matchable) => new()
    {
        ContentType = "person",
        Fields = [new ProfileField { Name = name, SemanticType = type, Roles = roles }],
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
    public async Task NormalizeAsync_WritesNormalizedCsvBlob()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await WriteCsvAsync(blobs, jobId, "email\nuser@EXAMPLE.COM");

        await service.NormalizeAsync(jobId, SingleFieldProfile("email", SemanticFieldType.Email));

        Assert.True(await blobs.ExistsAsync($"{jobId}/normalized.csv"));
    }

    [Fact]
    public async Task NormalizeAsync_PreservesHeaderRow()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await WriteCsvAsync(blobs, jobId, "email,notes\nuser@EXAMPLE.COM,some note");

        await service.NormalizeAsync(jobId, SingleFieldProfile("email", SemanticFieldType.Email));

        var output = await ReadNormalizedAsync(blobs, jobId);
        Assert.StartsWith("email,notes", output);
    }

    [Fact]
    public async Task NormalizeAsync_NormalizesColumnsInFields()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await WriteCsvAsync(blobs, jobId, "email\nUser@EXAMPLE.COM");

        await service.NormalizeAsync(jobId, SingleFieldProfile("email", SemanticFieldType.Email));

        var output = await ReadNormalizedAsync(blobs, jobId);
        Assert.Contains("user@example.com", output);
    }

    [Fact]
    public async Task NormalizeAsync_PassesThroughColumnsNotInFields()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await WriteCsvAsync(blobs, jobId, "email,notes\nuser@example.com,Some Note Value");

        await service.NormalizeAsync(jobId, SingleFieldProfile("email", SemanticFieldType.Email));

        var output = await ReadNormalizedAsync(blobs, jobId);
        Assert.Contains("Some Note Value", output);
    }

    [Fact]
    public async Task NormalizeAsync_ValidPhoneNormalized_InvalidPhonePassesThrough()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await WriteCsvAsync(blobs, jobId, "phone\n(800) 555-0100\nnot-a-phone");

        await service.NormalizeAsync(jobId, SingleFieldProfile("phone", SemanticFieldType.Phone));

        var output = await ReadNormalizedAsync(blobs, jobId);
        Assert.Contains("+18005550100", output);
        Assert.Contains("not-a-phone", output);
    }

    [Fact]
    public async Task NormalizeAsync_ColumnNameMatchIsCaseInsensitive()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await WriteCsvAsync(blobs, jobId, "Email\nUser@EXAMPLE.COM");

        await service.NormalizeAsync(jobId, SingleFieldProfile("email", SemanticFieldType.Email));

        var output = await ReadNormalizedAsync(blobs, jobId);
        Assert.Contains("user@example.com", output);
    }

    [Fact]
    public async Task NormalizeAsync_ReturnsDataRowCount()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await WriteCsvAsync(blobs, jobId, "email\na@b.com\nc@d.com\ne@f.com");

        var count = await service.NormalizeAsync(jobId, SingleFieldProfile("email", SemanticFieldType.Email));

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task NormalizeAsync_FieldWithParticipatesInMatchingFalse_StillNormalized()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await WriteCsvAsync(blobs, jobId, "phone\n(800) 555-0100");

        await service.NormalizeAsync(jobId, SingleFieldProfile("phone", SemanticFieldType.Phone, FieldRole.None));

        var output = await ReadNormalizedAsync(blobs, jobId);
        Assert.Contains("+18005550100", output);
    }

    [Fact]
    public async Task NormalizeAsync_WithProfile_NormalizesBySemanticType()
    {
        var (service, blobs) = Build();
        var jobId = Guid.NewGuid();
        await WriteCsvAsync(blobs, jobId, "id,email\n1,ALICE@EXAMPLE.COM\n");

        var profile = DefaultMatchingProfileProvider.CreatePersonProfile();

        var count = await service.NormalizeAsync(jobId, profile, CancellationToken.None);

        Assert.Equal(1, count);
        var output = await ReadNormalizedAsync(blobs, jobId);
        Assert.Contains("alice@example.com", output); // email lowercased by semantic normalization
    }
}
