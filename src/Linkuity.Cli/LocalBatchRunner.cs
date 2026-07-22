using System.Text.Json;
using System.Text.Json.Serialization;
using CsvHelper;
using CsvHelper.Configuration;
using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Infrastructure.Postgres;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Pipeline;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using CoreGoldenRecord = Linkuity.Core.Models.GoldenRecord;

namespace Linkuity.Cli;

public sealed class LocalBatchRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length > 0 && !string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
            return await RunMetadataCommandAsync(args, ct);

        if (!TryParseRunOptions(args, out var options, out var error))
        {
            await Console.Error.WriteLineAsync(error);
            return 2;
        }

        if (!File.Exists(options.InputPath))
        {
            await Console.Error.WriteLineAsync($"Input CSV not found: {options.InputPath}");
            return 2;
        }

        MatchingProfile profile;
        try
        {
            profile = ProfileResolver.ResolveNameOrFile(options.ProfilePath);
        }
        catch (MatchingProfileConfigException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        MergeConfiguration? merge = null;
        if (!string.IsNullOrWhiteSpace(options.MergePolicyPath))
        {
            try { merge = await ReadMergeConfigurationAsync(options.MergePolicyPath, ct); }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or JsonException)
            {
                await Console.Error.WriteLineAsync(ex.Message);
                return 2;
            }
        }

        var outputPath = Path.GetFullPath(options.OutputPath);
        var artifactRoot = Path.Combine(outputPath, "artifacts");
        Directory.CreateDirectory(outputPath);

        var store = new FileSystemArtifactStore(new FileSystemArtifactStoreOptions { RootPath = artifactRoot });
        var runService = new BatchRunService(
            new CsvNormalizationService(store),
            new BatchMatchingService(store),
            new PostProcessingService(store, new GraphService(), new GoldenRecordService(), NullLogger<PostProcessingService>.Instance),
            store);

        BatchRunResult result;
        await using (var input = File.OpenRead(options.InputPath))
            result = await runService.RunAsync(profile, merge, input, ct);
        var jobId = result.JobId;

        await CopyArtifactAsync(store, $"{jobId}/golden_records.csv", Path.Combine(outputPath, "golden-records.csv"), ct);

        if (options.WriteNeo4jExport)
            await WriteNeo4jExportAsync(store, jobId, profile, Path.Combine(outputPath, "neo4j-export.zip"), ct);

        Console.WriteLine($"Job {jobId} completed.");
        Console.WriteLine($"Golden records: {Path.Combine(outputPath, "golden-records.csv")}");
        return 0;
    }

    private static async Task<int> RunMetadataCommandAsync(string[] args, CancellationToken ct)
    {
        var optionOffset =
            string.Equals(args[0], "persist-batch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(args[0], "ingest-incremental", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 2;
        var options = ParseOptions(args.Skip(optionOffset));
        var profileProvider = BuildProfileProvider(options);

        int maxCandidates;
        try { maxCandidates = GetMaxCandidates(options); }
        catch (ArgumentException ex) { await Console.Error.WriteLineAsync(ex.Message); return 2; }

        int ingestParallelism;
        try { ingestParallelism = GetIngestParallelism(options); }
        catch (ArgumentException ex) { await Console.Error.WriteLineAsync(ex.Message); return 2; }

        options.TryGetValue("metadata-store", out var storeTypeName);
        var isPostgres = string.Equals(storeTypeName, "postgres", StringComparison.OrdinalIgnoreCase);

        if (isPostgres)
        {
            if (!options.TryGetValue("connection-string", out var connectionString) || string.IsNullOrWhiteSpace(connectionString))
            {
                await Console.Error.WriteLineAsync("Postgres backend requires --connection-string.");
                return 2;
            }

            options.TryGetValue("index-dir", out var indexDirOption);
            var indexDirectory = string.IsNullOrWhiteSpace(indexDirOption) ? ".linkuity/lucene-index" : indexDirOption;

            DbUpMigrator.EnsureSchema(connectionString);

            // Drive the durable path through the Lucene retrieval seam. The index is a derived
            // artifact beside the working dir; the `using` commits + disposes it on exit.
            // FuzzyMaxEdits = 0 keeps retrieval precise (exact blocking-key shares only).
            using var luceneIndex = new LuceneCandidateRetrieval(
                new LuceneCandidateRetrievalOptions { IndexDirectory = indexDirectory, FuzzyMaxEdits = 0, MaxCandidates = maxCandidates });
            IMetadataStore store = new PostgresMetadataStore(
                new PostgresMetadataStoreOptions { ConnectionString = connectionString, IngestParallelism = ingestParallelism },
                // engine is null by contract: when an indexed retrieval is supplied the store
                // builds its own index-backed engine internally and ignores this argument.
                engine: null,
                profileProvider,
                luceneIndex);
            return await DispatchMetadataCommandAsync(args, store, profileProvider, options, ct);
        }
        else
        {
            // file (default) — byte-for-byte the previous behavior
            if (!options.TryGetValue("metadata", out var metadataPath) || string.IsNullOrWhiteSpace(metadataPath))
            {
                await Console.Error.WriteLineAsync("Metadata commands require --metadata <path>.");
                return 2;
            }

            // Drive the durable path through the Lucene retrieval seam. The index is a derived
            // artifact that sits beside the metadata DB; the `using` commits + disposes it on exit.
            // FuzzyMaxEdits = 0 keeps retrieval precise (exact blocking-key shares only), so the
            // retrieved set matches the blocking-linear path and durable sample outcomes are preserved.
            var indexDirectory = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(metadataPath)) ?? ".",
                "lucene-index");
            using var luceneIndex = new LuceneCandidateRetrieval(
                new LuceneCandidateRetrievalOptions { IndexDirectory = indexDirectory, FuzzyMaxEdits = 0, MaxCandidates = maxCandidates });
            IMetadataStore store = new FileMetadataStore(
                new FileMetadataStoreOptions { DatabasePath = metadataPath },
                // engine is null by contract: when an indexed retrieval is supplied the store
                // builds its own index-backed engine internally and ignores this argument.
                engine: null,
                profileProvider,
                luceneIndex);
            return await DispatchMetadataCommandAsync(args, store, profileProvider, options, ct);
        }
    }

    private static async Task<int> DispatchMetadataCommandAsync(
        string[] args,
        IMetadataStore store,
        DefaultMatchingProfileProvider profileProvider,
        IReadOnlyDictionary<string, string> options,
        CancellationToken ct)
    {
        try
        {
            switch (args)
            {
                case ["project", "create", ..]:
                    return await CreateProjectCommandAsync(store, options, ct);
                case ["project", "get", ..]:
                    return await GetProjectCommandAsync(store, options, ct);
                case ["project", "merge-policy", "set", ..]:
                    return await SetProjectMergePolicyCommandAsync(store, options, ct);
                case ["source", "create", ..]:
                    return await CreateSourceCommandAsync(store, options, ct);
                case ["batch", "create", ..]:
                    return await CreateBatchCommandAsync(store, options, ct);
                case ["persist-batch", ..]:
                    return await PersistBatchCommandAsync(store, options, ct);
                case ["ingest-incremental", ..]:
                    return await IngestIncrementalCommandAsync(store, profileProvider, options, ct);
                case ["golden", "history", ..]:
                    return await GoldenHistoryCommandAsync(store, options, ct);
                case ["golden", "list", ..]:
                    return await ListGoldenRecordsCommandAsync(store, options, ct);
                case ["cluster", "list", ..]:
                    return await ListClustersCommandAsync(store, options, ct);
                case ["cluster", "merges", ..]:
                    return await ListClusterMergesCommandAsync(store, options, ct);
                case ["review", "export", ..]:
                    return await ExportReviewTasksCommandAsync(store, options, ct);
                case ["review", "list", ..]:
                    return await ListReviewTasksCommandAsync(store, options, ct);
                case ["match", "explain", ..]:
                    return await ExplainMatchesCommandAsync(store, options, ct);
                default:
                    await Console.Error.WriteLineAsync("Unknown command.");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FileNotFoundException or FormatException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }
    }

    private static async Task<int> CreateProjectCommandAsync(IMetadataStore store, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var mergeConfiguration = options.TryGetValue("merge-policy", out var policyPath)
            ? await ReadMergeConfigurationAsync(policyPath, ct)
            : null;
        var project = await store.CreateProjectAsync(
            Required(options, "name"),
            Required(options, "content-type"),
            mergeConfiguration,
            DateTimeOffset.UtcNow,
            ct);
        Console.WriteLine(project.Id);
        return 0;
    }

    private static async Task<int> GetProjectCommandAsync(IMetadataStore store, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var projectId = Guid.Parse(Required(options, "project-id"));
        var project = await store.GetProjectAsync(projectId, ct)
            ?? throw new InvalidOperationException($"Project not found: {projectId}");
        Console.WriteLine(JsonSerializer.Serialize(project, JsonOptions));
        return 0;
    }

    private static async Task<int> SetProjectMergePolicyCommandAsync(IMetadataStore store, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var projectId = Guid.Parse(Required(options, "project-id"));
        var mergeConfiguration = await ReadMergeConfigurationAsync(Required(options, "merge-policy"), ct);
        var project = await store.UpdateProjectMergePolicyAsync(projectId, mergeConfiguration, ct);
        Console.WriteLine(project.Id);
        return 0;
    }

    private static async Task<int> CreateSourceCommandAsync(IMetadataStore store, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var source = await store.CreateSourceAsync(
            Guid.Parse(Required(options, "project-id")),
            Required(options, "name"),
            DateTimeOffset.UtcNow,
            ct);
        Console.WriteLine(source.Id);
        return 0;
    }

    private static async Task<int> CreateBatchCommandAsync(IMetadataStore store, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var batch = await store.CreateIngestBatchAsync(
            Guid.Parse(Required(options, "project-id")),
            Guid.Parse(Required(options, "source-id")),
            options.TryGetValue("job-id", out var jobId) ? Guid.Parse(jobId) : null,
            options.TryGetValue("record-count", out var recordCount)
                ? int.Parse(recordCount, CultureInfo.InvariantCulture)
                : 0,
            DateTimeOffset.UtcNow,
            ct);
        Console.WriteLine(batch.Id);
        return 0;
    }

    private static async Task<int> PersistBatchCommandAsync(IMetadataStore store, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var artifactRoot = Required(options, "artifact-root");
        var jobId = Required(options, "job-id");
        var projectId = Guid.Parse(Required(options, "project-id"));
        var sourceId = Guid.Parse(Required(options, "source-id"));
        var batchId = Guid.Parse(Required(options, "batch-id"));
        var jobPath = Path.Combine(artifactRoot, jobId);
        var now = DateTimeOffset.UtcNow;

        var records = ReadNormalizedRecords(Path.Combine(jobPath, "normalized.csv"), projectId, sourceId, batchId, now);
        var recordIdBySourceId = records.ToDictionary(r => r.SourceRecordId, r => r.Id, StringComparer.OrdinalIgnoreCase);
        var edges = ReadMatchEdges(Path.Combine(jobPath, "matches.csv"), projectId, batchId, recordIdBySourceId, now);
        var golden = ReadGoldenRecords(Path.Combine(jobPath, "golden_records.csv"), projectId, batchId, recordIdBySourceId, now);

        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(records, edges, golden.Clusters, golden.GoldenRecords, golden.Versions),
            ct);
        Console.WriteLine($"Persisted batch {batchId}.");
        return 0;
    }

    private static async Task<int> IngestIncrementalCommandAsync(IMetadataStore store, DefaultMatchingProfileProvider profileProvider, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var projectId = Guid.Parse(Required(options, "project-id"));
        var sourceId = Guid.Parse(Required(options, "source-id"));
        var inputPath = Required(options, "input");
        var project = await store.GetProjectAsync(projectId, ct)
            ?? throw new InvalidOperationException($"Project not found: {projectId}");
        var profile = profileProvider.GetProfile(project.ContentType);
        var autoThreshold = options.TryGetValue("auto-threshold", out var autoValue)
            ? double.Parse(autoValue, CultureInfo.InvariantCulture)
            : profile.AutoMatchThreshold;
        var reviewThreshold = options.TryGetValue("review-threshold", out var reviewValue)
            ? double.Parse(reviewValue, CultureInfo.InvariantCulture)
            : profile.ReviewThreshold;

        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input CSV not found: {inputPath}", inputPath);

        // --batch-size N: chunk the input into N-record ingest calls, each its own auto-created
        // IngestBatch, bounding per-call memory to N. Omitting --batch-size preserves the
        // single-call path (which requires a pre-created --batch-id).
        int? batchSize = null;
        if (options.TryGetValue("batch-size", out var batchSizeRaw))
        {
            if (!int.TryParse(batchSizeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bs) || bs <= 0)
                throw new ArgumentException($"--batch-size must be a positive integer; got '{batchSizeRaw}'.");
            batchSize = bs;
        }

        var now = DateTimeOffset.UtcNow;

        if (batchSize is null)
        {
            var batchId = Guid.Parse(Required(options, "batch-id"));
            var records = ReadNormalizedRecords(inputPath, projectId, sourceId, batchId, now);
            var result = await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(projectId, sourceId, batchId, records, autoThreshold, reviewThreshold), ct);
            PrintIngestResult(result.RecordsAdded, result.AutoMatches, result.ReviewTasks, result.SingletonClusters, result.GoldenRecordVersionsCreated);
            return 0;
        }

        var rows = ReadRawRows(inputPath);
        int added = 0, auto = 0, reviews = 0, singletons = 0, versions = 0, batches = 0;
        for (var offset = 0; offset < rows.Count; offset += batchSize.Value)
        {
            var chunk = rows.Skip(offset).Take(batchSize.Value).ToList();
            var batch = await store.CreateIngestBatchAsync(projectId, sourceId, jobId: null, chunk.Count, now, ct);
            var records = BuildRecords(chunk, projectId, sourceId, batch.Id, now);
            var result = await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(projectId, sourceId, batch.Id, records, autoThreshold, reviewThreshold), ct);
            added += result.RecordsAdded; auto += result.AutoMatches; reviews += result.ReviewTasks;
            singletons += result.SingletonClusters; versions += result.GoldenRecordVersionsCreated; batches++;
        }

        Console.WriteLine($"Batches ingested: {batches}");
        PrintIngestResult(added, auto, reviews, singletons, versions);
        return 0;
    }

    private static void PrintIngestResult(int added, int auto, int reviews, int singletons, int versions)
    {
        Console.WriteLine($"Records added: {added}");
        Console.WriteLine($"Auto matches: {auto}");
        Console.WriteLine($"Review tasks: {reviews}");
        Console.WriteLine($"Singleton clusters: {singletons}");
        Console.WriteLine($"Golden versions created: {versions}");
    }

    private static async Task<int> ExportReviewTasksCommandAsync(IMetadataStore store, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var projectId = Guid.Parse(Required(options, "project-id"));
        var outputPath = Required(options, "output");
        var maxRows = GetMaxReadbackRows(options);
        var tasks = await store.ListReviewTasksAsync(projectId, ct);
        if (GuardReadBackSize("review export", tasks.Count, maxRows) is { } code) return code;

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream);
        await using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture));
        csv.WriteField("new_entity_record_id");
        csv.WriteField("candidate_entity_record_id");
        csv.WriteField("score");
        csv.WriteField("reason");
        csv.WriteField("status");
        await csv.NextRecordAsync();
        foreach (var task in tasks.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id))
        {
            csv.WriteField(task.NewEntityRecordId);
            csv.WriteField(task.CandidateEntityRecordId);
            csv.WriteField(task.Score);
            csv.WriteField(task.Reason);
            csv.WriteField(task.Status);
            await csv.NextRecordAsync();
        }

        Console.WriteLine($"Review tasks: {outputPath}");
        return 0;
    }

    private static async Task<int> ListClustersCommandAsync(IMetadataStore store, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var projectId = Guid.Parse(Required(options, "project-id"));
        options.TryGetValue("output", out var outputPath);
        var maxRows = GetMaxReadbackRows(options);

        var clusters = await store.ListClustersAsync(projectId, ct);
        if (GuardReadBackSize("cluster list", clusters.Count, maxRows) is { } code) return code;
        var sourceIdByRecordId = (await store.ListEntityRecordsAsync(projectId, ct))
            .ToDictionary(r => r.Id, r => r.SourceRecordId);

        var rows = clusters
            .OrderBy(c => c.Id)
            .Select(c => (IReadOnlyList<string>)new[]
            {
                c.Id.ToString(),
                c.MemberEntityRecordIds.Count.ToString(CultureInfo.InvariantCulture),
                string.Join("|", c.MemberEntityRecordIds
                    .Select(id => sourceIdByRecordId.TryGetValue(id, out var sourceRecordId) ? sourceRecordId : id.ToString())
                    .OrderBy(s => s, StringComparer.Ordinal))
            })
            .ToList();

        await WriteCsvAsync(["cluster_id", "record_count", "member_ids"], rows, outputPath, ct);
        return 0;
    }

    private static async Task<int> ListClusterMergesCommandAsync(IMetadataStore store, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var projectId = Guid.Parse(Required(options, "project-id"));
        options.TryGetValue("output", out var outputPath);
        var maxRows = GetMaxReadbackRows(options);

        var merges = await store.ListClusterMergeEventsAsync(projectId, ct);
        if (GuardReadBackSize("cluster merges", merges.Count, maxRows) is { } code) return code;
        var rows = merges
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.SurvivorClusterId)
            .Select(m => (IReadOnlyList<string>)new[]
            {
                m.SurvivorClusterId.ToString(),
                m.AbsorbedClusterId.ToString(),
                string.Join(";", m.TriggerRecordIds),
                m.Score.ToString(CultureInfo.InvariantCulture),
                m.IngestBatchId.ToString(),
                m.CreatedAt.ToString("O", CultureInfo.InvariantCulture)
            })
            .ToList();

        await WriteCsvAsync(
            ["survivor_cluster_id", "absorbed_cluster_id", "trigger_record_ids", "score", "ingest_batch_id", "created_at"],
            rows,
            outputPath,
            ct);
        return 0;
    }

    private static async Task<int> ListGoldenRecordsCommandAsync(IMetadataStore store, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var projectId = Guid.Parse(Required(options, "project-id"));
        options.TryGetValue("output", out var outputPath);
        var maxRows = GetMaxReadbackRows(options);

        var golden = await store.ListGoldenRecordsAsync(projectId, ct);
        if (GuardReadBackSize("golden list", golden.Count, maxRows) is { } code) return code;
        var versionsById = (await store.ListGoldenRecordVersionsAsync(projectId, ct)).ToDictionary(v => v.Id);
        var clustersById = (await store.ListClustersAsync(projectId, ct)).ToDictionary(c => c.Id);
        var sourceIdByRecordId = (await store.ListEntityRecordsAsync(projectId, ct)).ToDictionary(r => r.Id, r => r.SourceRecordId);

        var fieldNames = golden
            .SelectMany(g => g.Fields.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var headers = new List<string> { "cluster_id", "version", "record_count", "member_ids" };
        headers.AddRange(fieldNames);

        var rows = golden
            .OrderBy(g => g.ClusterId)
            .Select(g =>
            {
                var version = versionsById.TryGetValue(g.CurrentVersionId, out var v) ? v.VersionNumber : 0;
                var members = clustersById.TryGetValue(g.ClusterId, out var cluster)
                    ? cluster.MemberEntityRecordIds
                    : (IReadOnlyList<Guid>)[];
                var memberIds = string.Join("|", members
                    .Select(id => sourceIdByRecordId.TryGetValue(id, out var sourceRecordId) ? sourceRecordId : id.ToString())
                    .OrderBy(s => s, StringComparer.Ordinal));

                var row = new List<string>
                {
                    g.ClusterId.ToString(),
                    version.ToString(CultureInfo.InvariantCulture),
                    members.Count.ToString(CultureInfo.InvariantCulture),
                    memberIds
                };
                row.AddRange(fieldNames.Select(f => g.Fields.TryGetValue(f, out var value) ? value : ""));
                return (IReadOnlyList<string>)row;
            })
            .ToList();

        await WriteCsvAsync(headers, rows, outputPath, ct);
        return 0;
    }

    private static async Task<int> GoldenHistoryCommandAsync(IMetadataStore store, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var projectId = Guid.Parse(Required(options, "project-id"));
        options.TryGetValue("output", out var outputPath);
        var clusterFilter = options.TryGetValue("cluster-id", out var clusterText)
            ? Guid.Parse(clusterText)
            : (Guid?)null;
        var maxRows = GetMaxReadbackRows(options);

        var allVersions = await store.ListGoldenRecordVersionsAsync(projectId, ct);
        if (GuardReadBackSize("golden history", allVersions.Count, maxRows) is { } code) return code;
        var versions = allVersions
            .Where(v => clusterFilter is null || v.ClusterId == clusterFilter)
            .ToList();

        var fieldNames = versions
            .SelectMany(v => v.Fields.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var headers = new List<string> { "cluster_id", "version", "created_at" };
        headers.AddRange(fieldNames);

        var rows = versions
            .OrderBy(v => v.ClusterId)
            .ThenBy(v => v.VersionNumber)
            .Select(v =>
            {
                var row = new List<string>
                {
                    v.ClusterId.ToString(),
                    v.VersionNumber.ToString(CultureInfo.InvariantCulture),
                    v.CreatedAt.ToString("O", CultureInfo.InvariantCulture)
                };
                row.AddRange(fieldNames.Select(f => v.Fields.TryGetValue(f, out var value) ? value : ""));
                return (IReadOnlyList<string>)row;
            })
            .ToList();

        await WriteCsvAsync(headers, rows, outputPath, ct);
        return 0;
    }

    private static async Task<int> ListReviewTasksCommandAsync(IMetadataStore store, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var projectId = Guid.Parse(Required(options, "project-id"));
        options.TryGetValue("output", out var outputPath);
        var maxRows = GetMaxReadbackRows(options);

        var tasks = await store.ListReviewTasksAsync(projectId, ct);
        if (GuardReadBackSize("review list", tasks.Count, maxRows) is { } code) return code;
        var rows = tasks
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Select(t => (IReadOnlyList<string>)new[]
            {
                t.NewEntityRecordId.ToString(),
                t.CandidateEntityRecordId.ToString(),
                t.Score.ToString(CultureInfo.InvariantCulture),
                t.Reason,
                t.Status
            })
            .ToList();

        await WriteCsvAsync(
            ["new_entity_record_id", "candidate_entity_record_id", "score", "reason", "status"],
            rows,
            outputPath,
            ct);
        return 0;
    }

    private static async Task<int> ExplainMatchesCommandAsync(IMetadataStore store, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var projectId = Guid.Parse(Required(options, "project-id"));
        options.TryGetValue("output", out var outputPath);
        options.TryGetValue("edge-id", out var edgeIdRaw);
        options.TryGetValue("left", out var leftFilter);
        options.TryGetValue("right", out var rightFilter);
        var includeReviews = options.ContainsKey("include-reviews");
        var maxRows = GetMaxReadbackRows(options);

        // Load primary collection first so the guard fires before any secondary join.
        var edges = await store.ListMatchEdgesAsync(projectId, ct);
        if (GuardReadBackSize("match explain", edges.Count, maxRows) is { } code) return code;

        var sourceIdByRecordId = (await store.ListEntityRecordsAsync(projectId, ct))
            .ToDictionary(r => r.Id, r => r.SourceRecordId);
        string Display(Guid id) => sourceIdByRecordId.TryGetValue(id, out var srid) ? srid : id.ToString();

        bool PairMatches(Guid leftId, Guid rightId)
        {
            if (string.IsNullOrWhiteSpace(leftFilter) && string.IsNullOrWhiteSpace(rightFilter))
                return true;
            var ends = new[] { Display(leftId), Display(rightId) };
            bool Has(string? f) => f is null || ends.Contains(f, StringComparer.OrdinalIgnoreCase);
            return Has(leftFilter) && Has(rightFilter);
        }

        var rows = new List<IReadOnlyList<string>>();

        var edgeIdFilter = edgeIdRaw is null ? (Guid?)null : Guid.Parse(edgeIdRaw);
        foreach (var edge in edges
            .Where(e => edgeIdFilter is null || e.Id == edgeIdFilter)
            .Where(e => PairMatches(e.LeftEntityRecordId, e.RightEntityRecordId))
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id))
        {
            rows.AddRange(ExplainRows(
                edge.Id.ToString(),
                Display(edge.LeftEntityRecordId),
                Display(edge.RightEntityRecordId),
                edge.Score,
                edge.Decision,
                edge.Breakdown));
        }

        if (includeReviews && edgeIdRaw is null)
        {
            var tasks = await store.ListReviewTasksAsync(projectId, ct);
            foreach (var task in tasks
                .Where(t => PairMatches(t.NewEntityRecordId, t.CandidateEntityRecordId))
                .OrderBy(t => t.CreatedAt)
                .ThenBy(t => t.Id))
            {
                rows.AddRange(ExplainRows(
                    task.Id.ToString(),
                    Display(task.NewEntityRecordId),
                    Display(task.CandidateEntityRecordId),
                    task.Score,
                    "review",
                    task.Breakdown));
            }
        }

        await WriteCsvAsync(
            ["edge_id", "left_record", "right_record", "score", "decision", "signal", "value", "weight", "contribution"],
            rows,
            outputPath,
            ct);
        return 0;
    }

    private static IEnumerable<IReadOnlyList<string>> ExplainRows(
        string id, string left, string right, double score, string decision, IReadOnlyList<MatchScoreFactor> breakdown)
    {
        var scoreText = score.ToString(CultureInfo.InvariantCulture);
        if (breakdown.Count == 0)
        {
            yield return [id, left, right, scoreText, decision, "", "", "", ""];
            yield break;
        }

        foreach (var factor in breakdown)
            yield return
            [
                id, left, right, scoreText, decision,
                factor.Signal,
                factor.Value.ToString(CultureInfo.InvariantCulture),
                factor.Weight.ToString(CultureInfo.InvariantCulture),
                factor.Contribution.ToString(CultureInfo.InvariantCulture)
            ];
    }

    private static async Task<MergeConfiguration> ReadMergeConfigurationAsync(string configPath, CancellationToken ct)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Merge policy not found: {configPath}", configPath);

        await using var stream = File.OpenRead(configPath);
        return await JsonSerializer.DeserializeAsync<MergeConfiguration>(stream, JsonOptions, ct)
            ?? throw new InvalidOperationException($"Merge policy is empty: {configPath}");
    }

    private static async Task CopyArtifactAsync(FileSystemArtifactStore store, string artifactPath, string destinationPath, CancellationToken ct)
    {
        await using var source = await store.DownloadAsync(artifactPath, ct);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, ct);
    }

    private static async Task WriteNeo4jExportAsync(FileSystemArtifactStore store, Guid jobId, MatchingProfile profile, string destinationPath, CancellationToken ct)
    {
        var service = new Neo4jExportService(store);
        var result = await service.OpenZipAsync(jobId, profile, ct);
        if (result is not Neo4jExportResult.Ready ready)
            throw new InvalidOperationException($"Neo4j export was not ready for job {jobId}.");

        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await ready.Content.CopyToAsync(destination, ct);
    }

    private static bool TryParseRunOptions(string[] args, out RunOptions options, out string error)
    {
        options = new RunOptions("", "", null, "", false);
        error = "";

        if (args.Length == 0 || !string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            error = "Usage: linkuity run --input <sample.csv> --profile <name|profile.json> [--merge-policy <merge.json>] --output <directory> [--neo4j-export]";
            return false;
        }

        string? input = null;
        string? profile = null;
        string? mergePolicy = null;
        string? output = null;
        var neo4j = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input" when i + 1 < args.Length:
                    input = args[++i];
                    break;
                case "--profile" when i + 1 < args.Length:
                    profile = args[++i];
                    break;
                case "--merge-policy" when i + 1 < args.Length:
                    mergePolicy = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--neo4j-export":
                    neo4j = true;
                    break;
                default:
                    error = $"Unknown or incomplete option: {args[i]}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(input) ||
            string.IsNullOrWhiteSpace(profile) ||
            string.IsNullOrWhiteSpace(output))
        {
            error = "The run command requires --input, --profile, and --output.";
            return false;
        }

        options = new RunOptions(input, profile, mergePolicy, output, neo4j);
        return true;
    }

    private static List<(string SourceRecordId, Dictionary<string, string> Fields)> ReadRawRows(string path)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? [];
        var rows = new List<(string, Dictionary<string, string>)>();
        while (csv.Read())
        {
            var sourceRecordId = csv.GetField("id") ?? throw new InvalidOperationException("normalized.csv row is missing id.");
            var fields = headers.ToDictionary(h => h, h => csv.GetField(h) ?? "", StringComparer.OrdinalIgnoreCase);
            rows.Add((sourceRecordId, fields));
        }
        return rows;
    }

    private static List<EntityRecord> BuildRecords(
        IEnumerable<(string SourceRecordId, Dictionary<string, string> Fields)> rows,
        Guid projectId, Guid sourceId, Guid batchId, DateTimeOffset createdAt)
        => rows.Select(row => new EntityRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SourceId = sourceId,
            IngestBatchId = batchId,
            SourceRecordId = row.SourceRecordId,
            Fields = row.Fields,
            BlockingKeys = [],
            CreatedAt = createdAt
        }).ToList();

    private static List<EntityRecord> ReadNormalizedRecords(string path, Guid projectId, Guid sourceId, Guid batchId, DateTimeOffset createdAt)
        => BuildRecords(ReadRawRows(path), projectId, sourceId, batchId, createdAt);

    private static List<MatchEdge> ReadMatchEdges(
        string path,
        Guid projectId,
        Guid batchId,
        IReadOnlyDictionary<string, Guid> recordIdBySourceId,
        DateTimeOffset createdAt)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        csv.Read();
        csv.ReadHeader();
        var edges = new List<MatchEdge>();
        while (csv.Read())
        {
            var left = csv.GetField("left_id") ?? "";
            var right = csv.GetField("right_id") ?? "";
            if (!recordIdBySourceId.TryGetValue(left, out var leftId) ||
                !recordIdBySourceId.TryGetValue(right, out var rightId))
                continue;

            var scoreText = csv.GetField("similarity") ?? csv.GetField("fuzzy_similarity") ?? "0";
            edges.Add(new MatchEdge
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                IngestBatchId = batchId,
                LeftEntityRecordId = leftId,
                RightEntityRecordId = rightId,
                Score = double.Parse(scoreText, CultureInfo.InvariantCulture),
                Method = "batch",
                CreatedAt = createdAt
            });
        }

        return edges;
    }

    private static PersistedGoldenRecords ReadGoldenRecords(
        string path,
        Guid projectId,
        Guid batchId,
        IReadOnlyDictionary<string, Guid> recordIdBySourceId,
        DateTimeOffset createdAt)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? [];
        var clusters = new List<Cluster>();
        var goldenRecords = new List<CoreGoldenRecord>();
        var versions = new List<GoldenRecordVersion>();

        while (csv.Read())
        {
            var clusterIdText = csv.GetField("cluster_id") ?? "";
            var clusterId = Guid.TryParse(clusterIdText, out var parsedClusterId)
                ? parsedClusterId
                : Guid.NewGuid();
            var memberIds = (csv.GetField("member_ids") ?? "")
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(recordIdBySourceId.ContainsKey)
                .Select(memberId => recordIdBySourceId[memberId])
                .ToList();
            var fields = headers
                .Where(h => h is not "cluster_id" and not "record_count" and not "member_ids")
                .ToDictionary(h => h, h => csv.GetField(h) ?? "", StringComparer.OrdinalIgnoreCase);
            var goldenRecordId = Guid.NewGuid();
            var versionId = Guid.NewGuid();

            clusters.Add(new Cluster
            {
                Id = clusterId,
                ProjectId = projectId,
                MemberEntityRecordIds = memberIds,
                CreatedAt = createdAt
            });
            goldenRecords.Add(new CoreGoldenRecord
            {
                Id = goldenRecordId,
                ProjectId = projectId,
                ClusterId = clusterId,
                CurrentVersionId = versionId,
                Fields = fields,
                UpdatedAt = createdAt
            });
            versions.Add(new GoldenRecordVersion
            {
                Id = versionId,
                GoldenRecordId = goldenRecordId,
                ProjectId = projectId,
                ClusterId = clusterId,
                IngestBatchId = batchId,
                VersionNumber = 1,
                Fields = fields,
                CreatedAt = createdAt
            });
        }

        return new PersistedGoldenRecords(clusters, goldenRecords, versions);
    }

    // Read-back commands always emit CSV to stdout (the durable sample harness parses
    // stdout for row assertions); --output is an optional additional copy to a file.
    private static async Task WriteCsvAsync(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        string? outputPath,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(stream);
            await WriteCsvRowsAsync(writer, headers, rows);
        }

        await WriteCsvRowsAsync(Console.Out, headers, rows);
    }

    private static async Task WriteCsvRowsAsync(
        TextWriter writer,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture, leaveOpen: true);
        foreach (var header in headers)
            csv.WriteField(header);
        await csv.NextRecordAsync();

        foreach (var row in rows)
        {
            foreach (var cell in row)
                csv.WriteField(cell);
            await csv.NextRecordAsync();
        }

        await csv.FlushAsync();
    }

    private static DefaultMatchingProfileProvider BuildProfileProvider(IReadOnlyDictionary<string, string> options)
    {
        var loaded = new List<MatchingProfile>();

        if (options.TryGetValue("profiles", out var profilesPath) && !string.IsNullOrWhiteSpace(profilesPath))
        {
            var registry = MatchingDefaults.CreateRegistry();
            var loader = new MatchingProfileConfigLoader();
            loaded.AddRange(Directory.Exists(profilesPath)
                ? loader.LoadFromDirectory(profilesPath, registry)
                : [loader.LoadFromFile(profilesPath, registry)]);
        }

        return new DefaultMatchingProfileProvider(
            DefaultMatchingProfileProvider.BuiltInProfiles(), loaded);
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var tokens = args.ToArray();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!tokens[i].StartsWith("--", StringComparison.Ordinal))
                continue;
            if (i + 1 >= tokens.Length || tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {tokens[i]}.");

            options[tokens[i][2..]] = tokens[++i];
        }

        return options;
    }

    // Returns the configured row limit, defaulting to 100 000 when the option is absent.
    // A non-numeric value throws ArgumentException (same convention as Required): the dispatcher
    // catch writes the message to Console.Error and returns exit code 2 — no unhandled FormatException.
    private static int GetMaxReadbackRows(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("max-readback-rows", out var raw))
            return 100_000;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            throw new ArgumentException($"--max-readback-rows must be an integer; got '{raw}'.");
        return n;
    }

    // Returns the configured Lucene Top-N candidate cap, defaulting to 50 when absent.
    // A non-numeric value throws ArgumentException (same convention as GetMaxReadbackRows):
    // the dispatcher catch writes the message to stderr and returns exit code 2.
    private static int GetMaxCandidates(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("max-candidates", out var raw))
            return 50;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            throw new ArgumentException($"--max-candidates must be a positive integer; got '{raw}'.");
        return n;
    }

    private static int GetIngestParallelism(IReadOnlyDictionary<string, string> options)
    {
        // Default = ProcessorCount. Milestone 26 made concurrent Lucene retrieval scale via
        // per-thread committed readers + leaner candidate reconstruction (measured 3.33x vs the
        // sequential baseline at 20 cores, up from M25's 0.26x regression); parallel edge production
        // is now on by default. Set --ingest-parallelism 1 to force sequential. See
        // docs/roadmap/measurements/2026-07-05-ingest-retrieval-cost/.
        if (!options.TryGetValue("ingest-parallelism", out var raw))
            return Environment.ProcessorCount;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            throw new ArgumentException($"--ingest-parallelism must be a positive integer; got '{raw}'.");
        return n;
    }

    // Returns exit code 3 if the primary collection exceeds the threshold; null means proceed.
    // The guard fires BEFORE any secondary join so the multiplicative memory blow-up never happens.
    private static int? GuardReadBackSize(string label, int primaryCount, int maxRows)
    {
        if (primaryCount <= maxRows)
            return null;
        Console.Error.WriteLine(
            $"{label}: project has {primaryCount} rows, exceeding --max-readback-rows ({maxRows}). " +
            "CLI read-back loads the full result set in memory; use the PostgreSQL backend with direct SQL, " +
            "or wait for the Milestone 24 reporting/export feature. Raise --max-readback-rows to override.");
        return 3;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing --{name}.");

    private sealed record RunOptions(string InputPath, string ProfilePath, string? MergePolicyPath, string OutputPath, bool WriteNeo4jExport);
    private sealed record PersistedGoldenRecords(
        IReadOnlyList<Cluster> Clusters,
        IReadOnlyList<CoreGoldenRecord> GoldenRecords,
        IReadOnlyList<GoldenRecordVersion> Versions);
}
