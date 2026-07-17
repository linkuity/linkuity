namespace Linkuity.Infrastructure.Lucene;

/// <summary>Lucene document field names for an indexed <c>EntityRecord</c>.</summary>
internal static class LuceneFields
{
    public const string Id = "id";
    public const string ProjectId = "project_id";
    public const string SourceId = "source_id";
    public const string IngestBatchId = "ingest_batch_id";
    public const string SourceRecordId = "source_record_id";
    public const string CreatedAt = "created_at";
    public const string FieldsJson = "fields_json";
    public const string BlockingKey = "blocking_key";
    public const string Phonetic = "phonetic";
    public const string Name = "name";
}
