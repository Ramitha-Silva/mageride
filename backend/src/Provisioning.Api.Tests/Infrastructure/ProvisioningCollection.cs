using MageRide.TestKit;

namespace MageRide.Provisioning.Tests.Infrastructure;

/// <summary>
/// One Postgres, one Redis, one Redpanda and one EMQX shared by every integration test here.
/// </summary>
/// <remarks>
/// provisioning-svc needs all four and no fewer. Postgres holds the binding and the outbox row;
/// Redis holds the <c>imei:{imei}</c> cache and the pub/sub channel T-12's sub-second half travels
/// on; Redpanda is what the outbox dispatcher drains to, so "the revocation is durable" cannot be
/// shown without a broker; and EMQX is where the DoD's first line lands — a bound tracker
/// authenticating with the certificate this service minted.
/// <para>
/// The pure suites (IMEI, CSV, the CA) take no fixture at all and run on a machine with no Docker.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ProvisioningCollection
    : ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedisFixture>,
      ICollectionFixture<RedpandaFixture>,
      ICollectionFixture<EmqxFixture>
{
    public const string Name = "mageride-provisioning";
}
