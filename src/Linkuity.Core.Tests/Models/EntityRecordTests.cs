using Linkuity.Core.Models;

namespace Linkuity.Core.Tests.Models;

public class EntityRecordTests
{
    [Fact]
    public void SupersededAt_DefaultsToNull()
    {
        var record = new EntityRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            IngestBatchId = Guid.NewGuid(),
            SourceRecordId = "s-1",
            Fields = new Dictionary<string, string>(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.Null(record.SupersededAt);
    }

    [Fact]
    public void SupersededAt_SetViaWithExpression_LeavesOtherFieldsUnchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new EntityRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            IngestBatchId = Guid.NewGuid(),
            SourceRecordId = "s-1",
            Fields = new Dictionary<string, string> { ["email"] = "old@example.com" },
            CreatedAt = now
        };

        var superseded = record with { SupersededAt = now.AddMinutes(1) };

        Assert.Equal(now.AddMinutes(1), superseded.SupersededAt);
        Assert.Equal(record.Id, superseded.Id);
        Assert.Equal("old@example.com", superseded.Fields["email"]);
    }
}
