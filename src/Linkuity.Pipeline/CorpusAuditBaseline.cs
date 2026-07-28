using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Linkuity.Pipeline;

/// <summary>
/// Everything the two runs must agree on before their numbers mean anything. Split in two by
/// <see cref="CorpusAuditBaseline.Compare"/>: the evaluation inputs (records, ground truth, corpus
/// source, frozen strata sidecar) can never differ, while the system-under-test configuration
/// (profile, block size, thresholds) may be acknowledged deliberately.
/// </summary>
public sealed record BaselineInputs(
    string RecordsSha256, string GroundTruthSha256, string ProfileSha256, string CorpusSourceSha256,
    string StrataSha256, int? MaxBlockSize,
    double AutoMatchThreshold, double ReviewThreshold, double ReviewFloorGate);

/// <summary>Raw counts only. Every gate inequality is evaluated on these integers, never on a
/// derived ratio, so no comparison can be lost to rounding.</summary>
public sealed record BaselineCounts(
    int Records, long TruePairs, long CandidatePairs,
    long ActualPositive, long PredictedPositive, long TruePositive, long ReachableTruePairs);

public sealed record BaselineStratum(Stratum Id, long TruePairs, long PostClusterTruePositive)
{
    /// <summary>Null (rendered "n/a") rather than 0.0 when the stratum is empty. Display only —
    /// the gate never compares this value.</summary>
    [JsonIgnore]
    public double? PostClusterPairwiseRecall => TruePairs == 0 ? null : (double)PostClusterTruePositive / TruePairs;

    /// <summary>"n/a" for an empty stratum, so an absent cohort never reads as a total miss.</summary>
    [JsonIgnore]
    public string RecallDisplay => PostClusterPairwiseRecall is { } r
        ? r.ToString("P2", CultureInfo.InvariantCulture)
        : "n/a";
}

/// <summary>One true pair's stratum as the BASELINE assigned it. Persisted beside baseline.json
/// so a later run can be bucketed into the baseline's cohorts rather than its own.</summary>
public sealed record FrozenStratumAssignment(string LeftId, string RightId, Stratum Stratum);

/// <summary>
/// <paramref name="Refused"/> and a non-empty <paramref name="Failures"/> are different outcomes and
/// must never be collapsed: refusal (CLI exit 2) means the runs are not comparable, failure (exit 1)
/// means they are comparable and something got worse. A refusal never carries failures.
/// </summary>
public sealed record BaselineComparison(
    bool Refused, string? RefusalReason, IReadOnlyList<string> Failures, long ReclassifiedPairs);

/// <summary>
/// The gate's machine-enforced contract. Refusal (exit 2) and failure (exit 1) are different:
/// refusal means the two runs are not comparable at all; failure means they are comparable and
/// something got worse. Every inequality uses BigInteger on raw counts — a rounded percentage
/// comparison would hide a real regression inside display tolerance.
/// </summary>
public sealed record CorpusAuditBaseline(
    int SchemaVersion, string CreatedUtc, BaselineInputs Inputs, BaselineCounts Counts,
    IReadOnlyList<BaselineStratum> Strata)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Per-stratum post-cluster recall may fall by at most this many percentage points.
    /// A judgement call, not a derived value: S5 holds ~34k pairs where boundary cases move
    /// without meaning. Precision and reachability have no tolerance.</summary>
    public const int ToleranceNumerator = 5;      // 5 / 1000 == 0.5 percentage points
    public const int ToleranceDenominator = 1000;

    public const string BaselineFileName = "baseline.json";
    public const string StrataFileName = "baseline-strata.csv";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ToJson(CorpusAuditBaseline baseline) => JsonSerializer.Serialize(baseline, JsonOptions);

    public static CorpusAuditBaseline FromJson(string json)
    {
        var baseline = JsonSerializer.Deserialize<CorpusAuditBaseline>(json, JsonOptions)
            ?? throw new ArgumentException("Baseline JSON did not deserialize to a baseline.", nameof(json));
        if (baseline.SchemaVersion != CurrentSchemaVersion)
            throw new ArgumentException(
                $"Baseline schemaVersion {baseline.SchemaVersion} is not supported " +
                $"(this build reads {CurrentSchemaVersion}). Regenerate the baseline deliberately.",
                nameof(json));
        return baseline;
    }

    public static CorpusAuditBaseline Create(CorpusAuditResult result, BaselineInputs inputs, string createdUtc)
        => new(CurrentSchemaVersion, createdUtc, inputs,
            new BaselineCounts(result.Counts.Records, result.Counts.TruePairs, result.Counts.CandidatePairs,
                result.Counts.ActualPositive, result.Counts.PredictedPositive, result.Counts.TruePositive,
                result.Counts.ReachableTruePairs),
            result.Strata.Select(s => new BaselineStratum(s.Id, s.TruePairs, s.PostClusterTruePositive)).ToList());

    // ---- frozen strata sidecar ----

    /// <summary>Deterministic, byte-stable CSV: sorted by left then right, ordinal.</summary>
    public static string WriteStrataCsv(IReadOnlyList<FrozenStratumAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        var sb = new StringBuilder();
        sb.Append("left_id,right_id,stratum\n");
        foreach (var a in assignments
            .OrderBy(a => a.LeftId, StringComparer.Ordinal)
            .ThenBy(a => a.RightId, StringComparer.Ordinal))
            sb.Append(CultureInfo.InvariantCulture, $"{a.LeftId},{a.RightId},{a.Stratum}\n");
        return sb.ToString();
    }

    public static IReadOnlyList<FrozenStratumAssignment> ReadStrataCsv(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);

        var rows = new List<FrozenStratumAssignment>();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].TrimEnd('\r').Split(',');
            if (parts.Length != 3)
                throw new ArgumentException($"Malformed strata row {i + 1}: '{lines[i]}'.", nameof(csv));
            rows.Add(new FrozenStratumAssignment(parts[0], parts[1], Enum.Parse<Stratum>(parts[2])));
        }
        return rows;
    }

    public static string Sha256Of(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// Buckets the current run's outcomes into the BASELINE's stratum assignments. Comparing by
    /// the current run's classification would let a canonicalizer change move a hard pair into an
    /// excluded stratum and report improvement during a regression (spec §8.1).
    /// </summary>
    public static IReadOnlyList<BaselineStratum> AggregateByFrozenStratum(
        IReadOnlyList<TruePairOutcome> current, IReadOnlyList<FrozenStratumAssignment> frozen)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(frozen);

        var byPair = current.ToDictionary(o => (o.LeftSourceRecordId, o.RightSourceRecordId));
        var totals = new Dictionary<Stratum, (long Pairs, long Tp)>();

        foreach (var f in frozen)
        {
            if (!byPair.TryGetValue((f.LeftId, f.RightId), out var outcome))
                throw new ArgumentException(
                    $"Frozen true pair ({f.LeftId}, {f.RightId}) is absent from the current run. " +
                    "The ground-truth pair set changed; regenerate the baseline deliberately.",
                    nameof(current));
            var entry = totals.GetValueOrDefault(f.Stratum);
            totals[f.Stratum] = (entry.Pairs + 1, entry.Tp + (outcome.SameCluster ? 1 : 0));
        }

        return Enum.GetValues<Stratum>()
            .Select(s => new BaselineStratum(s, totals.GetValueOrDefault(s).Pairs, totals.GetValueOrDefault(s).Tp))
            .ToList();
    }

    /// <summary>How many true pairs the current run classifies into a different stratum than the
    /// baseline did. Reported, never gated on: movement is information, the frozen cohorts are
    /// what actually holds the engine to account.</summary>
    public static long CountReclassified(
        IReadOnlyList<TruePairOutcome> current, IReadOnlyList<FrozenStratumAssignment> frozen)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(frozen);

        var byPair = current.ToDictionary(o => (o.LeftSourceRecordId, o.RightSourceRecordId), o => o.Stratum);
        return frozen.LongCount(f => byPair.TryGetValue((f.LeftId, f.RightId), out var now) && now != f.Stratum);
    }

    // ---- comparison ----

    public static BaselineComparison Compare(
        CorpusAuditBaseline baseline, CorpusAuditBaseline current,
        bool acceptProfileChange, long reclassifiedPairs = 0)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        static string? Refuse(string field, object? a, object? b)
            => Equals(a, b) ? null
                : $"Cannot compare: {field} changed ('{a}' -> '{b}'). " +
                  "Comparing runs over different inputs is meaningless.";

        var refusal =
            Refuse("recordsSha256", baseline.Inputs.RecordsSha256, current.Inputs.RecordsSha256) ??
            Refuse("groundTruthSha256", baseline.Inputs.GroundTruthSha256, current.Inputs.GroundTruthSha256) ??
            Refuse("strataSha256", baseline.Inputs.StrataSha256, current.Inputs.StrataSha256) ??
            Refuse("corpusSourceSha256", baseline.Inputs.CorpusSourceSha256, current.Inputs.CorpusSourceSha256);

        // The system-under-test configuration is separable from the evaluation inputs: a profile
        // or threshold change is exactly what the gate should eventually be able to evaluate, so
        // it refuses by default but can be acknowledged explicitly. See plan Task 10 / spec §10.
        if (refusal is null && !acceptProfileChange)
            refusal =
                Refuse("profileSha256", baseline.Inputs.ProfileSha256, current.Inputs.ProfileSha256) ??
                Refuse("maxBlockSize", baseline.Inputs.MaxBlockSize, current.Inputs.MaxBlockSize) ??
                Refuse("autoMatchThreshold", baseline.Inputs.AutoMatchThreshold, current.Inputs.AutoMatchThreshold) ??
                Refuse("reviewThreshold", baseline.Inputs.ReviewThreshold, current.Inputs.ReviewThreshold) ??
                Refuse("reviewFloorGate", baseline.Inputs.ReviewFloorGate, current.Inputs.ReviewFloorGate);

        if (refusal is null)
        {
            var a = baseline.Strata.Select(s => s.Id).OrderBy(x => x).ToList();
            var b = current.Strata.Select(s => s.Id).OrderBy(x => x).ToList();
            if (!a.SequenceEqual(b))
                refusal = $"Cannot compare: stratum set changed ({string.Join(",", a)} -> {string.Join(",", b)}).";
        }

        if (refusal is not null) return new BaselineComparison(true, refusal, [], reclassifiedPairs);

        var failures = new List<string>();
        var bc = baseline.Counts;
        var cc = current.Counts;

        // precision: cc.tp/cc.pp < bc.tp/bc.pp  <=>  cc.tp*bc.pp < bc.tp*cc.pp
        if (cc.PredictedPositive == 0 && bc.PredictedPositive > 0)
            failures.Add("cluster pairwise precision is undefined: the run merged nothing " +
                         $"(baseline predicted {bc.PredictedPositive} pairs).");
        else if (bc.PredictedPositive > 0 && cc.PredictedPositive > 0 &&
                 (BigInteger)cc.TruePositive * bc.PredictedPositive <
                 (BigInteger)bc.TruePositive * cc.PredictedPositive)
            failures.Add($"cluster pairwise precision decreased " +
                         $"({bc.TruePositive}/{bc.PredictedPositive} -> {cc.TruePositive}/{cc.PredictedPositive}).");

        if (cc.ReachableTruePairs < bc.ReachableTruePairs)
            failures.Add($"reachability decreased ({bc.ReachableTruePairs} -> {cc.ReachableTruePairs} true pairs).");

        var currentById = current.Strata.ToDictionary(s => s.Id);
        foreach (var b in baseline.Strata)
        {
            var c = currentById[b.Id];
            if (b.TruePairs == 0 || c.TruePairs == 0) continue;

            // Fail iff (b.tp/b.n - c.tp/c.n) > 5/1000, exactly:
            //   (b.tp*c.n - c.tp*b.n) * 1000 > 5 * b.n * c.n
            var lhs = ((BigInteger)b.PostClusterTruePositive * c.TruePairs
                       - (BigInteger)c.PostClusterTruePositive * b.TruePairs) * ToleranceDenominator;
            var rhs = (BigInteger)ToleranceNumerator * b.TruePairs * c.TruePairs;
            if (lhs > rhs)
                failures.Add($"{b.Id} post-cluster recall fell beyond the 0.5pp tolerance " +
                             $"({b.PostClusterTruePositive}/{b.TruePairs} -> " +
                             $"{c.PostClusterTruePositive}/{c.TruePairs}).");
        }

        return new BaselineComparison(false, null, failures, reclassifiedPairs);
    }

    /// <summary>
    /// Writes baseline.json and baseline-strata.csv atomically, and REFUSES to overwrite an
    /// existing baseline unless replace is true. The corpus directory is not version-controlled,
    /// so "written once" has to be enforced by the tool rather than by convention — otherwise
    /// resetting the gate to current behaviour is a single command.
    /// </summary>
    public static void WriteAtomic(
        string directory, CorpusAuditBaseline baseline,
        IReadOnlyList<FrozenStratumAssignment> strata, bool replace)
    {
        var jsonPath = Path.Combine(directory, BaselineFileName);
        var strataPath = Path.Combine(directory, StrataFileName);

        if (!replace && (File.Exists(jsonPath) || File.Exists(strataPath)))
            throw new InvalidOperationException(
                $"A baseline already exists in '{directory}'. Overwriting it silently resets the gate " +
                "to current behaviour. Re-run with --replace-baseline if that is genuinely intended.");

        Directory.CreateDirectory(directory);
        WriteFileAtomic(strataPath, WriteStrataCsv(strata));
        WriteFileAtomic(jsonPath, ToJson(baseline));
    }

    private static void WriteFileAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
