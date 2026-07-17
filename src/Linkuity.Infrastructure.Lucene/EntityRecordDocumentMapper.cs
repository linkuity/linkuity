using System.Globalization;
using System.Text.Json;
using Linkuity.Core.Models;
using Lucene.Net.Documents;
using LuceneField = Lucene.Net.Documents.Field;

namespace Linkuity.Infrastructure.Lucene;

/// <summary>
/// Maps an <see cref="EntityRecord"/> to a Lucene document. Stored fields reconstruct the
/// scoring projection a candidate needs (id, project_id, source_record_id, fields-json);
/// source_id/ingest_batch_id/created_at remain stored for provenance but only the
/// projection is read back. Indexed fields drive retrieval: <c>blocking_key</c> (exact terms),
/// <c>phonetic</c> (Double-Metaphone codes), and <c>name</c> (name tokens, the fuzzy target).
/// The phonetic and name fields are projected from the record's namespaced blocking keys
/// with the prefix stripped, so indexed and query terms stay symmetric.
/// </summary>
internal static class EntityRecordDocumentMapper
{
    private const string PhoneticPrefix = "phonetic:";
    private const string NamePrefix = "name:";

    public static Document ToDocument(EntityRecord record)
    {
        var doc = new Document
        {
            new StringField(LuceneFields.Id, record.Id.ToString(), LuceneField.Store.YES),
            new StoredField(LuceneFields.ProjectId, record.ProjectId.ToString()),
            new StoredField(LuceneFields.SourceId, record.SourceId.ToString()),
            new StoredField(LuceneFields.IngestBatchId, record.IngestBatchId.ToString()),
            new StoredField(LuceneFields.SourceRecordId, record.SourceRecordId),
            new StoredField(LuceneFields.CreatedAt, record.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
            new StoredField(LuceneFields.FieldsJson, JsonSerializer.Serialize(record.Fields))
        };

        foreach (var key in record.BlockingKeys)
        {
            // Indexed only: answers exact term queries. Not stored — the scoring path never reads
            // a reconstructed candidate's BlockingKeys, so storing them was pure payload weight
            // (Milestone 26). The phonetic/name projections below remain the retrieval terms.
            doc.Add(new StringField(LuceneFields.BlockingKey, key, LuceneField.Store.NO));

            if (key.StartsWith(PhoneticPrefix, StringComparison.Ordinal))
                doc.Add(new StringField(LuceneFields.Phonetic, key[PhoneticPrefix.Length..], LuceneField.Store.NO));
            else if (key.StartsWith(NamePrefix, StringComparison.Ordinal))
                doc.Add(new StringField(LuceneFields.Name, key[NamePrefix.Length..], LuceneField.Store.NO));
        }

        return doc;
    }

    /// <summary>
    /// Reconstructs the <b>scoring projection</b> of a candidate from its stored fields: the
    /// fields the matching path consumes — <c>Id</c>, <c>ProjectId</c>, <c>SourceRecordId</c>, and
    /// <c>Fields</c>. <c>SourceId</c>, <c>IngestBatchId</c>, <c>CreatedAt</c>, and <c>BlockingKeys</c>
    /// are intentionally NOT reconstructed (no consumer reads them; see Milestone 26). Callers that
    /// pass a <see cref="Document"/> read with a field-limited visitor pay no cost for those fields.
    /// </summary>
    public static EntityRecord FromDocument(Document doc)
    {
        var fieldsJson = doc.Get(LuceneFields.FieldsJson);
        var fields = string.IsNullOrEmpty(fieldsJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(fieldsJson) ?? new Dictionary<string, string>();

        return new EntityRecord
        {
            Id = Guid.Parse(doc.Get(LuceneFields.Id)),
            ProjectId = Guid.Parse(doc.Get(LuceneFields.ProjectId)),
            SourceId = Guid.Empty,
            IngestBatchId = Guid.Empty,
            SourceRecordId = doc.Get(LuceneFields.SourceRecordId),
            Fields = fields,
            CreatedAt = default
        };
    }
}
