using Linkuity.Cli;
using Linkuity.Core.Models;
using Linkuity.TestSupport;
using Testcontainers.PostgreSql;

namespace Linkuity.Infrastructure.Postgres.Tests;

/// <summary>Gated on Docker. Proves the Postgres source adapter reads project records for the audit.</summary>
public sealed class PostgresBlockingAuditTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable()) return;
        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _pg.StartAsync();
        DbUpMigrator.EnsureSchema(_pg.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task Audit_PostgresSource_ReadsProjectRecords()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        var cs = _pg!.GetConnectionString();
        var store = new PostgresMetadataStore(
            new PostgresMetadataStoreOptions { ConnectionString = cs },
            engine: null, profileProvider: null, indexedRetrieval: null);
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Blk", "organization", null, now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "SEC", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        EntityRecord Rec(string srid, string name) => new()
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceId = source.Id, IngestBatchId = batch.Id,
            SourceRecordId = srid,
            Fields = new Dictionary<string, string> { ["organization_name"] = name },
            CreatedAt = now
        };
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [Rec("boe-gleif", "THE BOEING COMPANY"), Rec("boe-sec", "BOEING CO")], [], [], [], []),
            CancellationToken.None);

        var profile = Path.Combine(Path.GetTempPath(), "blk-org-" + Guid.NewGuid().ToString("N") + ".profile.json");
        await File.WriteAllTextAsync(profile, """
        {
          "contentType": "organization",
          "fields": [ { "name": "organization_name", "semanticType": "OrganizationName",
            "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "jaccard", "weight": 4.0 } ],
          "normalizationStrategy": "identity",
          "blockingStrategies": ["exact-value","token-name","prefix"],
          "candidateRetrievalStrategy": "blocking-linear",
          "similarityStrategy": "field-weighted",
          "scoringStrategy": "identifier-weighted",
          "decisionStrategy": "threshold",
          "clusteringStrategy": "union-find",
          "autoMatchThreshold": 0.41, "reviewThreshold": 0.31
        }
        """);

        using var outW = new StringWriter();
        var prev = Console.Out;
        Console.SetOut(outW);
        try
        {
            var exit = await new LocalBatchRunner().RunAsync(
            [
                "match", "blocking", "explain",
                "--metadata-store", "postgres", "--connection-string", cs,
                "--project-id", project.Id.ToString(),
                "--profile", profile, "--left", "boe-gleif", "--right", "boe-sec"
            ], CancellationToken.None);
            Assert.Equal(0, exit);
            Assert.Contains("SKIPPED", outW.ToString());
        }
        finally { Console.SetOut(prev); }
    }
}
