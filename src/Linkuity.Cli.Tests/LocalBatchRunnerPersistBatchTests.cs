using System.Text;
using Linkuity.Cli;
using Linkuity.Infrastructure.Local;

namespace Linkuity.Cli.Tests;

public sealed class LocalBatchRunnerPersistBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"linkuity-cli-persist-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task PersistBatch_ImportsCompletedArtifactsIntoDurableMetadata()
    {
        var metadataPath = Path.Combine(_root, "metadata.json");
        var artifactRoot = Path.Combine(_root, "artifacts");
        var jobId = Guid.NewGuid();
        var runner = new LocalBatchRunner();

        await runner.RunAsync(["project", "create", "--metadata", metadataPath, "--name", "Customer MDM", "--content-type", "person"], CancellationToken.None);
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        var project = Assert.Single(await store.ListProjectsAsync(CancellationToken.None));

        await runner.RunAsync(["source", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--name", "CRM"], CancellationToken.None);
        var source = Assert.Single(await store.ListSourcesAsync(project.Id, CancellationToken.None));

        await runner.RunAsync(["batch", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--source-id", source.Id.ToString(), "--job-id", jobId.ToString(), "--record-count", "2"], CancellationToken.None);
        var batch = Assert.Single(await store.ListIngestBatchesAsync(project.Id, CancellationToken.None));

        SeedCompletedArtifacts(artifactRoot, jobId);

        var exitCode = await runner.RunAsync(
            [
                "persist-batch",
                "--metadata", metadataPath,
                "--artifact-root", artifactRoot,
                "--job-id", jobId.ToString(),
                "--project-id", project.Id.ToString(),
                "--source-id", source.Id.ToString(),
                "--batch-id", batch.Id.ToString()
            ],
            CancellationToken.None);

        Assert.Equal(0, exitCode);

        var reloaded = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        Assert.Equal(2, (await reloaded.ListEntityRecordsAsync(project.Id, CancellationToken.None)).Count);
        Assert.Single(await reloaded.ListMatchEdgesAsync(project.Id, CancellationToken.None));
        Assert.Equal(2, Assert.Single(await reloaded.ListClustersAsync(project.Id, CancellationToken.None)).MemberEntityRecordIds.Count);
        Assert.Equal("alice@example.com", Assert.Single(await reloaded.ListGoldenRecordsAsync(project.Id, CancellationToken.None)).Fields["email"]);
        Assert.Equal(batch.Id, Assert.Single(await reloaded.ListGoldenRecordVersionsAsync(project.Id, CancellationToken.None)).IngestBatchId);
    }

    [Fact]
    public async Task ProjectCommands_CreateReadAndUpdateDurableMergePolicy()
    {
        var metadataPath = Path.Combine(_root, "metadata-policy.json");
        var policyPath = Path.Combine(_root, "policy.json");
        var updatedPolicyPath = Path.Combine(_root, "updated-policy.json");
        var runner = new LocalBatchRunner();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            policyPath,
            """
            {
              "mergeFields": [
                { "fieldName": "email", "sourcePriority": ["CRM", "Marketing"] }
              ]
            }
            """,
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            updatedPolicyPath,
            """
            {
              "mergeFields": [
                { "fieldName": "phone", "sourcePriority": ["Support", "CRM"] }
              ]
            }
            """,
            Encoding.UTF8);

        var createExit = await runner.RunAsync(
            [
                "project", "create",
                "--metadata", metadataPath,
                "--name", "Customer MDM",
                "--content-type", "person",
                "--merge-policy", policyPath
            ],
            CancellationToken.None);
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        var project = Assert.Single(await store.ListProjectsAsync(CancellationToken.None));

        Assert.Equal(0, createExit);
        Assert.Equal("email", project.MergeConfiguration!.MergeFields[0].FieldName);

        using var output = new StringWriter();
        var previousOut = Console.Out;
        Console.SetOut(output);
        try
        {
            var getExit = await runner.RunAsync(
                [
                    "project", "get",
                    "--metadata", metadataPath,
                    "--project-id", project.Id.ToString()
                ],
                CancellationToken.None);

            Assert.Equal(0, getExit);
        }
        finally
        {
            Console.SetOut(previousOut);
        }

        Assert.Contains("\"mergeConfiguration\"", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CRM", output.ToString(), StringComparison.Ordinal);

        var updateExit = await runner.RunAsync(
            [
                "project", "merge-policy", "set",
                "--metadata", metadataPath,
                "--project-id", project.Id.ToString(),
                "--merge-policy", updatedPolicyPath
            ],
            CancellationToken.None);
        var updated = await store.GetProjectAsync(project.Id, CancellationToken.None);

        Assert.Equal(0, updateExit);
        Assert.Equal("phone", updated!.MergeConfiguration!.MergeFields[0].FieldName);
        Assert.Equal(["Support", "CRM"], updated.MergeConfiguration.MergeFields[0].SourcePriority);
    }

    [Fact]
    public async Task IngestIncremental_AddsNewRecordsWithoutFullArtifactReprocessAndExportsReviewTasks()
    {
        var metadataPath = Path.Combine(_root, "metadata-incremental.json");
        var artifactRoot = Path.Combine(_root, "artifacts-incremental");
        var inputPath = Path.Combine(_root, "incremental.csv");
        var reviewPath = Path.Combine(_root, "review.csv");
        var jobId = Guid.NewGuid();
        var runner = new LocalBatchRunner();

        await runner.RunAsync(["project", "create", "--metadata", metadataPath, "--name", "Customer MDM", "--content-type", "person"], CancellationToken.None);
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        var project = Assert.Single(await store.ListProjectsAsync(CancellationToken.None));
        await runner.RunAsync(["source", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--name", "CRM"], CancellationToken.None);
        var source = Assert.Single(await store.ListSourcesAsync(project.Id, CancellationToken.None));
        await runner.RunAsync(["batch", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--source-id", source.Id.ToString(), "--job-id", jobId.ToString(), "--record-count", "2"], CancellationToken.None);
        var initialBatch = Assert.Single(await store.ListIngestBatchesAsync(project.Id, CancellationToken.None));
        SeedCompletedArtifacts(artifactRoot, jobId);
        await runner.RunAsync(
            [
                "persist-batch",
                "--metadata", metadataPath,
                "--artifact-root", artifactRoot,
                "--job-id", jobId.ToString(),
                "--project-id", project.Id.ToString(),
                "--source-id", source.Id.ToString(),
                "--batch-id", initialBatch.Id.ToString()
            ],
            CancellationToken.None);
        await runner.RunAsync(["batch", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--source-id", source.Id.ToString(), "--record-count", "2"], CancellationToken.None);
        var incrementalBatch = (await store.ListIngestBatchesAsync(project.Id, CancellationToken.None)).Last();
        // web-002 has no email captured (blank) and shares the last name-token "Martinez" with the
        // pre-existing mkt-001 record (so token-name blocking retrieves it) with a real, partial
        // name similarity ("Alissa Martinez" vs "Alice Martinez" fuzzy 0.83): name is the only
        // comparable signal (weight 1.5), which clears the review-floor gate with a comfortable
        // margin but stays under the 0.90 auto threshold. A shared-email row would instead be
        // diluted below the gate by the mismatched email identifier (weight 3.0); an exact name
        // match would push the score to an outright auto-match instead of a review. (It targets
        // mkt-001 rather than the incoming web-001: a review edge whose candidate is itself an
        // incoming record that already auto-merged elsewhere in the same batch is intentionally
        // suppressed as redundant — see IncrementalResolver.CreateBatchReviewTasks.)
        await File.WriteAllTextAsync(
            inputPath,
            """
            id,source,name,email
            web-001,Web,Alice Verified,alice@example.com
            web-002,Web,Alissa Martinez,
            """,
            Encoding.UTF8);

        var ingestExit = await runner.RunAsync(
            [
                "ingest-incremental",
                "--metadata", metadataPath,
                "--project-id", project.Id.ToString(),
                "--source-id", source.Id.ToString(),
                "--batch-id", incrementalBatch.Id.ToString(),
                "--input", inputPath,
                "--auto-threshold", "0.90",
                "--review-threshold", "0.75"
            ],
            CancellationToken.None);

        Assert.Equal(0, ingestExit);
        var reloaded = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        Assert.Equal(4, (await reloaded.ListEntityRecordsAsync(project.Id, CancellationToken.None)).Count);
        Assert.Contains(await reloaded.ListClustersAsync(project.Id, CancellationToken.None), c => c.MemberEntityRecordIds.Count == 3);
        Assert.Contains(await reloaded.ListGoldenRecordVersionsAsync(project.Id, CancellationToken.None), v => v.IngestBatchId == incrementalBatch.Id);
        Assert.Single(await reloaded.ListReviewTasksAsync(project.Id, CancellationToken.None));

        var exportExit = await runner.RunAsync(
            [
                "review", "export",
                "--metadata", metadataPath,
                "--project-id", project.Id.ToString(),
                "--output", reviewPath
            ],
            CancellationToken.None);

        Assert.Equal(0, exportExit);
        var reviewCsv = await File.ReadAllTextAsync(reviewPath);
        Assert.Contains("new_entity_record_id,candidate_entity_record_id,score,reason,status", reviewCsv);
        Assert.Contains("review_threshold", reviewCsv);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static void SeedCompletedArtifacts(string artifactRoot, Guid jobId)
    {
        var jobPath = Path.Combine(artifactRoot, jobId.ToString());
        Directory.CreateDirectory(jobPath);
        File.WriteAllText(Path.Combine(jobPath, "metadata.json"), """{"id":""" + jobId + """}""", Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(jobPath, "normalized.csv"),
            """
            id,source,name,email
            crm-001,CRM,Alice,alice@example.com
            mkt-001,Marketing,Alice Martinez,alice@example.com
            """,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(jobPath, "matches.csv"),
            """
            left_id,right_id,similarity,fuzzy_similarity
            crm-001,mkt-001,0.99,0.99
            """,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(jobPath, "golden_records.csv"),
            """
            cluster_id,record_count,member_ids,email,name
            00000000-0000-0000-0000-000000000001,2,crm-001|mkt-001,alice@example.com,Alice
            """,
            Encoding.UTF8);
    }
}
