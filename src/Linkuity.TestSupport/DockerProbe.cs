using System.Diagnostics;

namespace Linkuity.TestSupport;

/// <summary>
/// Answers one question for container-backed tests: should they run, or skip?
///
/// The distinction that matters is <b>absent</b> versus <b>busy</b>. A machine without Docker
/// should skip these tests. A machine whose Docker is merely slow — because the test run itself
/// is starting containers — must not, because skipping produces a green build that verified
/// nothing. A silent skip is worse than a loud failure: the failure gets investigated.
/// </summary>
public static class DockerProbe
{
    /// <summary>
    /// Generous on purpose. `docker info` is fast when idle and can take many seconds while
    /// containers are starting, which is exactly when this runs.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private static readonly Lazy<bool> Cached = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAvailable() => Cached.Value;

    /// <summary>
    /// Turns a probe outcome into a run/skip decision. Separated from process handling so the
    /// decision is testable without a Docker daemon.
    /// </summary>
    /// <param name="exitedInTime">Whether `docker info` completed before the timeout.</param>
    /// <param name="exitCode">Its exit code, or null when it did not finish.</param>
    public static bool InterpretProbe(bool exitedInTime, int? exitCode)
        // Timing out means the daemon answered too slowly, not that it is missing. Report
        // available so the tests attempt their work and fail visibly if the daemon really is
        // unusable, rather than skipping and reporting success.
        => !exitedInTime || exitCode == 0;

    private static bool Probe()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            // Only a missing executable means "no Docker here".
            if (proc is null)
                return false;

            var exitedInTime = proc.WaitForExit((int)ProbeTimeout.TotalMilliseconds);
            if (!exitedInTime)
            {
                // Reading ExitCode on a live process throws, so the previous
                // `WaitForExit(10_000); return proc.ExitCode == 0;` reported "no Docker" for a
                // merely-slow daemon — and skipped every container test in the assembly.
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return InterpretProbe(exitedInTime: false, exitCode: null);
            }

            return InterpretProbe(exitedInTime: true, proc.ExitCode);
        }
        catch
        {
            // Win32Exception when `docker` is not on PATH: genuinely absent.
            return false;
        }
    }
}
