using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline;

/// <summary>
/// One field-width variant to ablate: a label and the set of field names that should be
/// <see cref="FieldRole.Matchable"/> at this width. Every name must already carry
/// <see cref="FieldRole.Matchable"/> on the base profile — this is a narrowing tool, not a
/// profile editor, and widening would let a width silently invent scoring behavior the base
/// profile never had.
/// </summary>
public sealed record FieldWidth(string Name, IReadOnlyList<string> MatchableFieldNames);

/// <summary>
/// One width's outcome. <see cref="ThresholdAt100Precision"/>/<see cref="RecallAt100Precision"/>
/// are populated only when some cut achieves EXACT 100% direct-edge precision; when none does,
/// both are null and <see cref="PerfectPrecisionReachable"/> is false — the caller must report
/// that explicitly rather than substituting the best precision actually observed.
/// <see cref="MaxPrecisionObserved"/>/<see cref="RecallAtMaxPrecisionObserved"/> carry that
/// context anyway, but as a clearly separate, clearly-not-100% diagnostic.
/// </summary>
public sealed record FieldShapeAblationRow(
    string WidthName,
    int MatchableFieldCount,
    long TruePairs,
    double Reachability,
    bool PerfectPrecisionReachable,
    double? ThresholdAt100Precision,
    double? RecallAt100Precision,
    double? MaxPrecisionObserved,
    double? RecallAtMaxPrecisionObserved);

public sealed record FieldShapeAblationResult(IReadOnlyList<FieldShapeAblationRow> Rows);

/// <summary>
/// Field-shape ablation: runs the same labelled corpus through the same profile at several
/// Matchable-field widths, holding blocking (and therefore the candidate set) fixed, and reports
/// per width the best-recall point at 100% direct-edge precision. This is the instrument for the
/// weighted-average-denominator defect: the current scorer divides by the sum of weights of
/// whichever fields happen to be populated on both sides, so a single configured threshold
/// demands strong agreement on a wide record and weak agreement on a narrow one. If the scorer
/// has that defect, the usable threshold (the 100%-precision cut) moves across widths; a scorer
/// without it should hold that cut roughly still.
/// <para>
/// Delegates every per-width scoring run to <see cref="ScoringAuditService"/> — direct-edge P/R
/// and the threshold sweep already live there, and re-deriving them here would let this tool and
/// `match scoring audit` silently disagree about the same computation. This class contributes
/// only what ablation needs on top: building each width's profile variant and extracting the
/// one number per width ("the threshold at which precision is 100%, and the recall there") that
/// makes widths comparable.
/// </para>
/// </summary>
public sealed class FieldShapeAblationService
{
    private readonly ScoringAuditService _scoringAudit;

    public FieldShapeAblationService(IStrategyRegistry registry)
    {
        _scoringAudit = new ScoringAuditService(registry);
    }

    public FieldShapeAblationResult Audit(
        IReadOnlyList<EntityRecord> records,
        MatchingProfile baseProfile,
        IReadOnlyDictionary<string, string> groundTruth,
        IReadOnlyList<FieldWidth> widths,
        int? maxBlockSize = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(baseProfile);
        ArgumentNullException.ThrowIfNull(groundTruth);
        ArgumentNullException.ThrowIfNull(widths);

        if (widths.Count == 0)
            throw new ArgumentException("At least one width is required.");

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var width in widths)
        {
            if (string.IsNullOrWhiteSpace(width.Name))
                throw new ArgumentException("Every width needs a non-empty name.");
            if (!seenNames.Add(width.Name))
                throw new ArgumentException($"Duplicate width name: '{width.Name}'.");
            if (width.MatchableFieldNames.Count == 0)
                throw new ArgumentException($"Width '{width.Name}' names no fields.");
        }

        var rows = widths.Select(w => AuditWidth(records, baseProfile, groundTruth, w, maxBlockSize)).ToList();
        return new FieldShapeAblationResult(rows);
    }

    /// <summary>
    /// Narrows the base profile to one width: a field ends up Matchable iff its name is in
    /// <paramref name="width"/>, and every other role — Blocking above all — is copied verbatim
    /// from the base profile. A width naming a field the base profile does not already mark
    /// Matchable is refused (see class doc): ablation only removes evidence the base profile
    /// offered, it never adds evidence the base profile did not configure.
    /// </summary>
    internal static MatchingProfile BuildWidthProfile(MatchingProfile baseProfile, FieldWidth width)
    {
        var wanted = new HashSet<string>(width.MatchableFieldNames, StringComparer.OrdinalIgnoreCase);
        var baseNames = new HashSet<string>(
            baseProfile.Fields.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        var unknown = wanted.Where(n => !baseNames.Contains(n)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException(
                $"Width '{width.Name}' names field(s) not present on the base profile: " +
                $"{string.Join(", ", unknown)}.");

        var notMatchableInBase = wanted
            .Where(n => !baseProfile.Fields.Single(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase))
                .Roles.HasFlag(FieldRole.Matchable))
            .ToList();
        if (notMatchableInBase.Count > 0)
            throw new ArgumentException(
                $"Width '{width.Name}' names field(s) the base profile does not mark Matchable: " +
                $"{string.Join(", ", notMatchableInBase)}. Ablation only narrows the base profile's " +
                "Matchable set; it never widens it.");

        var fields = baseProfile.Fields
            .Select(f => f with
            {
                Roles = wanted.Contains(f.Name) ? f.Roles | FieldRole.Matchable : f.Roles & ~FieldRole.Matchable
            })
            .ToList();
        return baseProfile with { Fields = fields };
    }

    private FieldShapeAblationRow AuditWidth(
        IReadOnlyList<EntityRecord> records, MatchingProfile baseProfile,
        IReadOnlyDictionary<string, string> groundTruth, FieldWidth width, int? maxBlockSize)
    {
        var profile = BuildWidthProfile(baseProfile, width);
        var matchableCount = profile.Fields.Count(f => f.Roles.HasFlag(FieldRole.Matchable));

        var result = _scoringAudit.Audit(records, profile, groundTruth, maxBlockSize);
        var misses = result.Misses
            ?? throw new InvalidOperationException(
                "Ground truth was supplied but the scoring audit produced no miss decomposition.");

        var reachability = misses.TruePairs == 0
            ? 0.0
            : (double)(misses.TruePairs - misses.Unreachable) / misses.TruePairs;

        // Exact 100% precision only: PredictedPositives > 0 excludes the vacuous "nothing scored
        // this high" cuts, where Precision is null (0/0), not 1.0.
        var perfect = result.Sweep
            .Where(s => s.PredictedPositives > 0 && s.Precision is { } p && p >= 1.0 - 1e-9)
            .ToList();
        var best = perfect
            .OrderByDescending(s => s.Recall ?? 0.0)
            .ThenBy(s => s.Cut)
            .FirstOrDefault();

        var bestPrecision = result.Sweep
            .Where(s => s.PredictedPositives > 0)
            .OrderByDescending(s => s.Precision ?? 0.0)
            .ThenByDescending(s => s.Recall ?? 0.0)
            .FirstOrDefault();

        return new FieldShapeAblationRow(
            width.Name, matchableCount, misses.TruePairs, reachability,
            PerfectPrecisionReachable: best is not null,
            ThresholdAt100Precision: best?.Cut,
            RecallAt100Precision: best?.Recall,
            MaxPrecisionObserved: best is null ? bestPrecision?.Precision : null,
            RecallAtMaxPrecisionObserved: best is null ? bestPrecision?.Recall : null);
    }
}
