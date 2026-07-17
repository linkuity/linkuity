using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
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
}
