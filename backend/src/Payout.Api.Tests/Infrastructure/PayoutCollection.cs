using MageRide.TestKit;

namespace MageRide.Payout.Tests.Infrastructure;

/// <summary>
/// One Postgres and one Redis shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Postgres carries the invariants, not just the rows.</b> "One sweep per Colombo date" is
/// `run_date` UNIQUE; "one instruction per driver per batch" is `ux_payouts_batch_driver`; "a FAILED
/// payout says why" is `ck_payouts_failure_reason`; "the debit and the instruction are two halves"
/// is `journal_entry_id NOT NULL`. Every one is a claim about the server.
/// </para>
/// <para>
/// <b>Redis is wallet-svc's, not this service's.</b> payout-svc opens no Redis connection — the
/// fixture is here because the real wallet-svc this suite boots needs one for the D-08 balance
/// cache it writes through.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class PayoutCollection
    : ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedisFixture>
{
    public const string Name = "mageride-payout";
}
