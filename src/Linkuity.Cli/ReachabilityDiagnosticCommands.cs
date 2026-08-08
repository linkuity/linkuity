using System.Text.Json;
using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// `match blocking reachability`: classifies every unreachable true (ground-truth) pair as cause
/// A (every shared key suppressed by maxBlockSize), B1 (capability gap), B2 (configuration gap)
/// or B3 (genuinely disjoint), flags normalization loss, measures per-column co-occurrence
/// against a non-pair control, and reports the corpus-scale block-size histogram. Thin CLI
/// wrapper over <see cref="ReachabilityDiagnosticService"/>, which does the actual work and is
/// the one built to run at full corpus scale (3.9M records) rather than the sample-scale
/// <see cref="BlockingAuditService"/>.
/// <para>
/// Dispatched from <see cref="BlockingAuditCommands"/> alongside `audit` and `explain` -- profile
/// resolution and record loading are shared there; this class owns everything specific to the
/// reachability diagnostic: the (required) ground truth, the diagnostic itself, and its two
/// output artifacts.
/// </para>
/// <para>
/// The JSON artifact carries no wall-clock, machine name, or other run-varying content -- Task 7
/// diffs it across changes at 3.9M records, and a byte-for-byte identical run over identical
/// inputs is the property that makes that diff meaningful.
/// </para>
/// </summary>
public static class ReachabilityDiagnosticCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> RunAsync(
        IReadOnlyList<EntityRecord> records,
        MatchingProfile profile,
        IReadOnlyDictionary<string, string> options,
        CancellationToken ct)
    {
        if (!options.TryGetValue("ground-truth", out var truthPath) || string.IsNullOrWhiteSpace(truthPath))
        {
            await Console.Error.WriteLineAsync("A --ground-truth is required.");
            return 2;
        }
        if (!File.Exists(truthPath))
        {
            await Console.Error.WriteLineAsync($"Ground-truth CSV not found: {truthPath}");
            return 2;
        }

        Dictionary<string, string> groundTruth;
        try { groundTruth = AuditCliCommon.ReadGroundTruthStrict(truthPath); }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        var (maxBlockSize, maxBlockSizeError) = AuditCliCommon.ResolveMaxBlockSize(options, profile);
        if (maxBlockSizeError is not null)
        {
            await Console.Error.WriteLineAsync(maxBlockSizeError);
            return 2;
        }

        ReachabilityDiagnosticResult result;
        try
        {
            var service = new ReachabilityDiagnosticService(MatchingDefaults.CreateRegistry());
            result = service.Diagnose(records, profile, groundTruth, maxBlockSize, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        if (options.TryGetValue("json", out var jsonPath) && !string.IsNullOrWhiteSpace(jsonPath))
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(result, JsonOptions), ct);

        var report = ReachabilityDiagnosticTextFormatter.Format(result);
        if (options.TryGetValue("report", out var reportPath) && !string.IsNullOrWhiteSpace(reportPath))
            await File.WriteAllTextAsync(reportPath, report, ct);
        else
            Console.Write(report);

        return 0;
    }
}
