namespace Linkuity.Cli;

public interface IMatchingProcess
{
    Task RunAsync(string artifactRoot, string jobId, CancellationToken ct);
}
