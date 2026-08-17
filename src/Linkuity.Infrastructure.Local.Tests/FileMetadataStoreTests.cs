using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;

namespace Linkuity.Infrastructure.Local.Tests;

public class FileMetadataStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"linkuity-metadata-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveCompletedBatchAsync_PersistsMetadataAcrossStoreInstances()
    {
        var databasePath = Path.Combine(_root, "metadata", "linkuity.json");
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = databasePath });
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        var left = new EntityRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            SourceId = source.Id,
            IngestBatchId = batch.Id,
            SourceRecordId = "crm-001",
            Fields = new Dictionary<string, string> { ["email"] = "alice@example.com" },
            CreatedAt = now
        };
        var right = new EntityRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            SourceId = source.Id,
            IngestBatchId = batch.Id,
            SourceRecordId = "mkt-001",
            Fields = new Dictionary<string, string> { ["email"] = "alice@example.com" },
            CreatedAt = now
        };
        var clusterId = Guid.NewGuid();
        var goldenRecordId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [left, right],
                [
                    new MatchEdge
                    {
                        Id = Guid.NewGuid(),
                        ProjectId = project.Id,
                        IngestBatchId = batch.Id,
                        LeftEntityRecordId = left.Id,
                        RightEntityRecordId = right.Id,
                        Score = 0.99,
                        Method = "batch",
                        CreatedAt = now
                    }
                ],
                [
                    new Cluster
                    {
                        Id = clusterId,
                        ProjectId = project.Id,
                        MemberEntityRecordIds = [left.Id, right.Id],
                        CreatedAt = now
                    }
                ],
                [
                    new GoldenRecord
                    {
                        Id = goldenRecordId,
                        ProjectId = project.Id,
                        ClusterId = clusterId,
                        CurrentVersionId = versionId,
                        Fields = new Dictionary<string, string> { ["email"] = "alice@example.com" },
                        UpdatedAt = now
                    }
                ],
                [
                    new GoldenRecordVersion
                    {
                        Id = versionId,
                        GoldenRecordId = goldenRecordId,
                        ProjectId = project.Id,
                        ClusterId = clusterId,
                        IngestBatchId = batch.Id,
                        VersionNumber = 1,
                        Fields = new Dictionary<string, string> { ["email"] = "alice@example.com" },
                        CreatedAt = now
                    }
                ]),
            CancellationToken.None);

        var reloaded = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = databasePath });

        Assert.Equal("Customer MDM", Assert.Single(await reloaded.ListProjectsAsync(CancellationToken.None)).Name);
        Assert.Equal("CRM", Assert.Single(await reloaded.ListSourcesAsync(project.Id, CancellationToken.None)).Name);
        Assert.Equal(batch.Id, Assert.Single(await reloaded.ListIngestBatchesAsync(project.Id, CancellationToken.None)).Id);
        Assert.Equal(2, (await reloaded.ListEntityRecordsAsync(project.Id, CancellationToken.None)).Count);
        Assert.Single(await reloaded.ListMatchEdgesAsync(project.Id, CancellationToken.None));
        Assert.Equal(2, Assert.Single(await reloaded.ListClustersAsync(project.Id, CancellationToken.None)).MemberEntityRecordIds.Count);
        Assert.Equal(versionId, Assert.Single(await reloaded.ListGoldenRecordsAsync(project.Id, CancellationToken.None)).CurrentVersionId);
        Assert.Equal(batch.Id, Assert.Single(await reloaded.ListGoldenRecordVersionsAsync(project.Id, CancellationToken.None)).IngestBatchId);
    }

    [Fact]
    public async Task CreateSourceAsync_WhenProjectIsMissing_Throws()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata.json") });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateSourceAsync(Guid.NewGuid(), "CRM", DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Contains("Project not found", ex.Message);
    }

    [Fact]
    public async Task CreateProjectAsync_WhenNameAlreadyExists_Throws()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata.json") });
        await store.CreateProjectAsync("Customer MDM", "person", DateTimeOffset.UtcNow, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateProjectAsync("customer mdm", "person", DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Contains("Project already exists", ex.Message);
    }

    [Fact]
    public async Task ProjectMergePolicy_PersistsAcrossStoreInstancesAndCanBeCleared()
    {
        var databasePath = Path.Combine(_root, "metadata-policy.json");
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = databasePath });
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync(
            "Customer MDM",
            "person",
            new MergeConfiguration
            {
                MergeFields =
                [
                    new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing"] }
                ]
            },
            now,
            CancellationToken.None);

        var reloaded = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = databasePath });
        var persisted = await reloaded.GetProjectAsync(project.Id, CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal("email", persisted.MergeConfiguration!.MergeFields[0].FieldName);
        Assert.Equal(["CRM", "Marketing"], persisted.MergeConfiguration.MergeFields[0].SourcePriority);

        await reloaded.UpdateProjectMergePolicyAsync(project.Id, null, CancellationToken.None);
        var clearedReload = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = databasePath });

        Assert.Null((await clearedReload.GetProjectAsync(project.Id, CancellationToken.None))!.MergeConfiguration);
    }

    [Fact]
    public async Task CreateProjectAsync_WhenMergePolicyHasDuplicateFields_Throws()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-policy-invalid.json") });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.CreateProjectAsync(
                "Customer MDM",
                "person",
                new MergeConfiguration
                {
                    MergeFields =
                    [
                        new MergeField { FieldName = "email", SourcePriority = ["CRM"] },
                        new MergeField { FieldName = "EMAIL", SourcePriority = ["Marketing"] }
                    ]
                },
                DateTimeOffset.UtcNow,
                CancellationToken.None));

        Assert.Contains("Duplicate merge policy field", ex.Message);
    }

    [Fact]
    public async Task CreateProjectAsync_WhenCalledConcurrently_PreservesAllWrites()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata.json") });

        await Task.WhenAll(
            Enumerable.Range(1, 20).Select(i =>
                store.CreateProjectAsync($"Project {i}", "person", DateTimeOffset.UtcNow, CancellationToken.None)));

        Assert.Equal(20, (await store.ListProjectsAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task SaveCompletedBatchAsync_WhenBatchIsMissing_ThrowsWithoutWritingOrphans()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata.json") });
        var project = await store.CreateProjectAsync("Customer MDM", "person", DateTimeOffset.UtcNow, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", DateTimeOffset.UtcNow, CancellationToken.None);
        var missingBatchId = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveCompletedBatchAsync(
                new CompletedBatchMetadata(
                    [
                        new EntityRecord
                        {
                            Id = Guid.NewGuid(),
                            ProjectId = project.Id,
                            SourceId = source.Id,
                            IngestBatchId = missingBatchId,
                            SourceRecordId = "crm-001",
                            Fields = new Dictionary<string, string> { ["email"] = "alice@example.com" },
                            CreatedAt = DateTimeOffset.UtcNow
                        }
                    ],
                    [],
                    [],
                    [],
                    []),
                CancellationToken.None));

        Assert.Contains("Ingest batch not found", ex.Message);
        Assert.Empty(await store.ListEntityRecordsAsync(project.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SaveIncrementalIngestAsync_AutoMatchesExistingClusterAndCreatesGoldenVersion()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var initialBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var existing = NewRecord(project.Id, source.Id, initialBatch.Id, "crm-001", "alice@example.com", "Alice", ["email:alice@example.com"], now);
        var clusterId = Guid.NewGuid();
        var goldenRecordId = Guid.NewGuid();
        var initialVersionId = Guid.NewGuid();
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [existing],
                [],
                [new Cluster { Id = clusterId, ProjectId = project.Id, MemberEntityRecordIds = [existing.Id], CreatedAt = now }],
                [new GoldenRecord
                {
                    Id = goldenRecordId,
                    ProjectId = project.Id,
                    ClusterId = clusterId,
                    CurrentVersionId = initialVersionId,
                    Fields = new Dictionary<string, string> { ["email"] = "alice@example.com", ["name"] = "Alice" },
                    UpdatedAt = now
                }],
                [new GoldenRecordVersion
                {
                    Id = initialVersionId,
                    GoldenRecordId = goldenRecordId,
                    ProjectId = project.Id,
                    ClusterId = clusterId,
                    IngestBatchId = initialBatch.Id,
                    VersionNumber = 1,
                    Fields = new Dictionary<string, string> { ["email"] = "alice@example.com", ["name"] = "Alice" },
                    CreatedAt = now
                }]),
            CancellationToken.None);

        var incrementalBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1), CancellationToken.None);
        var incoming = NewRecord(project.Id, source.Id, incrementalBatch.Id, "web-001", "alice@example.com", "Alice Verified", ["email:alice@example.com"], now.AddMinutes(1));

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incrementalBatch.Id, [incoming], 0.90, 0.75),
            CancellationToken.None);

        Assert.Equal(1, result.RecordsAdded);
        Assert.Equal(1, result.AutoMatches);
        Assert.Equal(0, result.ReviewTasks);
        Assert.Equal(0, result.SingletonClusters);
        Assert.Equal(1, result.GoldenRecordVersionsCreated);

        Assert.Equal(2, (await store.ListEntityRecordsAsync(project.Id, CancellationToken.None)).Count);
        var cluster = Assert.Single(await store.ListClustersAsync(project.Id, CancellationToken.None));
        Assert.Equal(clusterId, cluster.Id);
        Assert.Contains(incoming.Id, cluster.MemberEntityRecordIds);
        Assert.Single(await store.ListMatchEdgesAsync(project.Id, CancellationToken.None));
        var golden = Assert.Single(await store.ListGoldenRecordsAsync(project.Id, CancellationToken.None));
        Assert.Equal("Alice Verified", golden.Fields["name"]);
        var versions = await store.ListGoldenRecordVersionsAsync(project.Id, CancellationToken.None);
        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, v => v.IngestBatchId == incrementalBatch.Id);
    }

    [Fact]
    public async Task SaveCompletedBatchAsync_UsesProjectMergePolicyInsteadOfImportedGoldenFields()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-policy-full.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync(
            "Customer MDM",
            "person",
            new MergeConfiguration
            {
                MergeFields =
                [
                    new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing"] }
                ]
            },
            now,
            CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        var crm = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-001", now, ["email:alice"],
            new Dictionary<string, string>
            {
                ["id"] = "crm-001",
                ["source"] = "CRM",
                ["email"] = "crm@example.com",
                ["name"] = "Alice CRM"
            });
        var marketing = NewRecordWithFields(project.Id, source.Id, batch.Id, "mkt-001", now, ["email:alice"],
            new Dictionary<string, string>
            {
                ["id"] = "mkt-001",
                ["source"] = "Marketing",
                ["email"] = "marketing@example.com",
                ["name"] = "Alice Marketing"
            });
        var clusterId = Guid.NewGuid();

        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [crm, marketing],
                [],
                [new Cluster { Id = clusterId, ProjectId = project.Id, MemberEntityRecordIds = [crm.Id, marketing.Id], CreatedAt = now }],
                [new GoldenRecord
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    ClusterId = clusterId,
                    CurrentVersionId = Guid.NewGuid(),
                    Fields = new Dictionary<string, string> { ["email"] = "marketing@example.com" },
                    UpdatedAt = now
                }],
                []),
            CancellationToken.None);

        var golden = Assert.Single(await store.ListGoldenRecordsAsync(project.Id, CancellationToken.None));
        Assert.Equal("crm@example.com", golden.Fields["email"]);
        Assert.Equal("crm@example.com", Assert.Single(await store.ListGoldenRecordVersionsAsync(project.Id, CancellationToken.None)).Fields["email"]);
    }

    /// <summary>
    /// The built-in "person" profile declares no field with
    /// SemanticType.SourceIdentifier (only the built-in "organization" profile does), so
    /// there is no profile-declared answer to "which column names the source system".
    /// Source-priority merge must still work by falling back to a column literally named
    /// "source" — the assumption durable ingestion made unconditionally before source-field
    /// resolution became profile-driven. This pins that fallback down as intentional,
    /// exercising CompletedBatchResolver's own copy of it.
    /// </summary>
    [Fact]
    public async Task SaveCompletedBatchAsync_SourcePriorityFallsBackToSourceColumn_WhenProfileDoesNotDeclareSourceIdentifier()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-source-fallback-completed.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync(
            "Customer MDM",
            "person",
            new MergeConfiguration
            {
                MergeFields = [new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing"] }]
            },
            now,
            CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        var crm = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-001", now, ["email:alice"],
            new Dictionary<string, string>
            {
                ["id"] = "crm-001",
                ["source"] = "CRM",
                ["email"] = "crm@example.com"
            });
        var marketing = NewRecordWithFields(project.Id, source.Id, batch.Id, "mkt-001", now, ["email:alice"],
            new Dictionary<string, string>
            {
                ["id"] = "mkt-001",
                ["source"] = "Marketing",
                ["email"] = "marketing@example.com"
            });
        var clusterId = Guid.NewGuid();

        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [crm, marketing],
                [],
                [new Cluster { Id = clusterId, ProjectId = project.Id, MemberEntityRecordIds = [crm.Id, marketing.Id], CreatedAt = now }],
                [],
                []),
            CancellationToken.None);

        var golden = Assert.Single(await store.ListGoldenRecordsAsync(project.Id, CancellationToken.None));
        Assert.Equal("crm@example.com", golden.Fields["email"]);
    }

    /// <summary>
    /// Same intent as the completed-batch version above, but exercises
    /// IncrementalResolver.UpdateGoldenRecords's own copy of the fallback — a separate code
    /// path — by recomputing the golden record when a third record joins an already-formed
    /// cluster via SaveIncrementalIngestAsync.
    /// </summary>
    [Fact]
    public async Task SaveIncrementalIngestAsync_SourcePriorityFallsBackToSourceColumn_WhenProfileDoesNotDeclareSourceIdentifier()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-source-fallback-incremental.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync(
            "Customer MDM",
            "person",
            new MergeConfiguration
            {
                MergeFields = [new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing", "Web"] }]
            },
            now,
            CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var initialBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        var crm = NewRecordWithFields(project.Id, source.Id, initialBatch.Id, "crm-001", now, ["phone:5550100"],
            new Dictionary<string, string>
            {
                ["id"] = "crm-001",
                ["source"] = "CRM",
                ["email"] = "crm@example.com",
                ["phone"] = "5550100",
                ["name"] = "Alice CRM"
            });
        var marketing = NewRecordWithFields(project.Id, source.Id, initialBatch.Id, "mkt-001", now, ["phone:5550100"],
            new Dictionary<string, string>
            {
                ["id"] = "mkt-001",
                ["source"] = "Marketing",
                ["email"] = "marketing@example.com",
                ["phone"] = "5550100",
                ["name"] = "Alice Marketing"
            });
        var clusterId = Guid.NewGuid();
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [crm, marketing],
                [],
                [new Cluster { Id = clusterId, ProjectId = project.Id, MemberEntityRecordIds = [crm.Id, marketing.Id], CreatedAt = now }],
                [],
                []),
            CancellationToken.None);

        var incrementalBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1), CancellationToken.None);
        var web = NewRecordWithFields(project.Id, source.Id, incrementalBatch.Id, "web-001", now.AddMinutes(1), ["phone:5550100"],
            new Dictionary<string, string>
            {
                ["id"] = "web-001",
                ["source"] = "Web",
                ["email"] = "web@example.com",
                ["phone"] = "5550100",
                ["name"] = "Alice Web"
            });

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incrementalBatch.Id, [web], 0.90, 0.75),
            CancellationToken.None);

        var golden = Assert.Single(await store.ListGoldenRecordsAsync(project.Id, CancellationToken.None));
        Assert.Equal("crm@example.com", golden.Fields["email"]);
    }

    [Fact]
    public async Task SaveCompletedBatchAsync_WhenPolicyProjectClusterHasNoMembers_ThrowsValidationError()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-empty-cluster.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync(
            "Customer MDM",
            "person",
            new MergeConfiguration
            {
                MergeFields =
                [
                    new MergeField { FieldName = "email", SourcePriority = ["CRM"] }
                ]
            },
            now,
            CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 0, now, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveCompletedBatchAsync(
                new CompletedBatchMetadata(
                    [],
                    [],
                    [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [], CreatedAt = now }],
                    [],
                    []),
                CancellationToken.None));

        Assert.Contains("Cluster must contain at least one entity record", ex.Message);
    }

    [Fact]
    public async Task SaveCompletedBatchAsync_RecomputesOnlyClustersWhoseProjectHasMergePolicy()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-mixed-policy.json") });
        var now = DateTimeOffset.UtcNow;
        var policyProject = await store.CreateProjectAsync(
            "Policy Project",
            "person",
            new MergeConfiguration
            {
                MergeFields =
                [
                    new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing"] }
                ]
            },
            now,
            CancellationToken.None);
        var plainProject = await store.CreateProjectAsync("Plain Project", "person", now, CancellationToken.None);
        var policySource = await store.CreateSourceAsync(policyProject.Id, "CSV", now, CancellationToken.None);
        var plainSource = await store.CreateSourceAsync(plainProject.Id, "CSV", now, CancellationToken.None);
        var policyBatch = await store.CreateIngestBatchAsync(policyProject.Id, policySource.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        var plainBatch = await store.CreateIngestBatchAsync(plainProject.Id, plainSource.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var policyCrm = NewRecordWithFields(policyProject.Id, policySource.Id, policyBatch.Id, "policy-crm", now, ["phone:5550100"],
            new Dictionary<string, string> { ["id"] = "policy-crm", ["source"] = "CRM", ["email"] = "crm@example.com", ["phone"] = "5550100" });
        var policyMarketing = NewRecordWithFields(policyProject.Id, policySource.Id, policyBatch.Id, "policy-mkt", now, ["phone:5550100"],
            new Dictionary<string, string> { ["id"] = "policy-mkt", ["source"] = "Marketing", ["email"] = "marketing@example.com", ["phone"] = "5550100" });
        var plainRecord = NewRecordWithFields(plainProject.Id, plainSource.Id, plainBatch.Id, "plain-001", now, ["email:plain@example.com"],
            new Dictionary<string, string> { ["id"] = "plain-001", ["source"] = "CSV", ["email"] = "plain@example.com" });
        var policyClusterId = Guid.NewGuid();
        var plainClusterId = Guid.NewGuid();
        var plainGoldenId = Guid.NewGuid();
        var plainVersionId = Guid.NewGuid();

        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [policyCrm, policyMarketing, plainRecord],
                [],
                [
                    new Cluster { Id = policyClusterId, ProjectId = policyProject.Id, MemberEntityRecordIds = [policyCrm.Id, policyMarketing.Id], CreatedAt = now },
                    new Cluster { Id = plainClusterId, ProjectId = plainProject.Id, MemberEntityRecordIds = [plainRecord.Id], CreatedAt = now }
                ],
                [
                    new GoldenRecord
                    {
                        Id = plainGoldenId,
                        ProjectId = plainProject.Id,
                        ClusterId = plainClusterId,
                        CurrentVersionId = plainVersionId,
                        Fields = new Dictionary<string, string> { ["email"] = "imported-plain@example.com" },
                        UpdatedAt = now
                    }
                ],
                [
                    new GoldenRecordVersion
                    {
                        Id = plainVersionId,
                        GoldenRecordId = plainGoldenId,
                        ProjectId = plainProject.Id,
                        ClusterId = plainClusterId,
                        IngestBatchId = plainBatch.Id,
                        VersionNumber = 1,
                        Fields = new Dictionary<string, string> { ["email"] = "imported-plain@example.com" },
                        CreatedAt = now
                    }
                ]),
            CancellationToken.None);

        Assert.Equal("crm@example.com", Assert.Single(await store.ListGoldenRecordsAsync(policyProject.Id, CancellationToken.None)).Fields["email"]);
        Assert.Equal("imported-plain@example.com", Assert.Single(await store.ListGoldenRecordsAsync(plainProject.Id, CancellationToken.None)).Fields["email"]);
    }

    [Fact]
    public async Task SaveIncrementalIngestAsync_UsesSameProjectMergePolicyAsCompletedBatch()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-policy-incremental.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync(
            "Customer MDM",
            "person",
            new MergeConfiguration
            {
                MergeFields =
                [
                    new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing", "Web"] }
                ]
            },
            now,
            CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var initialBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        var crm = NewRecordWithFields(project.Id, source.Id, initialBatch.Id, "crm-001", now, ["phone:5550100"],
            new Dictionary<string, string>
            {
                ["id"] = "crm-001",
                ["source"] = "CRM",
                ["email"] = "crm@example.com",
                ["phone"] = "5550100",
                ["name"] = "Alice CRM"
            });
        var marketing = NewRecordWithFields(project.Id, source.Id, initialBatch.Id, "mkt-001", now, ["phone:5550100"],
            new Dictionary<string, string>
            {
                ["id"] = "mkt-001",
                ["source"] = "Marketing",
                ["email"] = "marketing@example.com",
                ["phone"] = "5550100",
                ["name"] = "Alice Marketing"
            });
        var clusterId = Guid.NewGuid();
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [crm, marketing],
                [],
                [new Cluster { Id = clusterId, ProjectId = project.Id, MemberEntityRecordIds = [crm.Id, marketing.Id], CreatedAt = now }],
                [],
                []),
            CancellationToken.None);
        var fullImportEmail = Assert.Single(await store.ListGoldenRecordsAsync(project.Id, CancellationToken.None)).Fields["email"];
        var incrementalBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1), CancellationToken.None);
        var web = NewRecordWithFields(project.Id, source.Id, incrementalBatch.Id, "web-001", now.AddMinutes(1), ["phone:5550100"],
            new Dictionary<string, string>
            {
                ["id"] = "web-001",
                ["source"] = "Web",
                ["email"] = "web@example.com",
                ["phone"] = "5550100",
                ["name"] = "Alice Web"
            });

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incrementalBatch.Id, [web], 0.90, 0.75),
            CancellationToken.None);

        var incrementalEmail = Assert.Single(await store.ListGoldenRecordsAsync(project.Id, CancellationToken.None)).Fields["email"];
        Assert.Equal("crm@example.com", fullImportEmail);
        Assert.Equal(fullImportEmail, incrementalEmail);
    }

    [Fact]
    public async Task SaveIncrementalIngestAsync_ReviewBandCreatesReviewTaskAndNoMatchCreatesSingleton()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var initialBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var existing = NewRecord(project.Id, source.Id, initialBatch.Id, "crm-002", "bob@example.com", "Robert Smith", ["name:smith"], now);
        var existingClusterId = Guid.NewGuid();
        var existingGoldenId = Guid.NewGuid();
        var existingVersionId = Guid.NewGuid();
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [existing],
                [],
                [new Cluster { Id = existingClusterId, ProjectId = project.Id, MemberEntityRecordIds = [existing.Id], CreatedAt = now }],
                [new GoldenRecord
                {
                    Id = existingGoldenId,
                    ProjectId = project.Id,
                    ClusterId = existingClusterId,
                    CurrentVersionId = existingVersionId,
                    Fields = existing.Fields,
                    UpdatedAt = now
                }],
                [new GoldenRecordVersion
                {
                    Id = existingVersionId,
                    GoldenRecordId = existingGoldenId,
                    ProjectId = project.Id,
                    ClusterId = existingClusterId,
                    IngestBatchId = initialBatch.Id,
                    VersionNumber = 1,
                    Fields = existing.Fields,
                    CreatedAt = now
                }]),
            CancellationToken.None);

        var incrementalBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 2, now.AddMinutes(1), CancellationToken.None);
        // No email captured for this web scrape (blank, so the field isn't even compared) and a
        // nickname variant of the first name: "Robert Smith" vs "Robbie Smith" fuzzy 0.83 is the
        // whole weighted score (name is the only comparable field, weight 1.5), which clears the
        // review-floor gate with a comfortable margin and stays under the 0.90 auto threshold.
        var review = NewRecord(project.Id, source.Id, incrementalBatch.Id, "web-002", "", "Robbie Smith", ["name:smith"], now.AddMinutes(1));
        var singleton = NewRecord(project.Id, source.Id, incrementalBatch.Id, "web-003", "carol@example.com", "Carol Jones", ["email:carol@example.com"], now.AddMinutes(1));

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incrementalBatch.Id, [review, singleton], 0.90, 0.75),
            CancellationToken.None);

        Assert.Equal(2, result.RecordsAdded);
        Assert.Equal(0, result.AutoMatches);
        Assert.Equal(1, result.ReviewTasks);
        Assert.Equal(2, result.SingletonClusters);
        Assert.Equal(2, result.GoldenRecordVersionsCreated);

        var reviewTask = Assert.Single(await store.ListReviewTasksAsync(project.Id, CancellationToken.None));
        Assert.Equal(review.Id, reviewTask.NewEntityRecordId);
        Assert.Equal(existing.Id, reviewTask.CandidateEntityRecordId);
        Assert.Equal("open", reviewTask.Status);

        var clusters = await store.ListClustersAsync(project.Id, CancellationToken.None);
        Assert.Equal(3, clusters.Count);
        Assert.Contains(clusters, c => c.Id == existingClusterId && c.MemberEntityRecordIds.SequenceEqual([existing.Id]));
        Assert.Contains(clusters, c => c.MemberEntityRecordIds.SequenceEqual([review.Id]));
        Assert.Contains(clusters, c => c.MemberEntityRecordIds.SequenceEqual([singleton.Id]));
    }

    [Fact]
    public async Task SaveIncrementalIngestAsync_BackfillsBlockingKeysForExistingRecords()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var initialBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var existing = NewRecord(project.Id, source.Id, initialBatch.Id, "crm-legacy", "legacy@example.com", "Legacy Person", [], now);
        var clusterId = Guid.NewGuid();
        var goldenId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [existing],
                [],
                [new Cluster { Id = clusterId, ProjectId = project.Id, MemberEntityRecordIds = [existing.Id], CreatedAt = now }],
                [new GoldenRecord
                {
                    Id = goldenId,
                    ProjectId = project.Id,
                    ClusterId = clusterId,
                    CurrentVersionId = versionId,
                    Fields = existing.Fields,
                    UpdatedAt = now
                }],
                [new GoldenRecordVersion
                {
                    Id = versionId,
                    GoldenRecordId = goldenId,
                    ProjectId = project.Id,
                    ClusterId = clusterId,
                    IngestBatchId = initialBatch.Id,
                    VersionNumber = 1,
                    Fields = existing.Fields,
                    CreatedAt = now
                }]),
            CancellationToken.None);

        var incrementalBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1), CancellationToken.None);
        var incoming = NewRecord(project.Id, source.Id, incrementalBatch.Id, "web-legacy", "legacy@example.com", "Legacy Updated", [], now.AddMinutes(1));

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incrementalBatch.Id, [incoming], 0.90, 0.75),
            CancellationToken.None);

        Assert.Equal(1, result.AutoMatches);
        var records = await store.ListEntityRecordsAsync(project.Id, CancellationToken.None);
        Assert.All(records, record => Assert.NotEmpty(record.BlockingKeys));
        Assert.Contains(incoming.Id, Assert.Single(await store.ListClustersAsync(project.Id, CancellationToken.None)).MemberEntityRecordIds);
    }

    [Fact]
    public async Task SaveCompletedBatchAsync_WhenSourceRecordAlreadyExists_ThrowsWithoutDuplicatingFallbackState()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var firstBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var duplicateBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now.AddMinutes(1), CancellationToken.None);
        var firstRecord = NewRecord(project.Id, source.Id, firstBatch.Id, "crm-dup", "dup@example.com", "Dup One", ["email:dup@example.com"], now);
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [firstRecord],
                [],
                [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [firstRecord.Id], CreatedAt = now }],
                [],
                []),
            CancellationToken.None);

        var duplicate = NewRecord(project.Id, source.Id, duplicateBatch.Id, "crm-dup", "dup@example.com", "Dup Two", ["email:dup@example.com"], now.AddMinutes(1));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveCompletedBatchAsync(
                new CompletedBatchMetadata(
                    [duplicate],
                    [],
                    [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [duplicate.Id], CreatedAt = now.AddMinutes(1) }],
                    [],
                    []),
                CancellationToken.None));

        Assert.Contains("Entity record already exists", ex.Message);
        Assert.Single(await store.ListEntityRecordsAsync(project.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SaveIncrementalIngestAsync_WhenIncomingSourceRecordIdsRepeat_Throws()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 2, now, CancellationToken.None);
        var left = NewRecord(project.Id, source.Id, batch.Id, "dup-incoming", "left@example.com", "Dup Left", ["email:left@example.com"], now);
        var right = NewRecord(project.Id, source.Id, batch.Id, "dup-incoming", "right@example.com", "Dup Right", ["email:right@example.com"], now);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [left, right], 0.90, 0.75),
                CancellationToken.None));

        Assert.Contains("Duplicate source record id", ex.Message);
        Assert.Empty(await store.ListEntityRecordsAsync(project.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SaveIncrementalIngestAsync_ResendWithDifferentValues_AppliesCorrectionInsteadOfThrowing()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-correction.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var original = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-001", now, ["email:alice"],
            new Dictionary<string, string> { ["id"] = "crm-001", ["source"] = "CRM", ["email"] = "alice@old.example.com", ["name"] = "Alice" });

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75), CancellationToken.None);

        var correctionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1), CancellationToken.None);
        var corrected = NewRecordWithFields(project.Id, source.Id, correctionBatch.Id, "crm-001", now.AddMinutes(1), ["email:alice-new"],
            new Dictionary<string, string> { ["id"] = "crm-001", ["source"] = "CRM", ["email"] = "alice@new.example.com", ["name"] = "Alice" });

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, correctionBatch.Id, [corrected], 0.90, 0.75), CancellationToken.None);

        Assert.Equal(1, result.RecordsCorrected);

        var currentRecords = await store.ListEntityRecordsAsync(project.Id, CancellationToken.None);
        var live = Assert.Single(currentRecords); // superseded original is excluded by default
        Assert.Equal("alice@new.example.com", live.Fields["email"]);
    }

    [Fact]
    public async Task SaveIncrementalIngestAsync_ResendWithIdenticalValues_IsNoOp()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-noop.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var original = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-001", now, ["email:alice"],
            new Dictionary<string, string> { ["id"] = "crm-001", ["source"] = "CRM", ["email"] = "alice@example.com" });

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75), CancellationToken.None);

        var retryBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1), CancellationToken.None);
        var identicalResend = NewRecordWithFields(project.Id, source.Id, retryBatch.Id, "crm-001", now.AddMinutes(1), ["email:alice"],
            new Dictionary<string, string> { ["id"] = "crm-001", ["source"] = "CRM", ["email"] = "alice@example.com" });

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, retryBatch.Id, [identicalResend], 0.90, 0.75), CancellationToken.None);

        Assert.Equal(0, result.RecordsCorrected);
        Assert.Equal(0, result.RecordsAdded); // nothing new was added either
        Assert.Single(await store.ListEntityRecordsAsync(project.Id, CancellationToken.None)); // still exactly one row
    }

    [Fact]
    public async Task SaveIncrementalIngestAsync_CorrectionOnNonIdentifierField_SupersededRecordNotMatchedAgainst()
    {
        // Regression for the bug where GetLinearCorpus/GetRecordsByIds did not filter out
        // superseded records: a correction that leaves the identifier field (email) unchanged
        // and only changes a non-identifier field (name) used to score ~0.98 against its own
        // now-superseded predecessor (identifier-weighted scoring floors to 0.98 on any exact
        // identifier match, regardless of how much the non-identifier fields disagree) and
        // auto-merge the corrected record with the very record it just replaced.
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-correction-non-identifier.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var original = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-001", now, ["email:alice"],
            new Dictionary<string, string> { ["id"] = "crm-001", ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Alice" });

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75), CancellationToken.None);
        var originalId = original.Id;

        var correctionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1), CancellationToken.None);
        // Identifier field (email) unchanged; only the non-identifier "name" field changes.
        var corrected = NewRecordWithFields(project.Id, source.Id, correctionBatch.Id, "crm-001", now.AddMinutes(1), ["email:alice"],
            new Dictionary<string, string> { ["id"] = "crm-001", ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Bob" });

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, correctionBatch.Id, [corrected], 0.90, 0.75), CancellationToken.None);

        Assert.Equal(1, result.RecordsCorrected);

        var edges = await store.ListMatchEdgesAsync(project.Id, CancellationToken.None);
        Assert.DoesNotContain(edges, e => e.LeftEntityRecordId == originalId || e.RightEntityRecordId == originalId);

        var clusters = await store.ListClustersAsync(project.Id, CancellationToken.None);
        Assert.DoesNotContain(clusters, c => c.MemberEntityRecordIds.Contains(originalId));
    }

    /// <summary>
    /// Regression for #69/#70: a single batch mixing all three ClassifyAndDetachCorrections
    /// outcomes (brand new, identical no-op resend, correction) exercises both counter bugs
    /// found in the whole-branch review after PR #63. #69 — RecordsAdded double-counted
    /// corrected records because Resolve's incomingRecords parameter IS recordsToResolve
    /// (new + corrected, no-ops already excluded), so a corrected record was counted both as
    /// "added" and as "corrected". #70 — the batch's stored RecordCount was reconciled against
    /// request.Records.Count (the raw incoming count, still including the dropped no-op) rather
    /// than recordsToResolve.Count (what was actually stored/resolved).
    /// </summary>
    [Fact]
    public async Task SaveIncrementalIngestAsync_MixedBatch_CountsOnlyGenuinelyNewRecordsAsAdded()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-mixed-counts.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);

        var initialBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        var noOpSource = NewRecordWithFields(project.Id, source.Id, initialBatch.Id, "crm-noop", now, ["email:noop"],
            new Dictionary<string, string> { ["id"] = "crm-noop", ["source"] = "CRM", ["email"] = "noop@example.com" });
        var toBeCorrected = NewRecordWithFields(project.Id, source.Id, initialBatch.Id, "crm-correct", now, ["email:correct-old"],
            new Dictionary<string, string> { ["id"] = "crm-correct", ["source"] = "CRM", ["email"] = "old@example.com" });

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, initialBatch.Id, [noOpSource, toBeCorrected], 0.90, 0.75),
            CancellationToken.None);

        // Raw incoming count of 3, matching what request.Records.Count would be below — the
        // stored count bug #70 fixes reconciles this down to 2 (the no-op is dropped).
        var mixedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 3, now.AddMinutes(1), CancellationToken.None);
        var brandNew = NewRecordWithFields(project.Id, source.Id, mixedBatch.Id, "web-new", now.AddMinutes(1), ["email:new"],
            new Dictionary<string, string> { ["id"] = "web-new", ["source"] = "Web", ["email"] = "new@example.com" });
        var identicalResend = NewRecordWithFields(project.Id, source.Id, mixedBatch.Id, "crm-noop", now.AddMinutes(1), ["email:noop"],
            new Dictionary<string, string> { ["id"] = "crm-noop", ["source"] = "CRM", ["email"] = "noop@example.com" });
        var corrected = NewRecordWithFields(project.Id, source.Id, mixedBatch.Id, "crm-correct", now.AddMinutes(1), ["email:correct-new"],
            new Dictionary<string, string> { ["id"] = "crm-correct", ["source"] = "CRM", ["email"] = "newvalue@example.com" });

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, mixedBatch.Id, [brandNew, identicalResend, corrected], 0.90, 0.75),
            CancellationToken.None);

        // Bug #69: only the genuinely new record should count as "added" — the corrected
        // record must not be double-counted as both added and corrected.
        Assert.Equal(1, result.RecordsAdded);
        Assert.Equal(1, result.RecordsCorrected);

        // Bug #70: the batch's stored RecordCount must reflect rows actually resolved (new +
        // corrected = 2), not the raw incoming count of 3 that still includes the dropped no-op.
        var storedBatch = (await store.ListIngestBatchesAsync(project.Id, CancellationToken.None))
            .Single(b => b.Id == mixedBatch.Id);
        Assert.Equal(2, storedBatch.RecordCount);
    }

    [Fact]
    public async Task SaveIncrementalIngestAsync_ResendWithDifferentValues_OnIndexedStore_RemovesSupersededFromIndex()
    {
        var indexDir = Path.Combine(_root, "lucene-correction-guard");
        var engine = MatchingDefaults.CreateEngine();
        var profile = DefaultMatchingProfileProvider.CreatePersonProfile();
        var provider = new DefaultMatchingProfileProvider([profile]);
        using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });
        var store = new FileMetadataStore(
            new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-indexed-guard.json") },
            engine, provider, index);
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var original = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-001", now, [],
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "a@old.example.com", ["name"] = "Alice" });
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75), CancellationToken.None);

        // Blocking keys must be generated the same way the store generates them for real
        // incoming records (engine.PrepareForStorage), or the query never builds a search term.
        var queryOldEmail = engine.PrepareForStorage(
            NewRecordWithFields(project.Id, source.Id, batch.Id, "probe", now.AddMinutes(1), [],
                new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "a@old.example.com", ["name"] = "Alice" }),
            profile);
        // Prove the probe is capable of finding the record before correction, so the later
        // Assert.Empty means "removed", not "this query never matches anything."
        Assert.Contains(index.Retrieve(queryOldEmail, [], profile), c => c.Id == original.Id);

        var correctionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1), CancellationToken.None);
        var corrected = NewRecordWithFields(project.Id, source.Id, correctionBatch.Id, "crm-001", now.AddMinutes(1), [],
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "a@new.example.com", ["name"] = "Alice" });

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, correctionBatch.Id, [corrected], 0.90, 0.75), CancellationToken.None);
        Assert.Equal(1, result.RecordsCorrected);

        // The superseded record's Lucene doc must be gone. The corrected record can still
        // legitimately appear in this same result set (it shares the "Alice" name blocking key
        // with the probe), so the assertion targets the superseded record's own id specifically.
        Assert.DoesNotContain(index.Retrieve(queryOldEmail, [], profile), c => c.Id == original.Id);

        // The correcting record IS indexed and retrievable under its new value.
        var queryNewEmail = engine.PrepareForStorage(
            NewRecordWithFields(project.Id, source.Id, correctionBatch.Id, "probe2", now.AddMinutes(1), [],
                new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "a@new.example.com", ["name"] = "Alice" }),
            profile);
        Assert.Contains(index.Retrieve(queryNewEmail, [], profile), c => c.Id == corrected.Id);
    }

    [Fact]
    public async Task SaveIncrementalIngestAsync_CorrectionLandsInReviewBandAgainstOwnSupersededSelf_DoesNotCreateReviewTaskForDeadRecord()
    {
        // Lucene's Remove() for the superseded record runs AFTER Resolve() (see the ordering
        // comment in SaveIncrementalIngestAsync), so Resolve's candidate search can still return
        // the superseded record's stale doc. The correcting record's own detach already ran
        // (ApplyMutations(correctionMutations) before Resolve), so the superseded record is no
        // longer in any active cluster — but nothing stopped CreateBatchReviewTasks from creating
        // a task referencing it anyway if the self-comparison lands in the review (not auto) band.
        //
        // Recipe from IncrementalResolverTests.ReviewBand_CreatesOpenReviewTask_WithBreakdown:
        // last_name exact match (weight 2.0, blocking) + first_name "Robert"/"Bob" fuzzy ~0.67
        // (weight 1.0, not blocking) = 0.89 weighted — inside [0.75, 0.90), review band, not auto.
        // Correcting only first_name keeps last_name (the sole blocking field here) unchanged, so
        // the corrected record's candidate search still finds its own superseded predecessor via
        // the shared "last_name:smith" blocking key.
        var indexDir = Path.Combine(_root, "lucene-correction-review-liveness");
        var engine = MatchingDefaults.CreateEngine();
        var profile = DefaultMatchingProfileProvider.CreatePersonProfile();
        var provider = new DefaultMatchingProfileProvider([profile]);
        using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });
        var store = new FileMetadataStore(
            new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-correction-review-liveness.json") },
            engine, provider, index);
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var original = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-001", now, [],
            new Dictionary<string, string> { ["source"] = "CRM", ["first_name"] = "Robert", ["last_name"] = "Smith" });
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75), CancellationToken.None);

        var correctionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1), CancellationToken.None);
        var corrected = NewRecordWithFields(project.Id, source.Id, correctionBatch.Id, "crm-001", now.AddMinutes(1), [],
            new Dictionary<string, string> { ["source"] = "CRM", ["first_name"] = "Bob", ["last_name"] = "Smith" });

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, correctionBatch.Id, [corrected], 0.90, 0.75), CancellationToken.None);
        Assert.Equal(1, result.RecordsCorrected);

        var reviewTasks = await store.ListReviewTasksAsync(project.Id, CancellationToken.None);
        Assert.DoesNotContain(reviewTasks, t => t.CandidateEntityRecordId == original.Id || t.NewEntityRecordId == original.Id);
    }

    [Fact]
    public async Task DeleteRecordsAsync_ExistingRecord_MarksDeletedAndDetachesFromCluster()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-deletion.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var original = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-001", now, ["email:alice"],
            new Dictionary<string, string> { ["id"] = "crm-001", ["source"] = "CRM", ["email"] = "alice@example.com" });

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75), CancellationToken.None);

        var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1), CancellationToken.None);
        var result = await store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-001"], CancellationToken.None);

        Assert.Equal(1, result.RecordsDeleted);
        Assert.Empty(await store.ListEntityRecordsAsync(project.Id, CancellationToken.None)); // no longer current

        var events = await store.ListRecordDeletedEventsAsync(project.Id, CancellationToken.None);
        var evt = Assert.Single(events);
        Assert.Equal("alice@example.com", evt.PreviousFields["email"]);
        // Every current record belongs to a cluster, even an unmatched one — MaterializeComponent
        // always materializes a (possibly single-member) cluster for an incoming component, so
        // there is no "unclustered" state to observe here; PreviousClusterId records that
        // singleton cluster's id, not null.
        Assert.NotNull(evt.PreviousClusterId);
    }

    [Fact]
    public async Task DeleteRecordsAsync_DeletingSameSourceRecordIdTwice_SecondCallThrows()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-deletion-twice.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var original = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-001", now, ["email:alice"],
            new Dictionary<string, string> { ["id"] = "crm-001", ["source"] = "CRM", ["email"] = "alice@example.com" });
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75), CancellationToken.None);

        var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1), CancellationToken.None);
        await store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-001"], CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-001"], CancellationToken.None));
        Assert.Contains("crm-001", ex.Message);
    }

    [Fact]
    public async Task DeleteRecordsAsync_UnknownSourceRecordId_Throws()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-deletion-unknown.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 0, now, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteRecordsAsync(project.Id, source.Id, batch.Id, ["nonexistent"], CancellationToken.None));
        Assert.Contains("nonexistent", ex.Message);
    }

    [Fact]
    public async Task DeleteRecordsAsync_UnknownProjectSourceOrBatch_Throws()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-deletion-provenance.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 0, now, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteRecordsAsync(Guid.NewGuid(), source.Id, batch.Id, ["x"], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteRecordsAsync(project.Id, Guid.NewGuid(), batch.Id, ["x"], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteRecordsAsync(project.Id, source.Id, Guid.NewGuid(), ["x"], CancellationToken.None));
    }

    [Fact]
    public async Task DeleteRecordsAsync_DuplicateSourceRecordIdInOneCall_Throws()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-deletion-dup.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 0, now, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteRecordsAsync(project.Id, source.Id, batch.Id, ["crm-001", "crm-001"], CancellationToken.None));
        Assert.Contains("Duplicate", ex.Message);
    }

    [Fact]
    public async Task DeleteRecordsAsync_OnIndexedStore_RemovesFromIndex()
    {
        var indexDir = Path.Combine(_root, "lucene-deletion-guard");
        var engine = MatchingDefaults.CreateEngine();
        var profile = DefaultMatchingProfileProvider.CreatePersonProfile();
        var provider = new DefaultMatchingProfileProvider([profile]);
        using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });
        var databasePath = Path.Combine(_root, "metadata-deletion-indexed-guard.json");
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = databasePath }, engine, provider, index);
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var original = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-001", now, [],
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "a@example.com" });
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75), CancellationToken.None);

        // Blocking keys must be generated the same way the store generates them for real
        // incoming records (engine.PrepareForStorage), or the query never builds a search term.
        var queryProbe = engine.PrepareForStorage(
            NewRecordWithFields(project.Id, source.Id, batch.Id, "probe", now.AddMinutes(1), [],
                new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "a@example.com" }),
            profile);
        // Prove the probe is capable of finding the record before deletion, so the later
        // Assert.Empty means "removed", not "this query never matches anything."
        Assert.Contains(index.Retrieve(queryProbe, [], profile), c => c.Id == original.Id);

        var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1), CancellationToken.None);
        var result = await store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-001"], CancellationToken.None);
        Assert.Equal(1, result.RecordsDeleted);

        // The deleted record's Lucene doc must be gone.
        Assert.Empty(index.Retrieve(queryProbe, [], profile));
    }

    [Fact]
    public async Task DeleteRecordsAsync_IndexAlreadyDrifted_SelfHealsBeforeRemoving()
    {
        // SaveIncrementalIngestAsync calls EnsureIndexCurrent before mutating the index;
        // DeleteRecordsAsync must too, or a pre-existing drift (e.g. from a prior crash) is never
        // repaired on the deletion path — it just silently persists.
        var indexDir = Path.Combine(_root, "lucene-deletion-selfheal");
        var engine = MatchingDefaults.CreateEngine();
        var profile = DefaultMatchingProfileProvider.CreatePersonProfile();
        var provider = new DefaultMatchingProfileProvider([profile]);
        using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });
        var store = new FileMetadataStore(
            new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-deletion-selfheal.json") },
            engine, provider, index);
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        var keep = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-keep", now, [],
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "keep@example.com" });
        var toDelete = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-delete", now, [],
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "delete@example.com" });
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [keep, toDelete], 0.90, 0.75), CancellationToken.None);

        // Simulate drift external to this deletion call — e.g. an earlier crash between a Lucene
        // commit and its durable write — by removing "keep"'s doc directly, without touching the
        // durable store. The db still considers it live; the index no longer does.
        index.Remove(keep.Id);
        index.Commit();

        var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1), CancellationToken.None);
        await store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-delete"], CancellationToken.None);

        // "keep" must be back in the index — EnsureIndexCurrent should have detected the drift
        // and rebuilt from live durable records before the deletion-specific removal ran.
        var queryKeep = engine.PrepareForStorage(
            NewRecordWithFields(project.Id, source.Id, batch.Id, "probe", now.AddMinutes(1), [],
                new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "keep@example.com" }),
            profile);
        Assert.Contains(index.Retrieve(queryKeep, [], profile), c => c.Id == keep.Id);
    }

    [Fact]
    public async Task SaveIncrementalIngestAsync_AfterDeletionOnIndexedStore_SubsequentIngestDoesNotResurrectDeletedCandidate()
    {
        // EnsureIndexCurrent compares the LIVE Lucene doc count to the durable EntityRecords
        // count and does a full Rebuild on any mismatch. Deletion keeps the tombstoned row in
        // EntityRecords (SupersededAt/DeletedAt is never a hard delete) while removing its live
        // Lucene doc, so that comparison must count only LIVE records on both sides — otherwise
        // the very next SaveIncrementalIngestAsync call sees a mismatch and rebuilds the index
        // from every EntityRecord including tombstoned ones, undoing the deletion.
        var indexDir = Path.Combine(_root, "lucene-deletion-drift");
        var engine = MatchingDefaults.CreateEngine();
        var profile = DefaultMatchingProfileProvider.CreatePersonProfile();
        var provider = new DefaultMatchingProfileProvider([profile]);
        using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });
        var store = new FileMetadataStore(
            new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-deletion-drift.json") },
            engine, provider, index);
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var original = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-001", now, [],
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "deleted@example.com" });
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75), CancellationToken.None);

        var queryProbe = engine.PrepareForStorage(
            NewRecordWithFields(project.Id, source.Id, batch.Id, "probe", now.AddMinutes(1), [],
                new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "deleted@example.com" }),
            profile);

        var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1), CancellationToken.None);
        await store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-001"], CancellationToken.None);
        Assert.Empty(index.Retrieve(queryProbe, [], profile));

        // An unrelated ingest call exercises EnsureIndexCurrent's count check again.
        var unrelatedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(2), CancellationToken.None);
        var unrelated = NewRecordWithFields(project.Id, source.Id, unrelatedBatch.Id, "crm-002", now.AddMinutes(2), [],
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "unrelated@example.com" });
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, unrelatedBatch.Id, [unrelated], 0.90, 0.75), CancellationToken.None);

        Assert.Empty(index.Retrieve(queryProbe, [], profile));
    }

    [Fact]
    public async Task DeleteRecordsAsync_MultipleIdsOneCall_AllTombstonedAndCounted()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-deletion-multi.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        var a = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-a", now, [],
            new Dictionary<string, string> { ["id"] = "crm-a", ["email"] = "a@example.com" });
        var b = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-b", now, [],
            new Dictionary<string, string> { ["id"] = "crm-b", ["email"] = "b@example.com" });
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [a, b], 0.90, 0.75), CancellationToken.None);

        var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1), CancellationToken.None);
        var result = await store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-a", "crm-b"], CancellationToken.None);

        Assert.Equal(2, result.RecordsDeleted);
        Assert.Empty(await store.ListEntityRecordsAsync(project.Id, CancellationToken.None));
        Assert.Equal(2, (await store.ListRecordDeletedEventsAsync(project.Id, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task SaveIncrementalIngestAsync_WhenAutoMatchesMultipleExistingClusters_MergesBridgedClusters()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var initialBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        var left = NewRecord(project.Id, source.Id, initialBatch.Id, "crm-left", "shared@example.com", "Left Person", ["email:shared@example.com"], now);
        var right = NewRecord(project.Id, source.Id, initialBatch.Id, "crm-right", "shared@example.com", "Right Person", ["email:shared@example.com"], now);
        var leftCluster = Guid.NewGuid();
        var rightCluster = Guid.NewGuid();
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [left, right],
                [],
                [
                    new Cluster { Id = leftCluster, ProjectId = project.Id, MemberEntityRecordIds = [left.Id], CreatedAt = now },
                    new Cluster { Id = rightCluster, ProjectId = project.Id, MemberEntityRecordIds = [right.Id], CreatedAt = now.AddSeconds(1) }
                ],
                [],
                []),
            CancellationToken.None);
        var incrementalBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1), CancellationToken.None);
        var incoming = NewRecord(project.Id, source.Id, incrementalBatch.Id, "web-shared", "shared@example.com", "Shared Person", ["email:shared@example.com"], now.AddMinutes(1));

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incrementalBatch.Id, [incoming], 0.90, 0.75),
            CancellationToken.None);

        Assert.Equal(0, result.ReviewTasks);
        Assert.True(result.AutoMatches >= 1);
        var clusters = await store.ListClustersAsync(project.Id, CancellationToken.None);
        Assert.Single(clusters);                                  // merged into one survivor
        Assert.Equal(leftCluster, clusters[0].Id);               // oldest CreatedAt wins
        Assert.Equal(3, clusters[0].MemberEntityRecordIds.Count); // left, right, incoming
        var merges = await store.ListClusterMergeEventsAsync(project.Id, CancellationToken.None);
        Assert.Single(merges);
        Assert.Equal(leftCluster, merges[0].SurvivorClusterId);
        Assert.Empty(await store.ListReviewTasksAsync(project.Id, CancellationToken.None));
    }

    [Fact]
    public void Constructor_AcceptsInjectedEngineAndProvider()
    {
        var path = Path.Combine(Path.GetTempPath(), "linkuity-ctor-" + Guid.NewGuid().ToString("N") + ".json");
        var engine = MatchingDefaults.CreateEngine();
        var provider = new DefaultMatchingProfileProvider([DefaultMatchingProfileProvider.CreatePersonProfile()]);

        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = path }, engine, provider, indexedRetrieval: null);

        Assert.NotNull(store);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static EntityRecord NewRecord(
        Guid projectId,
        Guid sourceId,
        Guid batchId,
        string sourceRecordId,
        string email,
        string name,
        IReadOnlyList<string> blockingKeys,
        DateTimeOffset createdAt)
        => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SourceId = sourceId,
            IngestBatchId = batchId,
            SourceRecordId = sourceRecordId,
            Fields = new Dictionary<string, string>
            {
                ["id"] = sourceRecordId,
                ["email"] = email,
                ["name"] = name
            },
            BlockingKeys = blockingKeys,
            CreatedAt = createdAt
        };

    [Fact]
    public async Task IncrementalIngest_PersistsEngineBlockingKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), "linkuity-keys-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = path });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now, CancellationToken.None);
        var record = new Linkuity.Core.Models.EntityRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            SourceId = source.Id,
            IngestBatchId = batch.Id,
            SourceRecordId = "p1",
            Fields = new Dictionary<string, string> { ["last_name"] = "Smith" },
            CreatedAt = now
        };

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [record], 0.90, 0.75),
            CancellationToken.None);

        var stored = await store.ListEntityRecordsAsync(project.Id, CancellationToken.None);
        Assert.Single(stored);
        // The durable person profile blocks on ["exact-value", "token-name"]; the
        // engine-derived token-name key proves blocking keys flow through the engine.
        Assert.Contains(stored[0].BlockingKeys, k => k.Equals("name:smith", StringComparison.OrdinalIgnoreCase));
    }

    private static EntityRecord NewRecordWithFields(
        Guid projectId,
        Guid sourceId,
        Guid batchId,
        string sourceRecordId,
        DateTimeOffset createdAt,
        IReadOnlyList<string> blockingKeys,
        IReadOnlyDictionary<string, string> fields)
        => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SourceId = sourceId,
            IngestBatchId = batchId,
            SourceRecordId = sourceRecordId,
            Fields = fields,
            BlockingKeys = blockingKeys,
            CreatedAt = createdAt
        };

    [Fact]
    public async Task ListRecordCorrectedEventsAsync_ReturnsEventsForCorrection()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-correction-events.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var original = NewRecordWithFields(project.Id, source.Id, batch.Id, "crm-001", now, [],
            new Dictionary<string, string> { ["id"] = "crm-001", ["source"] = "CRM", ["email"] = "a@old.example.com" });
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75), CancellationToken.None);

        var correctionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1), CancellationToken.None);
        var corrected = NewRecordWithFields(project.Id, source.Id, correctionBatch.Id, "crm-001", now.AddMinutes(1), [],
            new Dictionary<string, string> { ["id"] = "crm-001", ["source"] = "CRM", ["email"] = "a@new.example.com" });
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, correctionBatch.Id, [corrected], 0.90, 0.75), CancellationToken.None);

        var events = await store.ListRecordCorrectedEventsAsync(project.Id, CancellationToken.None);

        var evt = Assert.Single(events);
        Assert.Equal("a@old.example.com", evt.PreviousFields["email"]);
        Assert.Equal("a@new.example.com", evt.NewFields["email"]);
    }

    [Fact]
    public async Task SaveIncrementalIngestAsync_CorrectingBothMembersOfTwoMemberClusterInOneBatch_LeavesNoOrphanedGoldenRecord()
    {
        // Regression for #67: a 2-member cluster {A,B} with a golden record, where ONE batch
        // corrects BOTH members. Iteration 1 (correcting A) reduces the cluster to the survivor
        // {B} and, because a golden record already exists, recomputes+queues a new golden record
        // row (still keyed to this same cluster id) alongside it. Iteration 2 (correcting B) sees
        // the cluster's pending-reduced membership as already empty, so it tombstones the cluster
        // and queues GoldenRecordClusterIdsToClear for it. ApplyMutations used to run that clear
        // BEFORE the GoldenRecordsToUpsert loop, so it could only remove a golden row already
        // persisted in `db` — never one iteration 1 queued into the SAME MutationSet for the SAME
        // cluster id — leaving an orphaned golden record pointing at a now-tombstoned cluster.
        //
        // ListGoldenRecordsAsync filters by active cluster and would mask this: an orphaned golden
        // record pointing at a "merged" cluster is exactly what that filter excludes. So this test
        // reads the raw persisted state directly instead.
        var databasePath = Path.Combine(_root, "metadata-two-member-correction.json");
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = databasePath });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);

        var memberA = NewRecordWithFields(project.Id, source.Id, seedBatch.Id, "crm-a", now, [],
            new Dictionary<string, string> { ["id"] = "crm-a", ["name"] = "Alice" });
        var memberB = NewRecordWithFields(project.Id, source.Id, seedBatch.Id, "crm-b", now, [],
            new Dictionary<string, string> { ["id"] = "crm-b", ["name"] = "Alice" });
        var clusterId = Guid.NewGuid();
        var goldenId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [memberA, memberB],
                [],
                [
                    new Cluster
                    {
                        Id = clusterId, ProjectId = project.Id,
                        MemberEntityRecordIds = [memberA.Id, memberB.Id], CreatedAt = now
                    }
                ],
                [
                    new GoldenRecord
                    {
                        Id = goldenId, ProjectId = project.Id, ClusterId = clusterId, CurrentVersionId = versionId,
                        Fields = new Dictionary<string, string> { ["name"] = "Alice" }, UpdatedAt = now
                    }
                ],
                [
                    new GoldenRecordVersion
                    {
                        Id = versionId, GoldenRecordId = goldenId, ProjectId = project.Id, ClusterId = clusterId,
                        IngestBatchId = seedBatch.Id, VersionNumber = 1,
                        Fields = new Dictionary<string, string> { ["name"] = "Alice" }, CreatedAt = now
                    }
                ]),
            CancellationToken.None);

        var correctionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 2, now.AddMinutes(1), CancellationToken.None);
        var correctedA = NewRecordWithFields(project.Id, source.Id, correctionBatch.Id, "crm-a", now.AddMinutes(1), [],
            new Dictionary<string, string> { ["id"] = "crm-a", ["name"] = "Alice-corrected" });
        var correctedB = NewRecordWithFields(project.Id, source.Id, correctionBatch.Id, "crm-b", now.AddMinutes(1), [],
            new Dictionary<string, string> { ["id"] = "crm-b", ["name"] = "Alice-corrected-too" });

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, correctionBatch.Id, [correctedA, correctedB], 0.90, 0.75),
            CancellationToken.None);

        Assert.Equal(2, result.RecordsCorrected);

        var raw = System.Text.Json.JsonSerializer.Deserialize<Linkuity.Mdm.Resolution.ResolutionWorkingSet>(
            await File.ReadAllTextAsync(databasePath))!;
        var tombstoned = raw.Clusters.Single(c => c.Id == clusterId);
        Assert.Equal("merged", tombstoned.Status);
        Assert.DoesNotContain(raw.GoldenRecords, g => g.ClusterId == clusterId);
    }

    /// <summary>
    /// Regression for #76. GoldenRecordMerge.MergeFields collects field names across all cluster
    /// members case-insensitively, then picks ONE canonical casing and looks that up in each
    /// member's own Fields dictionary via plain TryGetValue. That only works if every member's
    /// dictionary uses a case-insensitive comparer. FileMetadataStore reloads its whole working
    /// set via System.Text.Json on every call (LoadAsync), and STJ deserializes a
    /// Dictionary&lt;string,string&gt; with the default (ordinal, case-SENSITIVE) comparer,
    /// regardless of what comparer the in-memory object had before serialization. So a record
    /// ingested in one call and then read back on a later call carries a case-sensitive Fields
    /// dictionary — exactly what happens here to "existing" once "incoming" triggers the reload
    /// on the second SaveIncrementalIngestAsync call.
    /// </summary>
    [Fact]
    public async Task SaveIncrementalIngestAsync_ConsensusMerge_DoesNotDropFieldFromMemberReloadedWithDifferentCasing()
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, "metadata-field-casing.json") });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var initialBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);

        // Built in-process with a case-insensitive comparer (the normal case for a freshly
        // constructed record), but this record round-trips through the JSON file store between
        // this call and the next one, so by the time the second ingest reads it back its Fields
        // dictionary has lost that comparer.
        var existing = NewRecordWithFields(project.Id, source.Id, initialBatch.Id, "crm-001", now, ["email:alice@example.com"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = "alice@example.com",
                ["Department"] = "Sales"
            });
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, initialBatch.Id, [existing], 0.90, 0.75), CancellationToken.None);

        // Auto-matches "existing" on email and joins its cluster. Its own Fields dictionary is
        // freshly built (case-insensitive, like real ingestion paths build it) and carries the
        // SAME field under different casing with no value of its own — present only so the
        // field-name union in MergeFields has two casings of "department" to pick a canonical
        // spelling from. "Sales" (on "existing") is the cluster's only real value for this field.
        var incrementalBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1), CancellationToken.None);
        var incoming = NewRecordWithFields(project.Id, source.Id, incrementalBatch.Id, "web-001", now.AddMinutes(1), ["email:alice@example.com"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = "alice@example.com",
                ["DEPARTMENT"] = ""
            });

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incrementalBatch.Id, [incoming], 0.90, 0.75), CancellationToken.None);

        Assert.Equal(1, result.AutoMatches);
        var golden = Assert.Single(await store.ListGoldenRecordsAsync(project.Id, CancellationToken.None));

        // If MergeByConsensus looks each member's value up using only the one canonical
        // field-name casing MergeFields chose, "existing"'s case-sensitive (post-reload)
        // dictionary won't match that casing and its "Sales" value is silently dropped, leaving
        // the golden record with no real value for this field at all.
        var departmentEntry = golden.Fields.FirstOrDefault(kv => string.Equals(kv.Key, "department", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Sales", departmentEntry.Value);
    }
}
