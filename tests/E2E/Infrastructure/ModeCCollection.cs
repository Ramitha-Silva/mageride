using MageRide.TestKit;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// One Postgres, one Redis, one Redpanda and one EMQX, shared by every Mode C scenario.
/// </summary>
/// <remarks>
/// <para>
/// All four, always. A Mode C ride touches every one of them — the aggregate and the durable
/// timers are Postgres, the candidate index and the R-10 reservation are Redis, the outbox and
/// both consumers are Redpanda, and R-15's last will is the broker — so there is no scenario in
/// this assembly that could run against a subset. The TestKit ships a collection per container and
/// a class may only join one, so this defines a collection over all four (the shape
/// <c>DispatchCollection</c> established in C034).
/// </para>
/// <para>
/// The containers are started once and never reset between scenarios. Every scenario mints fresh
/// passengers, drivers and vehicles, so nothing it asserts is about a row another scenario could
/// have written — with one exception, the candidate pool, which <see cref="ModeCFleet"/> clears at
/// start-up for the reason recorded there.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ModeCCollection
    : ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedisFixture>,
      ICollectionFixture<RedpandaFixture>,
      ICollectionFixture<EmqxFixture>
{
    public const string Name = "mageride-e2e-mode-c";
}
