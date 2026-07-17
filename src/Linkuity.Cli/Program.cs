namespace Linkuity.Cli;

public static class Program
{
    public static Task<int> Main(string[] args)
        => new LocalBatchRunner(new NativeMatchingProcess()).RunAsync(args, CancellationToken.None);
}
