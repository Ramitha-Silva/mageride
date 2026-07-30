using MageRide.TestKit;

namespace MageRide.TcpAdapter.Tests.Infrastructure;

/// <summary>
/// Skips a test whose containers could not start, naming which one and why.
/// </summary>
/// <remarks>
/// The integration half of this suite needs three containers and no in-memory substitute exists for
/// any of them (see <see cref="AdapterCollection"/>). A skip with the fixture's own reason on it is the
/// difference between "Docker is not available on this machine" and "the assertion failed", and this
/// suite runs on a build host that also hosts the lightweight production replica — so "the port was
/// taken" is a real answer.
/// </remarks>
internal static class Skip
{
    /// <summary>Skips unless every fixture is running.</summary>
    public static void IfUnavailable(params ContainerFixture[] fixtures)
    {
        ArgumentNullException.ThrowIfNull(fixtures);

        foreach (var fixture in fixtures)
        {
            Assert.SkipWhen(!fixture.IsAvailable, fixture.SkipReason ?? "a container did not start");
        }
    }
}
