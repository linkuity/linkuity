using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkuity.Cli.Tests;

/// <summary>
/// DOCUMENTS KNOWN DEFECTS (engine audit 2026-07-28, finding C1). The tests marked <c>Skip</c> in
/// this class FAIL today; the Skip keeps CI green. Remove the Skips when Phase 1 lands — they are
/// the acceptance criteria for that work.
///
/// The engine ships two supported production paths that resolve the same input independently:
///   - BATCH   — BatchRunService (CLI `run`, API `POST /run`): normalize → match → cluster →
///               GoldenRecordService.
///   - DURABLE — IMetadataStore.SaveIncrementalIngestAsync (CLI `ingest-incremental`):
///               IncrementalResolver → GoldenRecordMerge.
///
/// Both are invoked here FOR REAL, in-process, over the same sample CSV and the same profile.
/// Neither arm reads a pre-baked artifact — contrast samples/durable/full-vs-incremental-consistency,
/// which runs `persist-batch` over pre-computed artifacts on BOTH arms and therefore never executes
/// the batch matcher at all. BatchArm_ActuallyRunsTheMatcher and DurableArm_ActuallyRunsTheMatcher
/// guard against this class silently degrading into that same non-comparison.
///
/// WHAT THIS CLASS ESTABLISHED
///  1. On all three shipped sample corpora the two paths produce the SAME partition of records into
///     entities — bulk-loaded, one-at-a-time, and durable-versus-durable. Clustering parity holds
///     where it has been measured.
///  2. They nevertheless produce DIFFERENT golden records on every one of those corpora, because
///     the durable path stores un-normalized field values. That is the live, customer-visible bug.
///  3. Clustering parity is luckier than it looks. It survives only because every sample happens to
///     carry an identically-formatted date_of_birth inside each true cluster, and because
///     MatchKey.Normalize absorbs punctuation before the evaluators see it. Change either and the
///     partitions diverge — NormalizationDrift_PhoneCountryCode and NormalizationDrift_DateFormat
///     are two-record corpora that do exactly that.
///
/// Retrieval is deliberately CONTROLLED FOR throughout (no Lucene index, so both arms use
/// blocking-linear). Every divergence recorded here is therefore attributable to normalization, not
/// to the batch/durable retrieval-strategy difference.
/// </summary>
public sealed class BatchDurableParityTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    // ---------------------------------------------------------------- sample corpora

    public static TheoryData<string> Samples() => new()
    {
        "people-multi-source",
        "people-phone-noise",
        "organizations-multi-source",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Linkuity.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (Linkuity.slnx).");
    }

    private static (string Csv, MatchingProfile Profile, MergeConfiguration? Merge) LoadSample(string name)
    {
        var dir = Path.Combine(RepoRoot(), "samples", name);
        var csv = File.ReadAllText(Path.Combine(dir, "sample.csv"));
        var profile = new MatchingProfileConfigLoader()
            .LoadFromJson(File.ReadAllText(Path.Combine(dir, $"{name}.profile.json")), MatchingDefaults.CreateRegistry());

        MergeConfiguration? merge = null;
        var mergePath = Path.Combine(dir, $"{name}.merge.json");
        if (File.Exists(mergePath))
            merge = System.Text.Json.JsonSerializer.Deserialize<MergeConfiguration>(
                File.ReadAllText(mergePath),
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                });

        return (csv, profile, merge);
    }

    private string NewTempPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"linkuity-parity-{Guid.NewGuid():N}{suffix}");
        _tempPaths.Add(path);
        return path;
    }

    // ---------------------------------------------------------------- batch arm (real matcher)

    /// <summary>
    /// Runs the genuine batch pipeline: CsvNormalizationService → BatchMatchingService →
    /// PostProcessingService (GraphService union-find + GoldenRecordService).
    /// Returns the partition of source-record ids, plus the emitted match edges.
    /// </summary>
    private async Task<(Partition Partition, int EdgeCount)> RunBatchAsync(
        string csv, MatchingProfile profile, MergeConfiguration? merge)
    {
        var root = NewTempPath("-artifacts");
        var store = new FileSystemArtifactStore(new FileSystemArtifactStoreOptions { RootPath = root });

        var runService = new BatchRunService(
            new CsvNormalizationService(store),
            new BatchMatchingService(store),
            new PostProcessingService(store, new GraphService(), new GoldenRecordService(),
                NullLogger<PostProcessingService>.Instance),
            store);

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var result = await runService.RunAsync(profile, merge, input, CancellationToken.None);

        // matches.csv is the batch matcher's own output — proof the matcher ran.
        int edgeCount;
        await using (var matches = await store.DownloadAsync($"{result.JobId}/matches.csv"))
            edgeCount = ReadCsvRows(matches).Count;

        await using var golden = await store.DownloadAsync($"{result.JobId}/golden_records.csv");
        var goldenRows = ReadCsvRows(golden);
        var groups = goldenRows
            .Select(row => row["member_ids"].Split('|', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        // Canonical field values keyed by cluster membership, so the two arms can be lined up even
        // though their cluster IDs are unrelated (GoldenRecordService.cs:40 mints a fresh Guid).
        _lastBatchFields = goldenRows.ToDictionary(
            row => GroupKey(row["member_ids"].Split('|', StringSplitOptions.RemoveEmptyEntries)),
            row => row.Where(kvp => kvp.Key is not ("cluster_id" or "record_count" or "member_ids"))
                      .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.Ordinal);

        return (Partition.From(groups), edgeCount);
    }

    private Dictionary<string, Dictionary<string, string>> _lastBatchFields = new(StringComparer.Ordinal);
    private Dictionary<string, Dictionary<string, string>> _lastDurableFields = new(StringComparer.Ordinal);

    private static string GroupKey(IEnumerable<string> members)
        => string.Join("+", members.OrderBy(x => x, StringComparer.Ordinal));

    // ---------------------------------------------------------------- durable arm (real matcher)

    /// <summary>
    /// Runs the genuine durable pipeline through IMetadataStore.SaveIncrementalIngestAsync, which
    /// invokes IncrementalResolver (retrieval, scoring, banding, union-find, GoldenRecordMerge).
    /// <paramref name="chunkSize"/> = null feeds every record in one call (bulk load); = 1 feeds them
    /// one at a time (true incremental).
    /// No Lucene index is attached, so IncrementalResolver.cs:52 selects "blocking-linear" — the SAME
    /// retrieval family the batch arm forces at BatchMatchingService.cs:44. Retrieval is therefore
    /// CONTROLLED FOR, and any divergence this test reports is NOT explained by retrieval strategy.
    /// </summary>
    private async Task<(Partition Partition, int AutoMatches, int ReviewTasks)> RunDurableAsync(
        string csv, MatchingProfile profile, MergeConfiguration? merge, int? chunkSize)
    {
        var provider = new DefaultMatchingProfileProvider([profile]);
        var store = new FileMetadataStore(
            new FileMetadataStoreOptions { DatabasePath = NewTempPath(".json") },
            engine: null, provider, indexedRetrieval: null);

        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("parity", profile.ContentType, merge, now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "sample", now, CancellationToken.None);

        // The durable path takes records exactly as the CLI's ReadRawRows produces them
        // (LocalBatchRunner.cs:773-788, 805) — RAW CSV STRINGS, never passed through FieldNormalizer.
        using var reader = new StringReader(csv);
        var rows = ReadCsvRows(reader);

        var autoMatches = 0;
        var reviewTasks = 0;
        foreach (var chunk in rows.Chunk(chunkSize ?? rows.Count))
        {
            var batch = await store.CreateIngestBatchAsync(
                project.Id, source.Id, null, chunk.Length, DateTimeOffset.UtcNow, CancellationToken.None);

            var records = chunk.Select(fields => new EntityRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                SourceId = source.Id,
                IngestBatchId = batch.Id,
                SourceRecordId = fields["id"],
                Fields = fields,
                BlockingKeys = [],
                CreatedAt = DateTimeOffset.UtcNow,
            }).ToList();

            var result = await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(project.Id, source.Id, batch.Id, records,
                    profile.AutoMatchThreshold, profile.ReviewThreshold),
                CancellationToken.None);

            autoMatches += result.AutoMatches;
            reviewTasks += result.ReviewTasks;
        }

        var recordsById = (await store.ListEntityRecordsAsync(project.Id, CancellationToken.None))
            .ToDictionary(r => r.Id, r => r.SourceRecordId);
        var clusters = await store.ListClustersAsync(project.Id, CancellationToken.None);
        var groups = clusters
            .Select(c => c.MemberEntityRecordIds.Select(id => recordsById[id]).ToArray())
            .ToList();

        var membersByCluster = clusters.ToDictionary(
            c => c.Id, c => GroupKey(c.MemberEntityRecordIds.Select(id => recordsById[id])));
        _lastDurableFields = (await store.ListGoldenRecordsAsync(project.Id, CancellationToken.None))
            .Where(g => membersByCluster.ContainsKey(g.ClusterId))
            .ToDictionary(
                g => membersByCluster[g.ClusterId],
                g => g.Fields.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.Ordinal);

        return (Partition.From(groups), autoMatches, reviewTasks);
    }

    // ---------------------------------------------------------------- guards: both matchers really run

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task BatchArm_ActuallyRunsTheMatcher(string sample)
    {
        var (csv, profile, merge) = LoadSample(sample);
        var (partition, edgeCount) = await RunBatchAsync(csv, profile, merge);

        // A non-trivial partition and a non-empty matches.csv can only come from BatchMatchingService
        // having scored real pairs. No pre-baked artifact is involved.
        Assert.True(edgeCount > 0, $"{sample}: batch matcher emitted no edges — it did not run.");
        Assert.True(partition.Groups.Any(g => g.Length > 1), $"{sample}: batch produced no multi-record entity.");
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task DurableArm_ActuallyRunsTheMatcher(string sample)
    {
        var (csv, profile, merge) = LoadSample(sample);
        var (partition, autoMatches, _) = await RunDurableAsync(csv, profile, merge, chunkSize: null);

        Assert.True(autoMatches > 0, $"{sample}: durable resolver reported no auto matches — it did not run.");
        Assert.True(partition.Groups.Any(g => g.Length > 1), $"{sample}: durable produced no multi-record entity.");
    }

    // ---------------------------------------------------------------- the parity claim

    /// <summary>
    /// PASSES today. Bulk load: every record handed to the durable path in a single ingest call,
    /// versus the batch path over the same CSV. Retrieval is controlled for (both blocking-linear),
    /// thresholds are taken from the same profile, and the comparison is on AUTO-band clustering
    /// only — the batch path has no review concept (BatchMatchingService.cs:56,66), so nothing else
    /// is comparable.
    ///
    /// That it passes is NOT evidence the paths agree in general. Every shipped sample corpus
    /// carries an identically-formatted date_of_birth inside each true cluster, and date_of_birth is
    /// an exact-blocking Identifier field. The identifier floor fires on EVALUATOR score >= 1.0
    /// (IdentifierAwareWeightedScoringStrategy.cs:63-68), not on raw string equality, so the pair
    /// auto-merges on both arms regardless of whether FieldNormalizer ran. The normalization
    /// divergence is real but MASKED here. See NormalizationDrift_* below for corpora that expose it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public async Task BulkLoad_ProducesSameEntitiesAsBatch(string sample)
    {
        var (csv, profile, merge) = LoadSample(sample);

        var (batch, _) = await RunBatchAsync(csv, profile, merge);
        var (durable, _, _) = await RunDurableAsync(csv, profile, merge, chunkSize: null);

        AssertSamePartition(sample, batch, durable);
    }

    /// <summary>
    /// PASSES today, and for the same masking reason as BulkLoad_ProducesSameEntitiesAsBatch.
    /// True incremental: records arrive one at a time. Also incidentally shows the durable path is
    /// order-insensitive on these corpora.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public async Task OneAtATimeIngest_ProducesSameEntitiesAsBatch(string sample)
    {
        var (csv, profile, merge) = LoadSample(sample);

        var (batch, _) = await RunBatchAsync(csv, profile, merge);
        var (durable, _, _) = await RunDurableAsync(csv, profile, merge, chunkSize: 1);

        AssertSamePartition(sample, batch, durable);
    }

    /// <summary>
    /// PASSES today. Isolates the durable path's own internal consistency, independent of batch.
    /// Bulk-loading and one-at-a-time ingest are the same path with different call shapes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public async Task DurableBulkLoad_MatchesDurableOneAtATime(string sample)
    {
        var (csv, profile, merge) = LoadSample(sample);

        var (bulk, _, _) = await RunDurableAsync(csv, profile, merge, chunkSize: null);
        var (oneAtATime, _, _) = await RunDurableAsync(csv, profile, merge, chunkSize: 1);

        AssertSamePartition(sample, bulk, oneAtATime);
    }

    /// <summary>
    /// EXPECTED TO FAIL on all three samples — and it is the most customer-visible divergence found,
    /// because it fires on the SHIPPED corpora with no special fixture required.
    ///
    /// The two paths agree on the PARTITION here, so this compares the canonical field values each
    /// path produced for identically-membered clusters. Every observed difference is the `phone`
    /// field: batch stores E.164 ("+12125552000"), durable stores whatever the CSV said
    /// ("(212) 555-2000", "312.555.0147"). Same entity, different golden record.
    ///
    /// ATTRIBUTION MATTERS: this is audit finding C1(a) — normalization — surfacing in OUTPUT rather
    /// than in clustering. It is NOT evidence for C1(d). Both mergers selected the same member
    /// record's value; they simply had different data to select from. None of the eight documented
    /// behavioural differences between GoldenRecordService and GoldenRecordMerge (case-sensitivity
    /// of the merge-policy lookup and consensus grouping, cluster-local versus corpus-wide field
    /// universe, hard-coded "source" versus configurable source field, IsNullOrEmpty versus
    /// IsNullOrWhiteSpace) is exercised by these corpora — they are real in code but latent here.
    /// </summary>
    [Theory(Skip = "Documents known defect: durable golden records keep un-normalized field values (engine audit C1a). Phase 1 fix pending.")]
    [MemberData(nameof(Samples))]
    public async Task GoldenRecordFieldValues_AgreeAcrossPaths(string sample)
    {
        var (csv, profile, merge) = LoadSample(sample);

        var (batch, _) = await RunBatchAsync(csv, profile, merge);
        var (durable, _, _) = await RunDurableAsync(csv, profile, merge, chunkSize: null);

        // Only meaningful where the partitions already agree; if they do not, the other tests own it.
        Assert.Equal(batch.Canonical, durable.Canonical);

        var problems = new List<string>();
        foreach (var (cluster, batchFields) in _lastBatchFields.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!_lastDurableFields.TryGetValue(cluster, out var durableFields))
            {
                problems.Add($"  {cluster}: no durable golden record");
                continue;
            }

            foreach (var field in batchFields.Keys.Union(durableFields.Keys, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var b = batchFields.GetValueOrDefault(field, "<absent>");
                var d = durableFields.GetValueOrDefault(field, "<absent>");
                if (!string.Equals(b, d, StringComparison.Ordinal))
                    problems.Add($"  {cluster} [{field}]: batch='{b}' durable='{d}'");
            }
        }

        Assert.True(problems.Count == 0,
            $"{sample}: identical clusters produced different golden records ({problems.Count} field differences):\n" +
            string.Join("\n", problems.Take(40)));
    }

    // ------------------------------------------------- corpora that DO expose the divergence
    //
    // No shipped sample discriminates (see BulkLoad_ProducesSameEntitiesAsBatch). These two fixtures
    // are the minimum needed to isolate normalization: each is a single pair whose ONLY corroborating
    // evidence is one identifier field, written in two equally-valid formats. Everything else about
    // the two records is deliberately different so that without that field the weighted score falls
    // far below the review threshold. They share a last_name purely so token-name blocking still
    // retrieves the pair on the durable arm — otherwise the pair would never be scored and the test
    // would prove a weaker thing (a retrieval miss) than it claims (a normalization miss).

    private const string PeopleHeader =
        "id,source,first_name,last_name,email,phone,date_of_birth,address_line,postal_code,company\n";

    /// <summary>
    /// EXPECTED TO FAIL — engine audit finding C1(a), the normalization divergence, isolated in the
    /// scoring path.
    ///
    /// One phone number, two spellings that differ by the country code. FieldNormalizer rewrites
    /// BOTH to E.164 +14155550199 (FieldNormalizer.cs:29 → PhoneNormalizer), so on the BATCH arm the
    /// `exact` evaluator scores 1.0, the Identifier floor fires
    /// (IdentifierAwareWeightedScoringStrategy.cs:63-68) and the pair auto-merges into one entity.
    /// On the DURABLE arm the raw strings reach the evaluator unchanged (LocalBatchRunner.cs:773-788,
    /// 805 — ReadRawRows never calls FieldNormalizer), so `exact` compares "14155550199" against
    /// "4155550199", scores 0, no identifier matches, and the pair stays two entities.
    ///
    /// The country code is doing essential work here, and that is the finding: `exact` does NOT
    /// compare raw strings, it compares MatchKey.Normalize output (lowercase, letters+digits only),
    /// which already absorbs every punctuation-only difference — "(415) 555-0199" and "415-555-0199"
    /// both reduce to "4155550199" and match on BOTH arms without FieldNormalizer. The engine
    /// therefore has two independent normalization layers, and only the SEMANTIC residue that
    /// MatchKey cannot absorb (country code, date reformatting, honorifics) still diverges.
    /// </summary>
    [Fact(Skip = "Documents known defect: batch normalizes, durable does not (engine audit C1a). Phase 1 fix pending.")]
    public async Task NormalizationDrift_PhoneCountryCode_BatchMergesDurableDoesNot()
    {
        var csv = PeopleHeader +
            "p-1,CRM,Ann,Nolan,ann.nolan@example.com,+1 415-555-0199,1980-01-15,10 First St,94100,Nolan Co\n" +
            "p-2,Billing,Beth,Nolan,beth.nolan@example.net,(415) 555-0199,1974-06-02,22 Second Ave,94200,Other Inc\n";

        var (_, profile, merge) = LoadSample("people-multi-source");

        var (batch, _) = await RunBatchAsync(csv, profile, merge);
        var (durable, _, _) = await RunDurableAsync(csv, profile, merge, chunkSize: null);

        AssertSamePartition("phone country-code drift", batch, durable);
    }

    /// <summary>
    /// PASSES today, and is kept deliberately as the control for the fixture above: a
    /// punctuation-only phone difference does NOT diverge, because MatchKey.Normalize absorbs it
    /// before either evaluator sees it. Without this control, the country-code fixture would look
    /// like evidence that all phone formatting diverges, which is false and would over-scope the fix.
    /// </summary>
    [Fact]
    public async Task NormalizationDrift_PhonePunctuationOnly_BothPathsAgree()
    {
        var csv = PeopleHeader +
            "p-1,CRM,Ann,Nolan,ann.nolan@example.com,(415) 555-0199,1980-01-15,10 First St,94100,Nolan Co\n" +
            "p-2,Billing,Beth,Nolan,beth.nolan@example.net,415-555-0199,1974-06-02,22 Second Ave,94200,Other Inc\n";

        var (_, profile, merge) = LoadSample("people-multi-source");

        var (batch, _) = await RunBatchAsync(csv, profile, merge);
        var (durable, _, _) = await RunDurableAsync(csv, profile, merge, chunkSize: null);

        AssertSamePartition("phone punctuation drift", batch, durable);
    }

    /// <summary>
    /// EXPECTED TO FAIL — the same divergence reached through BLOCKING rather than scoring, and a
    /// correction to how the audit describes it.
    ///
    /// The audit's headline example (DOB `01/15/1980` vs `1980-01-15`) claims the durable pair "is
    /// never scored". Half of that is wrong: DateSimilarityEvaluator.TryParse uses format-agnostic
    /// DateTime.TryParse, so IF the pair is retrieved the durable arm scores it 1.0 and merges too.
    /// What actually differs is retrieval — blocking keys are built from the RAW field value, so the
    /// two spellings produce different date_of_birth keys on the durable arm. This fixture gives the
    /// pair no other shared blocking key at all (different last_name, email, phone, company), so the
    /// batch arm retrieves and merges it while the durable arm never generates a candidate.
    /// </summary>
    [Fact(Skip = "Documents known defect: batch normalizes, durable does not (engine audit C1a). Phase 1 fix pending.")]
    public async Task NormalizationDrift_DateFormat_BatchRetrievesDurableDoesNot()
    {
        var csv = PeopleHeader +
            "d-1,CRM,Carl,Osgood,carl.osgood@example.com,(415) 555-0111,1980-01-15,10 First St,94100,\n" +
            "d-2,Billing,Carl,Pruitt,carl.pruitt@example.net,(212) 555-0222,01/15/1980,10 First St,94100,\n";

        var (_, profile, merge) = LoadSample("people-multi-source");

        var (batch, _) = await RunBatchAsync(csv, profile, merge);
        var (durable, _, _) = await RunDurableAsync(csv, profile, merge, chunkSize: null);

        AssertSamePartition("date-format drift", batch, durable);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Compares two partitions and, on mismatch, reports which source records are grouped
    /// differently — not just that the canonical strings differ.
    /// </summary>
    private static void AssertSamePartition(string what, Partition batch, Partition durable)
    {
        if (batch.Canonical == durable.Canonical)
            return;

        var batchOf = batch.Groups.SelectMany(g => g.Select(id => (id, key: string.Join("+", g))))
            .ToDictionary(x => x.id, x => x.key, StringComparer.Ordinal);
        var durableOf = durable.Groups.SelectMany(g => g.Select(id => (id, key: string.Join("+", g))))
            .ToDictionary(x => x.id, x => x.key, StringComparer.Ordinal);

        var differing = batchOf.Keys.Union(durableOf.Keys, StringComparer.Ordinal)
            .Where(id => !string.Equals(
                batchOf.GetValueOrDefault(id, "<absent>"),
                durableOf.GetValueOrDefault(id, "<absent>"), StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => $"  {id}: batch={batchOf.GetValueOrDefault(id, "<absent>")} durable={durableOf.GetValueOrDefault(id, "<absent>")}");

        Assert.Fail(
            $"{what}: batch and durable produced different entities.\n" +
            $"batch   : {batch.Groups.Count} entities\n{batch.Canonical}\n" +
            $"durable : {durable.Groups.Count} entities\n{durable.Canonical}\n" +
            $"records clustered differently:\n{string.Join("\n", differing)}");
    }

    private sealed record Partition(IReadOnlyList<string[]> Groups, string Canonical)
    {
        public static Partition From(IEnumerable<string[]> groups)
        {
            var normalized = groups
                .Select(g => g.OrderBy(x => x, StringComparer.Ordinal).ToArray())
                .OrderBy(g => string.Join(",", g), StringComparer.Ordinal)
                .ToList();
            var canonical = string.Join("\n", normalized.Select(g => string.Join(",", g)));
            return new Partition(normalized, canonical);
        }
    }

    private static List<Dictionary<string, string>> ReadCsvRows(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return ReadCsvRows(reader);
    }

    private static List<Dictionary<string, string>> ReadCsvRows(TextReader reader)
    {
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        var rows = new List<Dictionary<string, string>>();
        if (!csv.Read()) return rows;
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? [];
        while (csv.Read())
            rows.Add(headers.ToDictionary(h => h, h => csv.GetField(h) ?? "", StringComparer.OrdinalIgnoreCase));
        return rows;
    }

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }
}
