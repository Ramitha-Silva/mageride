using MageRide.TestKit;

namespace MageRide.FleetHealth.Tests.Infrastructure;

/// <summary>
/// One Postgres, one Redpanda and one EMQX shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// The TestKit ships a collection per container and a test class can only join one, so this suite
/// declares its own over the TestKit's fixtures rather than hand-rolling containers (following
/// <c>DispatchCollection</c>, C034).
/// </para>
/// <para>
/// <b>Postgres is load-bearing beyond "a place to put rows".</b> US-3.13's four states are decided by
/// <c>telemetry.device_health_state()</c>, "exactly one alert per window" is a unique index, the fleet
/// scoping is a security-barrier view over a session GUC, and the window rollup is a TimescaleDB
/// continuous aggregate. Every one of those is a claim about the server.
/// </para>
/// <para>
/// <b>No Redis</b>, because the service registers no Redis client — see
/// <c>FleetHealthApplication</c>'s <c>UseRedis = false</c>. A fixture for something the service cannot
/// reach would test a fiction.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class FleetHealthCollection
    : ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedpandaFixture>,
      ICollectionFixture<EmqxFixture>
{
    public const string Name = "mageride-fleet-health";
}
