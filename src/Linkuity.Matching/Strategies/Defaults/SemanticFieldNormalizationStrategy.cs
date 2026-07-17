using Linkuity.Core.Models;
using Linkuity.Core.Normalization;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Field-level normalization that wraps the existing FieldNormalizer (E.164 phone,
/// lowercase email/domain, ISO dates, honorific stripping). Fields the profile does
/// not map pass through unchanged. This must be idempotent on the records the engine
/// scores; durable records are normalized upstream, so re-applying it is a no-op.
/// </summary>
public sealed class SemanticFieldNormalizationStrategy : INormalizationStrategy
{
    public string Name => "semantic-field";

    public EntityRecord Normalize(EntityRecord record, MatchingProfile profile)
    {
        var typesByField = profile.Fields.ToDictionary(f => f.Name, f => f.SemanticType, StringComparer.OrdinalIgnoreCase);

        var normalized = new Dictionary<string, string>(record.Fields.Count, StringComparer.Ordinal);
        foreach (var (field, value) in record.Fields)
        {
            normalized[field] = typesByField.TryGetValue(field, out var type)
                ? FieldNormalizer.Normalize(value, type)
                : value;
        }

        return new EntityRecord
        {
            Id = record.Id,
            ProjectId = record.ProjectId,
            SourceId = record.SourceId,
            IngestBatchId = record.IngestBatchId,
            SourceRecordId = record.SourceRecordId,
            Fields = normalized,
            BlockingKeys = record.BlockingKeys,
            CreatedAt = record.CreatedAt
        };
    }
}
