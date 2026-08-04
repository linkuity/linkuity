using System.Text;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline.Tests;

public sealed class BatchMatchingServiceTests
{
    // Mirrors what the retired config-to-profile factory used to produce for a
    // person config with first_name/last_name/email: same roles/evaluator/weight as
    // the built-in person profile's templates for those semantic types, remapped
    // onto blocking-linear retrieval (as BuildMatchesCsv always forces).
    private static MatchingProfile PersonProfile() => new()
    {
        ContentType = "person",
        Fields =
        [
            new ProfileField { Name = "first_name", SemanticType = SemanticFieldType.FirstName, Roles = FieldRole.Searchable | FieldRole.Matchable, SimilarityEvaluator = "fuzzy", Weight = 1.0 },
            new ProfileField { Name = "last_name", SemanticType = SemanticFieldType.LastName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking, SimilarityEvaluator = "fuzzy", Weight = 2.0 },
            new ProfileField { Name = "email", SemanticType = SemanticFieldType.Email, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking | FieldRole.Identifier, SimilarityEvaluator = "exact", Weight = 3.0 }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["exact-value", "token-name"],
        CandidateRetrievalStrategy = "blocking-linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.90,
        ReviewThreshold = 0.75
    };

    private static (string, IReadOnlyDictionary<string, string>) Row(
        string id, string first, string last, string email) =>
        (id, new Dictionary<string, string>
        {
            ["id"] = id, ["first_name"] = first, ["last_name"] = last, ["email"] = email
        });

    private static List<string[]> ParseCsv(string csv)
    {
        var lines = csv.Replace("\r\n", "\n").Trim().Split('\n');
        return lines.Select(l => l.Split(',')).ToList();
    }

    [Fact]
    public void BuildMatchesCsv_HasExpectedHeader()
    {
        var csv = BatchMatchingService.BuildMatchesCsv([Row("a", "Ada", "Lovelace", "ada@x.com")], PersonProfile());
        var header = ParseCsv(csv)[0];
        Assert.Equal(["left_id", "right_id", "similarity", "fuzzy_similarity"], header);
    }

    [Fact]
    public void BuildMatchesCsv_IdenticalRecordsProduceOneEdge()
    {
        var rows = new List<(string, IReadOnlyDictionary<string, string>)>
        {
            Row("a", "Ada", "Lovelace", "ada@x.com"),
            Row("b", "Ada", "Lovelace", "ada@x.com")
        };
        var csv = BatchMatchingService.BuildMatchesCsv(rows, PersonProfile());
        var dataRows = ParseCsv(csv).Skip(1).ToList();
        var edge = Assert.Single(dataRows);
        Assert.Equal(new HashSet<string> { "a", "b" }, new HashSet<string> { edge[0], edge[1] });
    }

    [Fact]
    public void BuildMatchesCsv_DissimilarRecordsProduceNoEdges()
    {
        var rows = new List<(string, IReadOnlyDictionary<string, string>)>
        {
            Row("a", "Ada", "Lovelace", "ada@x.com"),
            Row("b", "Zed", "Quixote", "zed@y.com")
        };
        var csv = BatchMatchingService.BuildMatchesCsv(rows, PersonProfile());
        Assert.Empty(ParseCsv(csv).Skip(1));
    }

    [Fact]
    public void BuildMatchesCsv_ExcludesSelfPairs()
    {
        var rows = new List<(string, IReadOnlyDictionary<string, string>)>
        {
            Row("a", "Ada", "Lovelace", "ada@x.com"),
            Row("b", "Ada", "Lovelace", "ada@x.com")
        };
        var csv = BatchMatchingService.BuildMatchesCsv(rows, PersonProfile());
        foreach (var edge in ParseCsv(csv).Skip(1))
            Assert.NotEqual(edge[0], edge[1]);
    }

    [Fact]
    public void BuildMatchesCsv_WithProfile_MatchesOnSharedIdentifier()
    {
        var profile = DefaultMatchingProfileProvider.CreatePersonProfile();
        var rows = new List<(string, IReadOnlyDictionary<string, string>)>
        {
            ("1", new Dictionary<string, string> { ["id"] = "1", ["email"] = "a@example.com", ["first_name"] = "Al" }),
            ("2", new Dictionary<string, string> { ["id"] = "2", ["email"] = "a@example.com", ["first_name"] = "Al" }),
            ("3", new Dictionary<string, string> { ["id"] = "3", ["email"] = "b@example.com", ["first_name"] = "Bo" }),
        };

        var csv = BatchMatchingService.BuildMatchesCsv(rows, profile);

        Assert.Contains("1,2", csv); // shared email -> auto-match edge
        Assert.DoesNotContain("1,3", csv);
    }

    // Pins the fix to BuildMatchesCsv's own threshold construction: it now asks
    // engine.ScaleOf(profile) for the resolved scorer's scale instead of calling
    // profile.ThresholdsOn() with no argument (which defaults to ScoreScale.UnitInterval). An
    // "evidence" (log-odds) profile's autoMatchThreshold/reviewThreshold are bits of evidence,
    // well above 1.0 — before the fix, MatchThresholds's constructor threw
    // ArgumentOutOfRangeException the first time BuildMatchesCsv tried to band a score, rather
    // than ever writing a row.
    private static MatchingProfile EvidenceProfile() => new()
    {
        ContentType = "organization",
        Fields =
        [
            new ProfileField
            {
                Name = "organization_name",
                SemanticType = SemanticFieldType.OrganizationName,
                // Blocking, not just Matchable: BuildMatchesCsv always forces blocking-linear
                // retrieval (see WithCandidateRetrievalStrategy below), which only returns
                // candidates sharing a blocking key — a field with no Blocking role never
                // generates one, so no pair would ever become a candidate.
                Roles = FieldRole.Matchable | FieldRole.Blocking,
                SimilarityEvaluator = "exact",
                Evidence = new FieldEvidence { SameEntityAgreement = 0.9, ChanceAgreement = 0.01, MaxAgreementBits = 6.0 }
            }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["exact-value", "token-name"],
        CandidateRetrievalStrategy = "blocking-linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "evidence",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 5.0,
        ReviewThreshold = 2.0
    };

    [Fact]
    public void BuildMatchesCsv_WithEvidenceScoredProfile_BandsOnLogOdds_InsteadOfThrowing()
    {
        var rows = new List<(string, IReadOnlyDictionary<string, string>)>
        {
            ("1", new Dictionary<string, string> { ["id"] = "1", ["organization_name"] = "Acme Corp" }),
            ("2", new Dictionary<string, string> { ["id"] = "2", ["organization_name"] = "Acme Corp" }),
            ("3", new Dictionary<string, string> { ["id"] = "3", ["organization_name"] = "Zed Industries" }),
        };

        var csv = BatchMatchingService.BuildMatchesCsv(rows, EvidenceProfile());

        Assert.Contains("1,2", csv); // identical name -> 6.0 bits, clears the 5.0 auto threshold
        Assert.DoesNotContain("1,3", csv);
    }
}

public sealed class BatchMatchingServiceRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bms-{Guid.NewGuid():N}");

    [Fact]
    public async Task RunAsync_WritesMatchesForIdenticalRecords()
    {
        var store = new FileSystemArtifactStore(new FileSystemArtifactStoreOptions { RootPath = _root });
        var jobId = Guid.NewGuid().ToString();
        var normalized = "id,first_name,last_name,email\na,Ada,Lovelace,ada@x.com\nb,Ada,Lovelace,ada@x.com\n";
        using (var s = new MemoryStream(Encoding.UTF8.GetBytes(normalized)))
            await store.UploadAsync($"{jobId}/normalized.csv", s, "text/csv");

        await new BatchMatchingService(store).RunAsync(
            jobId, DefaultMatchingProfileProvider.CreatePersonProfile(), CancellationToken.None);

        await using var outStream = await store.DownloadAsync($"{jobId}/matches.csv");
        using var reader = new StreamReader(outStream);
        var csv = await reader.ReadToEndAsync();
        Assert.Contains("a,b", csv.Replace("\r\n", "\n"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
