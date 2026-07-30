using MageRide.TestKit;

namespace MageRide.Wallet.Tests.Infrastructure;

/// <summary>
/// One Postgres, one Redis and one Redpanda shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// The TestKit ships a collection per container and a test class can only join one, so this suite
/// declares its own over the TestKit's fixtures (following <c>DispatchCollection</c>, C034).
/// </para>
/// <para>
/// <b>Postgres carries the invariants, not just the rows.</b> The balanced-entry rule is a deferred
/// constraint trigger; "credit once" is two unique indexes; the voucher's
/// <c>credited = denomination</c> is a CHECK; the transfer's not-self and posting rules are two more.
/// Every one of those is a claim about the server, and several can only be observed at COMMIT.
/// </para>
/// <para>
/// <b>Redis is the D-08 seam</b> — the key dispatch-svc's wallet gate reads — and <b>Redpanda is where
/// <c>wallet.events</c> lands</b>, so the outbox claim is asserted by consuming the topic rather than
/// by reading the table this service wrote.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class WalletCollection
    : ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedisFixture>,
      ICollectionFixture<RedpandaFixture>
{
    public const string Name = "mageride-wallet";
}
