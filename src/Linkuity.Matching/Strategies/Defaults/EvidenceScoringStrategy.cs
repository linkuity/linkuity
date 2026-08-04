using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Additive evidence scoring. Each compared field contributes a fixed amount of evidence to a
/// running total on a shared log-odds scale; a field the records do not share contributes zero.
/// <para>
/// This is what removes the field-shape defect: the older scorers divide by the weights of the
/// signals that happened to be emitted, so the same threshold demands strong agreement on a wide
/// record and weak agreement on a narrow one. Here, adding a field to a record changes the total
/// but never changes what the fields already there were worth.
/// </para>
/// <para>
/// It is NOT a calibrated Fellegi-Sunter model while m and u are hand-set, and must not be
/// described as one. It is the structurally correct model those measurements would slot into.
/// </para>
/// </summary>
public sealed class EvidenceScoringStrategy : IScoringStrategy
{
    public string Name => "evidence";

    public SignalShape Consumes => SignalShape.PerField;

    /// <summary>Unbounded on both sides — a threshold is an absolute quantity of evidence, not a
    /// fraction of the evidence that happened to exist.</summary>
    public ScoreScale Scale => ScoreScale.LogOdds;

    public ScoreResult Score(IReadOnlyList<SimilaritySignal> signals, MatchingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(profile);

        var fields = new Dictionary<string, ProfileField>(StringComparer.Ordinal);
        foreach (var field in profile.Fields)
            fields[field.Name] = field;

        double total = 0;
        var breakdown = new List<ScoreContribution>(signals.Count);

        foreach (var signal in signals)
        {
            // A signal naming no profile field cannot be priced. Scoring it at some default would
            // invent evidence; dropping it is the only honest option, and the shipped similarity
            // strategy never produces one.
            if (!fields.TryGetValue(signal.Name, out var field))
                continue;

            var evidence = EvidenceFor(field, profile);

            var contribution = signal.Outcome == ComparisonOutcome.Compared
                ? evidence.EvidenceFor(signal.Value)
                : 0;

            total += contribution;
            breakdown.Add(new ScoreContribution(
                signal.Name, signal.Value, evidence.AgreementBits, contribution, signal.Outcome));
        }

        return new ScoreResult(total, breakdown);
    }

    /// <summary>
    /// Hard failure, never a default. Deriving m and u from <see cref="ProfileField.Weight"/>
    /// would be inventing statistics, and the throw is what enforces equal treatment across
    /// taxonomies: a profile nobody has given numbers to does not run, so no taxonomy can be
    /// quietly left behind while the others move.
    /// </summary>
    private static FieldEvidence EvidenceFor(ProfileField field, MatchingProfile profile)
        => field.Evidence ?? throw new InvalidOperationException(
            $"Matchable field '{field.Name}' in the '{profile.ContentType}' profile has no evidence " +
            "parameters. The evidence scorer requires sameEntityAgreement and chanceAgreement on " +
            "every matchable field; it will not infer them from the field's weight.");
}
