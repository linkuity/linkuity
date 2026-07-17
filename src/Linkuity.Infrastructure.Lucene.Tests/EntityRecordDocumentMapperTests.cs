using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;

namespace Linkuity.Infrastructure.Lucene.Tests;

public class EntityRecordDocumentMapperTests
{
    [Fact]
    public void ToDocument_ThenFromDocument_ReconstructsScoringProjection()
    {
        var original = LuceneTestRecords.Person("r1", new Dictionary<string, string>
        {
            ["first_name"] = "Alice",
            ["last_name"] = "Smith",
            ["email"] = "alice@example.com",
            ["date_of_birth"] = "1990-01-02"
        });

        var doc = EntityRecordDocumentMapper.ToDocument(original);
        var restored = EntityRecordDocumentMapper.FromDocument(doc);

        // Fields the scoring path consumes are reconstructed exactly.
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.ProjectId, restored.ProjectId);
        Assert.Equal(original.SourceRecordId, restored.SourceRecordId);
        Assert.Equal(original.Fields, restored.Fields);

        // Fields no consumer reads are NOT reconstructed (scoring projection, not full fidelity).
        Assert.Equal(Guid.Empty, restored.SourceId);
        Assert.Equal(Guid.Empty, restored.IngestBatchId);
        Assert.Empty(restored.BlockingKeys);

        // Blocking keys are no longer STORED (they remain indexed for retrieval). Document.GetValues
        // on an in-memory Document ignores the Store flag (it just returns whatever fieldsData was
        // set), so the only way to observe "not stored" pre-index-round-trip is the field's type.
        var blockingKeyField = doc.GetField("blocking_key");
        Assert.NotNull(blockingKeyField);
        Assert.False(blockingKeyField!.IndexableFieldType.IsStored);
    }

    [Fact]
    public void ToDocument_IndexesPhoneticAndNameTerms()
    {
        var record = LuceneTestRecords.Person("r1", new Dictionary<string, string> { ["last_name"] = "Smith" });

        var doc = EntityRecordDocumentMapper.ToDocument(record);

        // name: and phonetic: keys are projected into dedicated indexed fields with the prefix stripped.
        Assert.Contains("smith", doc.GetValues("name"));
        Assert.NotEmpty(doc.GetValues("phonetic"));
        Assert.DoesNotContain(doc.GetValues("phonetic"), v => v.StartsWith("phonetic:", StringComparison.Ordinal));
    }
}
