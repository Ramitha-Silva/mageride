using MageRide.TestKit;

namespace MageRide.FleetBilling.Tests.Infrastructure;

/// <summary>
/// One Postgres and one Redpanda shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// The TestKit ships a collection per container and a test class can only join one, so this suite
/// declares its own over the two fixtures it needs (following <c>WalletCollection</c>, C046).
/// </para>
/// <para>
/// <b>Postgres carries the invariants, not just the rows.</b> "An invoice's lines sum to its total"
/// is Σ over a table; "the entry balances" is <c>trg_balanced</c>, a DEFERRABLE constraint trigger
/// that only fires at COMMIT; "re-running generation is idempotent" is three unique indexes;
/// "a settled invoice carries a posting and an unsettled one does not" is a pair of CHECKs. Every
/// one of those is a claim about the server.
/// </para>
/// <para>
/// <b>Redpanda is where <c>fleet.events</c> lands</b>, so the outbox claim is asserted by consuming
/// the topic rather than by reading the table this service wrote. There is no Redis fixture,
/// because this service uses none — the D-08 balance cache is a driver's and belongs to
/// dispatch-svc and wallet-svc.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class FleetBillingCollection
    : ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedisFixture>,
      ICollectionFixture<RedpandaFixture>
{
    public const string Name = "mageride-fleet-billing";
}
