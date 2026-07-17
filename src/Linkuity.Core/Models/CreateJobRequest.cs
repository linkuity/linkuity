namespace Linkuity.Core.Models;

public class CreateJobRequest
{
    public required MatchConfiguration Configuration { get; init; }
    public bool AutoStart { get; init; }
    public MergeConfiguration? MergeConfiguration { get; init; }
}
