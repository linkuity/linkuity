using Linkuity.TestSupport;

namespace Linkuity.Infrastructure.Postgres.Tests;

/// <summary>
/// The probe decides whether container-backed tests run or skip. Getting it wrong in the
/// permissive direction produces a visible failure; getting it wrong in the strict direction
/// produces a green build that verified nothing, which is far more expensive.
///
/// No Docker daemon is involved here — the decision is tested directly, which is why it is a
/// separate function from the process handling.
/// </summary>
public class DockerProbeTests
{
    [Fact]
    public void ExitZero_MeansAvailable()
        => Assert.True(DockerProbe.InterpretProbe(exitedInTime: true, exitCode: 0));

    [Fact]
    public void NonZeroExit_MeansUnavailable()
        => Assert.False(DockerProbe.InterpretProbe(exitedInTime: true, exitCode: 1));

    /// <summary>
    /// The case that made the suite lie. `docker info` is slow precisely when the run is
    /// starting containers, and the previous implementation read ExitCode from a process that
    /// had not exited — which throws, was swallowed, and reported "no Docker". Every
    /// container-backed test in the assembly then skipped and the build passed green.
    /// </summary>
    [Fact]
    public void Timeout_MeansAvailable_SoTestsRunAndFailLoudlyRatherThanSkipSilently()
        => Assert.True(DockerProbe.InterpretProbe(exitedInTime: false, exitCode: null));
}
