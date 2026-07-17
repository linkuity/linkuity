using System.Globalization;
using System.Text.Json;
using Linkuity.Core.Interfaces;
using Linkuity.Infrastructure.Local;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Infrastructure.Postgres;
using Linkuity.Matching.Profiles;
using Linkuity.Mdm.Benchmarks;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var subCommand = args[0];
var rest = args.Skip(1);

try
{
    return subCommand.ToLowerInvariant() switch
    {
        "generate" => RunGenerate(ParseOptions(rest)),
        "measure"  => await RunMeasureAsync(ParseOptions(rest), cts.Token),
        "measure-matching" => MatchingThroughputMeasurement.Run(ParseOptions(rest)),
        _          => PrintError($"Unknown sub-command '{subCommand}'. Expected: generate | measure | measure-matching"),
    };
}
catch (ArgumentException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 2;
}
catch (NotImplementedException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 3;
}

// ─── generate ────────────────────────────────────────────────────────────────

static int RunGenerate(Dictionary<string, string> opts)
{
    var total         = int.Parse(Required(opts, "total"), CultureInfo.InvariantCulture);
    var batchSize     = int.Parse(Required(opts, "batch-size"), CultureInfo.InvariantCulture);
    var sources       = Required(opts, "sources").Split(',', StringSplitOptions.RemoveEmptyEntries);
    var duplicateRate = double.Parse(Required(opts, "duplicate-rate"), CultureInfo.InvariantCulture);
    var seed          = int.Parse(Required(opts, "seed"), CultureInfo.InvariantCulture);
    var outPath       = Required(opts, "out");

    var generator = new SyntheticDatasetGenerator();
    var batches   = generator.Generate(new SyntheticDatasetOptions(
        total, batchSize, sources, duplicateRate, seed));

    // Project to serializable DTOs (avoids IReadOnlyDictionary/IReadOnlyList STJ edge-cases).
    var dtos = batches.Select(b => new BatchDto
    {
        Source  = b.Source,
        Records = b.Records.Select(r => new RecordDto
        {
            SourceRecordId = r.SourceRecordId,
            Fields         = new Dictionary<string, string>(r.Fields),
        }).ToArray(),
    }).ToArray();

    var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
    var json     = JsonSerializer.Serialize(dtos, jsonOpts);

    var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
    if (!string.IsNullOrEmpty(dir))
        Directory.CreateDirectory(dir);

    File.WriteAllText(outPath, json);
    Console.WriteLine($"Generated {batches.Count} batch(es) / {total} records → {outPath}");
    return 0;
}

// ─── measure ─────────────────────────────────────────────────────────────────

static async Task<int> RunMeasureAsync(Dictionary<string, string> opts, CancellationToken ct)
{
    var backend = Required(opts, "backend");

    var isPostgres = backend.Equals("postgres", StringComparison.OrdinalIgnoreCase);
    if (!isPostgres && !backend.Equals("file", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException($"Unknown --backend '{backend}'. Supported: file, postgres.");

    var dataPath  = Required(opts, "data");
    var workDir   = Required(opts, "work-dir");
    var outPrefix = Required(opts, "out");

    // Load dataset from JSON.
    var json = File.ReadAllText(dataPath);
    var dtos = JsonSerializer.Deserialize<BatchDto[]>(json,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Dataset JSON deserialized as null.");

    var batches = dtos
        .Select(d => new SyntheticBatch(
            d.Source,
            d.Records.Select(r => new SyntheticRecord(r.SourceRecordId, r.Fields)).ToList()))
        .ToList();

    Directory.CreateDirectory(workDir);
    var indexDir = Path.Combine(workDir, "lucene-index");

    // Both backends share a Lucene index built in the work dir (FuzzyMaxEdits=0).
    var maxCandidates = opts.TryGetValue("max-candidates", out var mc)
        ? int.Parse(mc, CultureInfo.InvariantCulture)
        : 50;
    // Default sequential (1); parallel ingest is opt-in and currently regresses throughput
    // (shared-reader stored-field retrieval serializes). See the parallel-ingest measurements.
    var ingestParallelism = opts.TryGetValue("ingest-parallelism", out var ipRaw)
        ? int.Parse(ipRaw, CultureInfo.InvariantCulture)
        : 1;
    using var luceneIndex = new LuceneCandidateRetrieval(
        new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir, FuzzyMaxEdits = 0, MaxCandidates = maxCandidates });
    var profileProvider = new DefaultMatchingProfileProvider(
        DefaultMatchingProfileProvider.BuiltInProfiles());

    IMetadataStore store;
    if (isPostgres)
    {
        var connectionString = Required(opts, "connection-string");
        DbUpMigrator.EnsureSchema(connectionString);
        store = new PostgresMetadataStore(
            new PostgresMetadataStoreOptions { ConnectionString = connectionString, IngestParallelism = ingestParallelism },
            engine: null,
            profileProvider,
            luceneIndex);
    }
    else
    {
        var dbPath = Path.Combine(workDir, "metadata.db");
        store = new FileMetadataStore(
            new FileMetadataStoreOptions { DatabasePath = dbPath },
            engine: null,
            profileProvider,
            luceneIndex);
    }

    var setup  = new MeasurementSetup(backend);
    Console.WriteLine($"Running measure: {batches.Count} batches, backend={backend}...");

    var report = await IngestMeasurement.RunAsync(() => store, batches, setup, ct);

    // Write outputs.
    var outDir = Path.GetDirectoryName(Path.GetFullPath(outPrefix));
    if (!string.IsNullOrEmpty(outDir))
        Directory.CreateDirectory(outDir);

    File.WriteAllText(outPrefix + ".csv", report.ToCsv());
    File.WriteAllText(outPrefix + ".md",  report.ToMarkdown());

    Console.WriteLine($"Wrote {outPrefix}.csv and {outPrefix}.md");
    Console.WriteLine();
    Console.WriteLine("  batch | cumulative_records | elapsed_ms | peak_ws_mb");
    Console.WriteLine("  ------|--------------------|-----------:|----------:");
    foreach (var row in report.Rows)
        Console.WriteLine(
            $"  {row.BatchIndex,5} | {row.CumulativeRecords,18} | {row.ElapsedMs,10:F0} | {row.PeakWorkingSetMb:F1}");

    return 0;
}

// ─── helpers ─────────────────────────────────────────────────────────────────

static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
{
    var tokens  = args.ToArray();
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

static string Required(IReadOnlyDictionary<string, string> opts, string name)
    => opts.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
        ? v
        : throw new ArgumentException($"Missing --{name}.");

static int PrintError(string message)
{
    Console.Error.WriteLine(message);
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Linkuity.Mdm.Benchmarks — ingest measurement harness

        Usage:
          generate --total N --batch-size N --sources CRM,Marketing --duplicate-rate 0.2 --seed 42 --out <path.json>
          measure  --backend file     --data <path.json> --work-dir <dir> --out <report-prefix> [--max-candidates N]
          measure  --backend postgres --data <path.json> --work-dir <dir> --out <report-prefix> --connection-string <cs> [--max-candidates N] [--ingest-parallelism N]
          measure-matching --corpus <n> --batch <n> [--max-candidates 50] [--iterations 3] [--index-dir <path>]

        Examples:
          dotnet run -- generate --total 10000 --batch-size 1000 --sources CRM,Marketing,Support --duplicate-rate 0.2 --seed 42 --out /tmp/bench.json
          dotnet run -- measure  --backend file --data /tmp/bench.json --work-dir /tmp/bench-file --out /tmp/file-baseline
          dotnet run -- measure  --backend postgres --data /tmp/bench.json --work-dir /tmp/bench-pg --out /tmp/pg-baseline --connection-string "Host=localhost;Database=linkuity;Username=postgres;Password=postgres"
        """);
}

// ─── JSON DTOs (serializable shapes for generate ↔ measure round-trip) ───────

/// <summary>Serializable proxy for <see cref="SyntheticBatch"/>.</summary>
internal sealed class BatchDto
{
    public string    Source  { get; set; } = "";
    public RecordDto[] Records { get; set; } = [];
}

/// <summary>Serializable proxy for <see cref="SyntheticRecord"/>.</summary>
internal sealed class RecordDto
{
    public string                      SourceRecordId { get; set; } = "";
    public Dictionary<string, string>  Fields         { get; set; } = new();
}
