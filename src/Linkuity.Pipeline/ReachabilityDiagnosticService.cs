using Linkuity.Core.Models;
using Linkuity.Matching.Canonicalization;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline;

/// <summary>
/// Classifies WHY the engine never compares a true (ground-truth) pair: cause A (every shared
/// key was suppressed by maxBlockSize), cause B1 (a declared-Blocking field shares a value but no
/// strategy can key it -- a capability gap), cause B2 (an undeclared corpus column shares a value
/// -- a configuration gap), or cause B3 (genuinely disjoint). Normalization loss is reported as an
/// orthogonal flag, since it can accompany either an A or a B classification.
///
/// Built directly on <see cref="BlockingKeyIndex"/> -- the same interned, scale-safe primitives
/// <see cref="BlockingAuditService"/> uses -- so this is the instrument that runs at full corpus
/// scale (3.9M records), not a sample-scale companion to it.
/// </summary>
public sealed class ReachabilityDiagnosticService
{
    private const int CauseSampleCap = 50;
    private const int LargestBlockCount = 10;
    private const string ProbeValue = "PROBE-VALUE-1";

    private static readonly OrganizationNameCanonicalizer OrgCanonicalizer = new();

    private readonly IStrategyRegistry _registry;

    public ReachabilityDiagnosticService(IStrategyRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public ReachabilityDiagnosticResult Diagnose(
        IReadOnlyList<EntityRecord> records,
        MatchingProfile profile,
        IReadOnlyDictionary<string, string> groundTruth,
        int? maxBlockSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(groundTruth);

        var index = BlockingKeyIndex.Build(records, profile, _registry, ct);
        var suppressed = BlockingKeyIndex.SuppressedKeys(index, maxBlockSize);

        var bySource = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < records.Count; i++) bySource[records[i].SourceRecordId] = i;

        var declaredFieldNames = profile.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var undeclaredColumns = records
            .SelectMany(r => r.Fields.Keys)
            .Where(c => !declaredFieldNames.Contains(c))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        var unusableBlockingFields = ComputeUnusableBlockingFields(profile, _registry);

        // Every column present anywhere in the corpus, declared or not. Field co-occurrence is
        // a taxonomy-agnostic measurement -- postal code, address line, city, country,
        // jurisdiction, legal form are all just columns here -- so it runs generically over
        // whatever columns the corpus actually carries rather than a hardcoded list.
        var allColumns = AllColumnsOf(records);

        // Ground-truth groups, restricted to records actually present in this record set.
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (sourceId, canonical) in groundTruth)
        {
            if (!bySource.ContainsKey(sourceId)) continue;
            (groups.TryGetValue(canonical, out var list) ? list : groups[canonical] = []).Add(sourceId);
        }

        long truePairs = 0, reachablePairs = 0;
        long aCount = 0, b1Count = 0, b2Count = 0, b3Count = 0;
        long normImplicatedCount = 0, legalSuffixOnlyCount = 0;

        var b1ByColumn = new Dictionary<string, long>(StringComparer.Ordinal);
        var b2ByColumn = new Dictionary<string, long>(StringComparer.Ordinal);
        var aDetailCounts = new Dictionary<(string Strategy, int BlockSize), long>();
        var owningStrategyCache = new Dictionary<int, string>();

        var unreachableAcc = new Dictionary<string, (long Shared, long Sample)>(StringComparer.Ordinal);

        var aSampler = new CappedPairSampler(CauseSampleCap);
        var b1Sampler = new CappedPairSampler(CauseSampleCap);
        var b2Sampler = new CappedPairSampler(CauseSampleCap);
        var b3Sampler = new CappedPairSampler(CauseSampleCap);
        var normSampler = new CappedPairSampler(CauseSampleCap);

        // Iterate true pairs GROUP BY GROUP: never materialise a per-pair collection over the
        // whole ground truth. The only retained structures are the counters above, the
        // ByColumn/CauseADetail dictionaries (bounded by column count and by strategy count x
        // distinct block sizes respectively), the owning-strategy cache (bounded by distinct key
        // count), and the five capped samplers.
        foreach (var (canonical, membersRaw) in groups)
        {
            if (membersRaw.Count < 2) continue;
            var members = membersRaw.OrderBy(x => x, StringComparer.Ordinal).ToList();

            for (var i = 0; i < members.Count; i++)
            {
                for (var j = i + 1; j < members.Count; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    truePairs++;

                    var li = bySource[members[i]];
                    var ri = bySource[members[j]];
                    var left = records[li];
                    var right = records[ri];
                    var leftKeys = index.RecordKeys[li];
                    var rightKeys = index.RecordKeys[ri];

                    if (BlockingKeyIndex.SharesAnyActiveKey(leftKeys, rightKeys, suppressed))
                    {
                        reachablePairs++;
                    }
                    else
                    {
                        // Field co-occurrence is orthogonal to the A/B1/B2/B3 classification below
                        // and runs for EVERY unreachable pair regardless of cause -- it answers "do
                        // they share anything else", not "why can't the engine compare them".
                        AccumulateColumnStats(left, right, allColumns, unreachableAcc);

                        var sharedIgnoringSuppression = BlockingKeyIndex.SharedKeysIgnoringSuppression(leftKeys, rightKeys);
                        if (sharedIgnoringSuppression.Count > 0)
                        {
                            // Cause A: every key both records carry was thrown away by the cap.
                            aCount++;
                            aSampler.Offer(members[i], members[j], canonical);

                            // Dedupe to (strategy, blockSize) buckets THIS PAIR TOUCHES before
                            // incrementing: a multi-token org name can share several keys from the
                            // same strategy at the same block size (e.g. "ACME TRADING LIMITED"
                            // clones share 3 acronym keys at block size 4), and counting each
                            // shared key separately would inflate one pair into several -- exactly
                            // the number a per-feature threshold must not be chosen from.
                            var bucketsTouched = new HashSet<(string Strategy, int BlockSize)>();
                            foreach (var keyId in sharedIgnoringSuppression)
                            {
                                var strategy = OwningStrategyOf(keyId, index, records, profile, _registry, owningStrategyCache);
                                var blockSize = index.KeyCount[keyId];
                                bucketsTouched.Add((strategy, blockSize));
                            }
                            foreach (var detailKey in bucketsTouched)
                                aDetailCounts[detailKey] = aDetailCounts.GetValueOrDefault(detailKey) + 1;
                        }
                        else
                        {
                            // No shared key at all (active or suppressed). Classify B1 BEFORE B2:
                            // a field the profile declares Blocking but that no strategy can key
                            // is a capability gap regardless of what else is undeclared on the
                            // same pair. Checking B2 first would attribute a capability gap to
                            // configuration and understate the real problem -- "add a profile
                            // line" instead of "build a capability".
                            var b1Matches = FindUnusableBlockingFieldMatches(left, right, profile, unusableBlockingFields);
                            if (b1Matches.Count > 0)
                            {
                                b1Count++;
                                foreach (var column in b1Matches)
                                    b1ByColumn[column] = b1ByColumn.GetValueOrDefault(column) + 1;
                                b1Sampler.Offer(members[i], members[j], canonical);
                            }
                            else
                            {
                                var b2Matches = FindUndeclaredColumnMatches(left, right, undeclaredColumns);
                                if (b2Matches.Count > 0)
                                {
                                    b2Count++;
                                    foreach (var column in b2Matches)
                                        b2ByColumn[column] = b2ByColumn.GetValueOrDefault(column) + 1;
                                    b2Sampler.Offer(members[i], members[j], canonical);
                                }
                                else
                                {
                                    b3Count++;
                                    b3Sampler.Offer(members[i], members[j], canonical);
                                }
                            }
                        }

                        // Normalization loss is orthogonal to the A/B classification above: it
                        // flags pairs whose organization-name fields share a raw token (e.g. a
                        // legal suffix) that canonicalization removed before any blocking
                        // strategy saw it. It can apply to a B pair (nothing else shared either)
                        // or, in principle, alongside cause A.
                        if (IsNormalizationImplicated(left, right, profile, out var legalSuffixOnly))
                        {
                            normImplicatedCount++;
                            if (legalSuffixOnly) legalSuffixOnlyCount++;
                            normSampler.Offer(members[i], members[j], canonical);
                        }
                    }
                }
            }
        }

        var unreachablePairs = aCount + b1Count + b2Count + b3Count;
        AssertReconciles(truePairs, reachablePairs, unreachablePairs, aCount, b1Count, b2Count, b3Count);

        var causeADetail = aDetailCounts
            .OrderBy(kv => kv.Key.Strategy, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.BlockSize)
            .Select(kv => new SuppressedKeyDetail(kv.Key.Strategy, kv.Key.BlockSize, kv.Value))
            .ToList();

        var causeA = new CauseTally(aCount, new Dictionary<string, long>(), aSampler.ToSortedList());
        var causeB1 = new CauseTally(b1Count, SortedColumns(b1ByColumn), b1Sampler.ToSortedList());
        var causeB2 = new CauseTally(b2Count, SortedColumns(b2ByColumn), b2Sampler.ToSortedList());
        var causeB3 = new CauseTally(b3Count, new Dictionary<string, long>(), b3Sampler.ToSortedList());
        var normalization = new NormalizationTally(normImplicatedCount, legalSuffixOnlyCount, normSampler.ToSortedList());

        var blocks = BuildBlockHistogram(index, records, profile, _registry, owningStrategyCache);

        // The non-pair control: without it, a high co-occurrence rate on a low-cardinality
        // column (country, jurisdiction) is unfalsifiable evidence -- see the class doc. Compute
        // it BEFORE the unreachable-side figures so each column's Lift can reference the
        // control's rate for that same column.
        var (controlSampledPairCount, truePairsAccidentallyIncluded, selfPairsSkipped, controlAcc) =
            BuildControlAccumulation(records, groundTruth, allColumns, ct);

        var controlByColumn = allColumns.ToDictionary(
            column => column,
            column =>
            {
                var (shared, sampleSize) = controlAcc.TryGetValue(column, out var v) ? v : (0L, 0L);
                return BuildCoOccurrence(column, shared, sampleSize, controlRateForLift: null);
            },
            StringComparer.Ordinal);

        var unreachableByColumn = allColumns.ToDictionary(
            column => column,
            column =>
            {
                var (shared, sampleSize) = unreachableAcc.TryGetValue(column, out var v) ? v : (0L, 0L);
                var controlRate = controlByColumn[column].Rate;
                return BuildCoOccurrence(column, shared, sampleSize, controlRateForLift: controlRate);
            },
            StringComparer.Ordinal);

        return new ReachabilityDiagnosticResult(
            truePairs,
            reachablePairs,
            unreachablePairs,
            causeA,
            causeB1,
            causeB2,
            causeB3,
            normalization,
            causeADetail,
            Unreachable: new FieldCoOccurrenceSet(unreachablePairs, unreachableByColumn),
            Control: new ControlSet(controlSampledPairCount, truePairsAccidentallyIncluded, selfPairsSkipped, controlByColumn),
            Blocks: blocks);
    }

    /// <summary>Fails the run if the cause tallies do not account for every pair. The corpus
    /// build's final review found its equivalent arithmetic living in review prose rather than
    /// in code; a future silent skip must trip an assertion, not depend on someone noticing.</summary>
    internal static void AssertReconciles(
        long truePairs, long reachable, long unreachable, long a, long b1, long b2, long b3)
    {
        if (reachable + unreachable != truePairs)
            throw new InvalidOperationException(
                $"reachable {reachable:N0} + unreachable {unreachable:N0} != truePairs {truePairs:N0}");
        if (a + b1 + b2 + b3 != unreachable)
            throw new InvalidOperationException(
                $"A {a:N0} + B1 {b1:N0} + B2 {b2:N0} + B3 {b3:N0} != unreachable {unreachable:N0}");
    }

    private static IReadOnlyDictionary<string, long> SortedColumns(Dictionary<string, long> byColumn)
        => byColumn.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>A Blocking-role field is "unusable" when NONE of the profile's configured
    /// blocking strategies would emit any key from it -- checked once, up front, against a
    /// synthetic probe record carrying only that field, so the result depends purely on
    /// capability (semantic type x configured strategy set), never on the pair's actual values.
    /// </summary>
    private static HashSet<string> ComputeUnusableBlockingFields(MatchingProfile profile, IStrategyRegistry registry)
    {
        var unusable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in profile.Fields)
        {
            if (!field.Roles.HasFlag(FieldRole.Blocking)) continue;
            var probe = ProbeRecord(field.Name);
            var keyed = profile.BlockingStrategies.Any(name =>
                registry.Blocking[name].GenerateKeys(probe, profile).Count > 0);
            if (!keyed) unusable.Add(field.Name);
        }
        return unusable;
    }

    private static EntityRecord ProbeRecord(string fieldName) => new()
    {
        Id = Guid.Empty, ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
        SourceRecordId = "probe",
        Fields = new Dictionary<string, string> { [fieldName] = ProbeValue },
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    /// <summary>Every profile-declared Blocking field that is capability-unusable AND has an
    /// equal, non-empty value on both records. Returns every matching column (not just the
    /// first) so CauseB1.ByColumn reflects all of them; classification only needs Count > 0.
    /// </summary>
    private static List<string> FindUnusableBlockingFieldMatches(
        EntityRecord left, EntityRecord right, MatchingProfile profile, IReadOnlySet<string> unusableBlockingFields)
    {
        var matches = new List<string>();
        foreach (var field in profile.Fields)
        {
            if (!field.Roles.HasFlag(FieldRole.Blocking)) continue;
            if (!unusableBlockingFields.Contains(field.Name)) continue;
            if (ValuesEqual(left.Fields.GetValueOrDefault(field.Name), right.Fields.GetValueOrDefault(field.Name)))
                matches.Add(field.Name);
        }
        return matches;
    }

    /// <summary>Every corpus column the profile does NOT declare that has an equal, non-empty
    /// value on both records.</summary>
    private static List<string> FindUndeclaredColumnMatches(
        EntityRecord left, EntityRecord right, IReadOnlyList<string> undeclaredColumns)
    {
        var matches = new List<string>();
        foreach (var column in undeclaredColumns)
            if (ValuesEqual(left.Fields.GetValueOrDefault(column), right.Fields.GetValueOrDefault(column)))
                matches.Add(column);
        return matches;
    }

    private static bool ValuesEqual(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Every column present anywhere in the corpus -- declared or undeclared alike.
    /// Field co-occurrence is a taxonomy-agnostic measurement, not a classification: postal
    /// code, address line, city, country, jurisdiction, legal form are all just columns here, so
    /// this runs generically over whatever columns the corpus actually carries rather than a
    /// hardcoded list.</summary>
    private static List<string> AllColumnsOf(IReadOnlyList<EntityRecord> records)
        => records.SelectMany(r => r.Fields.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

    /// <summary>Accumulates, per column, how many pairs had a non-empty value on BOTH sides
    /// (Sample) and how many of those were equal (Shared). Never retains a per-pair record --
    /// only these two running counters per column, bounded by column count, not corpus size.
    /// </summary>
    private static void AccumulateColumnStats(
        EntityRecord left, EntityRecord right, IReadOnlyList<string> columns,
        Dictionary<string, (long Shared, long Sample)> acc)
    {
        foreach (var column in columns)
        {
            var leftValue = left.Fields.GetValueOrDefault(column);
            var rightValue = right.Fields.GetValueOrDefault(column);
            if (string.IsNullOrWhiteSpace(leftValue) || string.IsNullOrWhiteSpace(rightValue)) continue;

            var (shared, sample) = acc.TryGetValue(column, out var v) ? v : (0L, 0L);
            sample++;
            if (string.Equals(leftValue.Trim(), rightValue.Trim(), StringComparison.OrdinalIgnoreCase)) shared++;
            acc[column] = (shared, sample);
        }
    }

    /// <summary>The non-pair control.
    ///
    /// An earlier version of this method walked record INDICES at a fixed stride
    /// (floor(sqrt(recordCount))) and paired each record with the one `stride` ahead. That was
    /// wrong for this corpus specifically, not just in theory: `records.csv` is sorted by
    /// (LEI, ordinal), an LEI's first four characters are the issuing LOU's prefix, so the sort
    /// groups every LOU into one contiguous run -- and LOUs are largely national or regional
    /// registries, so "same LOU" implies "same country/jurisdiction" for a large share of
    /// records. Many LOU runs are far longer than a sqrt(n) stride (~1,986 at 3.9M records), so
    /// most stride-selected pairs landed INSIDE the same LOU run, and the control silently
    /// inherited the corpus's own sort-order correlation with the very columns (country,
    /// jurisdiction, and anything else correlated with the issuer, e.g. legal form) it exists to
    /// give an unbiased base rate for. This is not a new risk to this project: the corpus build
    /// hit the identical failure mode measuring `Region` -- a head-of-file sample read 76.0%
    /// populated against 65.9% populated corpus-wide, a 10.1-point bias -- and every sampling
    /// decision in that work was moved off file position onto blake2b(LEI) hashing as a result.
    /// A file-position stride for this control reintroduced exactly the method that measurement
    /// rejected. At n ~ 3.9M the Wilson interval is narrow enough that a biased rate reads as
    /// PRECISE, which is worse than an obviously wide, honest one.
    ///
    /// The fix: derive each record's control partner from a hash of its OWN identity
    /// (StableHash(SourceRecordId) % recordCount), not its file position. Hashing the id
    /// destroys any relationship to sort order -- an LEI's issuing-LOU prefix has no bearing on
    /// where its hash lands mod n -- which is exactly why the corpus build adopted blake2b(LEI)
    /// for the same reason. Reuses BlockingAuditService.MissedPairSampler.StableHash: an FNV-1a
    /// hash with no per-process seed (unlike string.GetHashCode()), already `internal`, already
    /// pinned by a golden-value test, and used elsewhere in this very service (CappedPairSampler)
    /// -- a second, potentially-divergent hash implementation is exactly the kind of thing this
    /// codebase has already paid for once.
    ///
    /// Sample size stays comparable to the old stride walk: every one of the n records
    /// contributes at most one partner lookup, so this is still O(n) with no dedup structure and
    /// no per-pair retention -- only the running column counters and three long counters survive
    /// the walk. Three outcomes per index, all counted, none silent:
    ///   - the hashed partner IS the record itself (recordCount is small or unlucky) -- skipped
    ///     and counted into SelfPairsSkipped, not just `continue`d;
    ///   - the hashed partner shares a ground-truth canonical id -- a true pair, not a control
    ///     sample; skipped and counted into TruePairsAccidentallyIncluded (unchanged from before);
    ///   - otherwise, a legitimate non-pair; its columns are folded into the aggregate.
    /// SampledPairCount + TruePairsAccidentallyIncluded + SelfPairsSkipped == records.Count always.
    ///
    /// On deduplication: the SAME unordered pair can arise from two different indices (record A's
    /// hash points to record B, and independently record B's hash points to record A). This
    /// implementation does NOT deduplicate that case -- each index is walked once, independently,
    /// and its outcome (sampled, excluded, or self-paired) is counted on its own terms. Dedup
    /// would require an extra O(n) "already emitted" structure to buy a rare, non-biasing
    /// correction (a reciprocal pair contributes its own true value to the aggregate twice, which
    /// does not shift the RATE for that pair, only its weight); accepting it is the simpler and
    /// (per review) equally defensible choice, so it is what this method does.</summary>
    private static (long SampledPairCount, long TruePairsAccidentallyIncluded, long SelfPairsSkipped, Dictionary<string, (long Shared, long Sample)> Accumulator)
        BuildControlAccumulation(
            IReadOnlyList<EntityRecord> records,
            IReadOnlyDictionary<string, string> groundTruth,
            IReadOnlyList<string> columns,
            CancellationToken ct)
    {
        var acc = new Dictionary<string, (long Shared, long Sample)>(StringComparer.Ordinal);
        long sampled = 0, accidentallyIncluded = 0, selfPairsSkipped = 0;
        var n = records.Count;
        if (n == 0) return (0, 0, 0, acc);

        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var left = records[i];
            var partner = (int)(BlockingAuditService.MissedPairSampler.StableHash(left.SourceRecordId) % (uint)n);

            if (partner == i) { selfPairsSkipped++; continue; }

            var right = records[partner];

            var isTruePair =
                groundTruth.TryGetValue(left.SourceRecordId, out var leftCanonical) &&
                groundTruth.TryGetValue(right.SourceRecordId, out var rightCanonical) &&
                string.Equals(leftCanonical, rightCanonical, StringComparison.Ordinal);

            if (isTruePair) { accidentallyIncluded++; continue; }

            sampled++;
            AccumulateColumnStats(left, right, columns, acc);
        }

        return (sampled, accidentallyIncluded, selfPairsSkipped, acc);
    }

    /// <summary>95% Wilson score interval. Unlike the normal approximation, it does not extend
    /// below 0 or above 1 when the observed rate sits at either extreme -- exactly the regime
    /// several of these columns occupy (country near 1, a rare column near 0). Internal (not
    /// private) so it is directly testable at the boundary rates, the case the normal
    /// approximation gets wrong and the reason Wilson was chosen over it.</summary>
    internal static (double Low, double High) WilsonInterval(long successes, long sampleSize)
    {
        if (sampleSize <= 0) return (0.0, 1.0);

        const double z = 1.959963984540054; // two-sided 95% normal quantile
        var n = (double)sampleSize;
        var p = successes / n;
        var z2 = z * z;
        var denom = 1.0 + z2 / n;
        var center = (p + z2 / (2 * n)) / denom;
        var margin = z * Math.Sqrt(p * (1 - p) / n + z2 / (4 * n * n)) / denom;

        return (Math.Clamp(center - margin, 0.0, 1.0), Math.Clamp(center + margin, 0.0, 1.0));
    }

    /// <summary>Builds one column's co-occurrence figure. Lift is the ratio of this rate to the
    /// control's rate for the same column, and is null -- not a divide-by-zero -- both when
    /// building the control's own entries (no lift applies to itself) and when the control
    /// observed zero base rate for the column, which is a real case for a rare column.</summary>
    private static FieldCoOccurrence BuildCoOccurrence(
        string column, long shared, long sampleSize, double? controlRateForLift)
    {
        var rate = sampleSize > 0 ? (double)shared / sampleSize : 0.0;
        var (low, high) = WilsonInterval(shared, sampleSize);
        double? lift = controlRateForLift is > 0 ? rate / controlRateForLift.Value : null;
        return new FieldCoOccurrence(column, shared, sampleSize, rate, low, high, lift);
    }

    /// <summary>True when the pair's organization-name field(s) share a RAW token (kept-suffix
    /// canonical form) that the fully-stripped canonical form no longer shares -- i.e. suffix
    /// stripping is why the tokens diverged. legalSuffixOnly is set when every such lost token is
    /// a recognised legal suffix (ORGANIZATIONNAMECANONICALIZER.IsLegalSuffix), isolating "the
    /// suffix list cost a match" from "the suffix list is fine, something else differed".</summary>
    private static bool IsNormalizationImplicated(
        EntityRecord left, EntityRecord right, MatchingProfile profile, out bool legalSuffixOnly)
    {
        legalSuffixOnly = false;
        var implicatedAny = false;

        foreach (var field in profile.Fields)
        {
            if (field.SemanticType != SemanticFieldType.OrganizationName) continue;
            if (!left.Fields.TryGetValue(field.Name, out var leftValue) || string.IsNullOrWhiteSpace(leftValue)) continue;
            if (!right.Fields.TryGetValue(field.Name, out var rightValue) || string.IsNullOrWhiteSpace(rightValue)) continue;

            var rawLeft = OrgCanonicalizer.CanonicalizeKeepingSuffixes(leftValue).ToHashSet(StringComparer.Ordinal);
            var rawRight = OrgCanonicalizer.CanonicalizeKeepingSuffixes(rightValue).ToHashSet(StringComparer.Ordinal);
            var sharedRaw = rawLeft.Intersect(rawRight, StringComparer.Ordinal).ToList();
            if (sharedRaw.Count == 0) continue;

            var sharedCanonical = OrgCanonicalizer.Canonicalize(leftValue)
                .ToHashSet(StringComparer.Ordinal)
                .Intersect(OrgCanonicalizer.Canonicalize(rightValue), StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            var lostToStripping = sharedRaw.Where(t => !sharedCanonical.Contains(t)).ToList();
            if (lostToStripping.Count == 0) continue;

            implicatedAny = true;
            if (lostToStripping.All(OrganizationNameCanonicalizer.IsLegalSuffix))
                legalSuffixOnly = true;
        }

        return implicatedAny;
    }

    /// <summary>Which configured strategy emitted a given interned key, resolved lazily and
    /// cached by key id (bounded by distinct key count, not corpus size). Picks the first
    /// strategy, in PROFILE order (a List, not a Dictionary/HashSet), whose output for one member
    /// record contains the key string -- deterministic regardless of process or hash seed.
    /// </summary>
    private static string OwningStrategyOf(
        int keyId, KeyIndex index, IReadOnlyList<EntityRecord> records, MatchingProfile profile,
        IStrategyRegistry registry, Dictionary<int, string> cache)
    {
        if (cache.TryGetValue(keyId, out var cached)) return cached;

        var normalization = registry.Normalization[profile.NormalizationStrategy];
        var memberRecordIndex = index.KeyMembers[keyId][0];
        var normalized = normalization.Normalize(records[memberRecordIndex], profile);
        var keyName = index.KeyNames[keyId];

        var owner = "unknown";
        foreach (var strategyName in profile.BlockingStrategies)
        {
            var keys = registry.Blocking[strategyName].GenerateKeys(normalized, profile);
            if (keys.Contains(keyName, StringComparer.Ordinal))
            {
                owner = strategyName;
                break;
            }
        }
        cache[keyId] = owner;
        return owner;
    }

    /// <summary>Block-size distribution built straight from the interned index -- bounded by
    /// distinct-key count, not corpus size, so it survives at 3.9M records where
    /// BlockingAuditService's string-keyed structures would not. Largest blocks are capped to the
    /// top N, tie-broken by key name (ordinal) so the cap never depends on array/dictionary
    /// enumeration order.</summary>
    private static BlockSizeHistogram BuildBlockHistogram(
        KeyIndex index, IReadOnlyList<EntityRecord> records, MatchingProfile profile,
        IStrategyRegistry registry, Dictionary<int, string> owningStrategyCache)
    {
        var buckets = new SortedDictionary<int, (int Count, long Slots)>();
        var maxSize = 0;
        for (var k = 0; k < index.KeyCount.Length; k++)
        {
            var size = index.KeyCount[k];
            if (size > maxSize) maxSize = size;
            var bucketIndex = size <= 1 ? 0 : (int)Math.Ceiling(Math.Log2(size));
            var (count, slots) = buckets.TryGetValue(bucketIndex, out var agg) ? agg : (0, 0L);
            buckets[bucketIndex] = (count + 1, slots + size);
        }

        var bucketList = buckets.Select(kv =>
        {
            var (min, max) = kv.Key == 0 ? (1, 1) : ((1 << (kv.Key - 1)) + 1, 1 << kv.Key);
            return new BlockingSizeBucket(min, max, kv.Value.Count, kv.Value.Slots);
        }).ToList();

        var largest = Enumerable.Range(0, index.KeyCount.Length)
            .OrderByDescending(k => index.KeyCount[k])
            .ThenBy(k => index.KeyNames[k], StringComparer.Ordinal)
            .Take(LargestBlockCount)
            .Select(k => new LargestBlock(
                index.KeyNames[k],
                OwningStrategyOf(k, index, records, profile, registry, owningStrategyCache),
                index.KeyCount[k]))
            .ToList();

        return new BlockSizeHistogram(bucketList, index.KeyCount.Length, maxSize, largest);
    }

    /// <summary>Deterministic bounded sample, ranked by a stable hash of the pair's ids with an
    /// ordinal tie-break -- same technique as BlockingAuditService.MissedPairSampler and for the
    /// same reason: ground truth is an IReadOnlyDictionary, so encounter order follows Dictionary
    /// iteration, which is not stable across runs. "First N encountered" would make the sample
    /// noise rather than a sample. Reuses MissedPairSampler's proven stable hash rather than a
    /// second, potentially-divergent implementation.</summary>
    private sealed class CappedPairSampler(int cap)
    {
        private readonly record struct RankKey(uint Rank, string Left, string Right) : IComparable<RankKey>
        {
            public int CompareTo(RankKey other)
            {
                var cmp = Rank.CompareTo(other.Rank);
                if (cmp != 0) return cmp;
                cmp = StringComparer.Ordinal.Compare(Left, other.Left);
                return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(Right, other.Right);
            }
        }

        private static readonly IComparer<RankKey> WorstFirst = Comparer<RankKey>.Create((a, b) => b.CompareTo(a));
        private readonly PriorityQueue<SampledPair, RankKey> _queue = new(WorstFirst);

        internal void Offer(string left, string right, string canonicalKey)
        {
            var rank = BlockingAuditService.MissedPairSampler.Rank(left, right);
            var key = new RankKey(rank, left, right);
            if (_queue.Count < cap)
            {
                _queue.Enqueue(new SampledPair(left, right, canonicalKey), key);
                return;
            }
            _queue.TryPeek(out _, out var worst);
            if (key.CompareTo(worst) >= 0) return;
            _queue.Enqueue(new SampledPair(left, right, canonicalKey), key);
            _queue.Dequeue();
        }

        internal IReadOnlyList<SampledPair> ToSortedList()
            => [.. _queue.UnorderedItems
                    .Select(x => x.Element)
                    .OrderBy(p => p.CanonicalKey, StringComparer.Ordinal)
                    .ThenBy(p => p.LeftSourceRecordId, StringComparer.Ordinal)
                    .ThenBy(p => p.RightSourceRecordId, StringComparer.Ordinal)];
    }
}
