using Linkuity.Core.Models;

namespace Linkuity.Core.Tests.Models;

public class RecordDeletedEventTests
{
    [Fact]
    public void Constructs_WithAllRequiredFields()
    {
        var evt = new RecordDeletedEvent
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            DeletedEntityRecordId = Guid.NewGuid(),
            PreviousFields = new Dictionary<string, string> { ["email"] = "alice@example.com" },
            PreviousClusterId = Guid.NewGuid(),
            IngestBatchId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal("alice@example.com", evt.PreviousFields["email"]);
        Assert.NotNull(evt.PreviousClusterId);
    }

    [Fact]
    public void PreviousClusterId_NullWhenRecordWasAlreadyASingleton()
    {
        var evt = new RecordDeletedEvent
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            DeletedEntityRecordId = Guid.NewGuid(),
            PreviousFields = new Dictionary<string, string>(),
            PreviousClusterId = null,
            IngestBatchId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.Null(evt.PreviousClusterId);
    }
}
