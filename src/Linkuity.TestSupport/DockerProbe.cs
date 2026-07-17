using System.Diagnostics;

namespace Linkuity.TestSupport;

public static class DockerProbe
{
    private static readonly Lazy<bool> _cached = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAvailable() => _cached.Value;

    private static bool Probe()
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            proc.Start();
            proc.WaitForExit(10_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
