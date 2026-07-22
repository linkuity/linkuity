namespace Linkuity.Core.Models;

public class Job
{
    public required Guid Id { get; init; }
    public required JobState State { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required bool AutoStart { get; init; }
    public MergeConfiguration? MergeConfiguration { get; init; }
    public int RecordCount { get; set; }
}
