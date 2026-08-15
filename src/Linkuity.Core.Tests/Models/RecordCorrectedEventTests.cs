using Linkuity.Core.Models;

namespace Linkuity.Core.Tests.Models;

public class RecordCorrectedEventTests
{
    [Fact]
    public void Constructs_WithAllRequiredFields()
    {
        var evt = new RecordCorrectedEvent
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            SupersededEntityRecordId = Guid.NewGuid(),
            CorrectedEntityRecordId = Guid.NewGuid(),
            PreviousFields = new Dictionary<string, string> { ["email"] = "old@example.com" },
            NewFields = new Dictionary<string, string> { ["email"] = "new@example.com" },
            PreviousClusterId = Guid.NewGuid(),
            IngestBatchId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal("old@example.com", evt.PreviousFields["email"]);
        Assert.Equal("new@example.com", evt.NewFields["email"]);
        Assert.NotNull(evt.PreviousClusterId);
    }

    [Fact]
    public void PreviousClusterId_NullWhenRecordWasAlreadyASingleton()
    {
        var evt = new RecordCorrectedEvent
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            SupersededEntityRecordId = Guid.NewGuid(),
            CorrectedEntityRecordId = Guid.NewGuid(),
            PreviousFields = new Dictionary<string, string>(),
            NewFields = new Dictionary<string, string>(),
            PreviousClusterId = null,
            IngestBatchId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.Null(evt.PreviousClusterId);
    }
}
