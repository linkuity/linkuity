using Linkuity.Core.Models;

namespace Linkuity.Matching.Tests;

internal static class TestRecords
{
    /// <summary>
    /// Builds an EntityRecord from field values. Blocking keys default to the
    /// durable matcher's GenerateBlockingKeys output so candidates mirror stored records.
    /// </summary>
    public static EntityRecord Person(
        string sourceRecordId,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<string>? blockingKeys = null)
        => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.Empty,
            SourceId = Guid.Empty,
            IngestBatchId = Guid.Empty,
            SourceRecordId = sourceRecordId,
            Fields = fields,
            BlockingKeys = blockingKeys ?? [],
            CreatedAt = DateTimeOffset.UnixEpoch
        };
}
