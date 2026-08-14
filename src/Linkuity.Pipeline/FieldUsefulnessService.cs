using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline;

/// <summary>
/// Answers "which of my fields are actually worth matching on, and what can this data never
/// resolve?" — the two questions a profile author would otherwise have to guess at.
///
/// Guessing is the loop this exists to end: pick fields, assign weights, run, look, adjust,
/// repeat. Whether a column is good evidence is a property of the DATA, not of the domain, and it
/// is measurable. Address may be decisive in one corpus and near-worthless in another; nothing
/// here decides that in advance, and nothing about a field's meaning is hard-coded.
///
/// The agreement rates come from <see cref="FieldEvidenceCalibrationService"/> rather than being
/// measured again here. Two independently written measurements of the same quantity drift, and the
/// first anyone would notice is a report confidently contradicting the numbers the engine actually
/// scores with.
/// </summary>
public sealed class FieldUsefulnessService
{
    /// <summary>ASCII unit separator. Cannot occur in a field value, so no combination of values
    /// can forge it and make two genuinely different records collide in the signature below.</summary>
    private const char Separator = '';

    private readonly IStrategyRegistry _registry;
    private readonly FieldEvidenceCalibrationService _calibration;

    public FieldUsefulnessService(IStrategyRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _calibration = new FieldEvidenceCalibrationService(registry);
    }

    public FieldUsefulnessResult Analyze(
        IReadOnlyList<EntityRecord> records,
        MatchingProfile profile,
        IReadOnlyDictionary<string, string> groundTruth,
        int? maxBlockSize = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(groundTruth);

        // fitFraction is deliberately near-total rather than the calibration default of 0.5. This
        // report is not producing parameters to be evaluated on held-out data; it is describing the
        // corpus in front of the reader, and halving the observations would only widen the error
        // bars on a description.
        var calibration = _calibration.Calibrate(records, profile, groundTruth, maxBlockSize, 0.99, ct);
        var calibrationByField = calibration.Fields.ToDictionary(f => f.FieldName, StringComparer.Ordinal);

        var normalization = ProfileNormalization.Resolve(_registry, profile);
        var normalized = records.Select(r => normalization.Normalize(r, profile)).ToArray();

        var matchable = profile.Fields.Where(f => f.Roles.HasFlag(FieldRole.Matchable)).ToList();

        var rows = new List<FieldUsefulnessRow>(matchable.Count);
        foreach (var field in matchable)
        {
            ct.ThrowIfCancellationRequested();

            // IsAbsent, not IsNullOrWhiteSpace: a column full of "UNKNOWN" is not a filled column,
            // and reporting it as 100% filled would recommend matching on nothing.
            var filled = normalized.LongCount(r =>
                r.Fields.TryGetValue(field.Name, out var v) && !field.IsAbsent(v));

            calibrationByField.TryGetValue(field.Name, out var row);

            rows.Add(new FieldUsefulnessRow(
                field.Name,
                records.Count == 0 ? 0 : (double)filled / records.Count,
                filled,
                row?.SmoothedM,
                row?.SmoothedU,
                row?.SameEntityComparisons ?? 0,
                row?.DifferentEntityComparisons ?? 0));
        }

        return new FieldUsefulnessResult(
            records.Count,
            records.Count(r => groundTruth.ContainsKey(r.SourceRecordId)),
            calibration.LabeledSameEntityPairs,
            rows,
            FindIndistinguishable(normalized, matchable, groundTruth, ct));
    }

    /// <summary>
    /// Groups records by the value of EVERY matchable field as the engine sees it. A group holding
    /// two or more distinct ground-truth entities is unresolvable: the matcher is looking at
    /// identical inputs and being asked for different answers.
    /// <para>
    /// Deliberately whole-corpus and not restricted to blocked candidates. Blocking is a
    /// performance decision; whether two records are DISTINGUISHABLE is not, and a reader asking
    /// "what can this data never resolve" is asking about the data, not about the index.
    /// </para>
    /// </summary>
    private static IndistinguishableGroups FindIndistinguishable(
        IReadOnlyList<EntityRecord> normalized,
        IReadOnlyList<ProfileField> matchable,
        IReadOnlyDictionary<string, string> groundTruth,
        CancellationToken ct)
    {
        var bySignature = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var i = 0; i < normalized.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Only labeled records can reveal a conflict: an unlabeled one cannot be shown to be a
            // different entity from anything, so including it would inflate group sizes with
            // records that are merely unverified.
            if (!groundTruth.ContainsKey(normalized[i].SourceRecordId))
                continue;

            // Joined on the ASCII unit separator, which cannot occur in a field value, so no
            // combination of values can forge the separator and make two different records
            // collide by accident.
            var signature = string.Join(Separator, matchable.Select(f =>
                normalized[i].Fields.TryGetValue(f.Name, out var v) && !f.IsAbsent(v) ? v : ""));

            (bySignature.TryGetValue(signature, out var list) ? list : bySignature[signature] = []).Add(i);
        }

        var groups = 0;
        long recordsInvolved = 0;
        var largestRecords = 0;
        var largestEntities = 0;
        List<int>? largest = null;

        foreach (var members in bySignature.Values)
        {
            if (members.Count < 2)
                continue;

            var entities = members
                .Select(i => groundTruth[normalized[i].SourceRecordId])
                .Distinct(StringComparer.Ordinal)
                .Count();

            // One entity's records sharing a signature is the system working, not a limitation.
            if (entities < 2)
                continue;

            groups++;
            recordsInvolved += members.Count;
            if (members.Count > largestRecords)
            {
                largestRecords = members.Count;
                largestEntities = entities;
                largest = members;
            }
        }

        var unfilled = largest is null
            ? []
            : matchable
                .Where(f => largest.All(i =>
                    !normalized[i].Fields.TryGetValue(f.Name, out var v) || f.IsAbsent(v)))
                .Select(f => f.Name)
                .ToList();

        return new IndistinguishableGroups(groups, recordsInvolved, largestRecords, largestEntities, unfilled);
    }
}
