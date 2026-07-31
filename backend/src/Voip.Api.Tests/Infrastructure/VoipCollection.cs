using MageRide.TestKit;

namespace MageRide.Voip.Tests.Infrastructure;

/// <summary>
/// One Postgres and one Redpanda shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// The TestKit ships a collection per container and a test class can only join one, so this suite
/// declares its own over the TestKit's fixtures rather than hand-rolling containers.
/// </para>
/// <para>
/// <b>Redpanda is load-bearing, not scenery.</b> D6' §6's "expiring at trip end" cannot be shown
/// without it: a LiveKit token is checked at join and never again, so what actually ends a call is
/// the room being closed when `ride.events` says the ride is over. Asserting that against an
/// in-process handler call would test the handler and not the wiring.
/// </para>
/// <para>
/// <b>No Redis fixture</b>, because the service opens no Redis connection — see
/// <c>VoipApplication</c> for why. Asking for one would start a container this suite has nothing to
/// assert against.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class VoipCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<RedpandaFixture>
{
    public const string Name = "mageride-voip";
}
