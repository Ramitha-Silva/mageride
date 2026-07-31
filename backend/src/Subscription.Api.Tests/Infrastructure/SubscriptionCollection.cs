using MageRide.TestKit;

namespace MageRide.Subscriptions.Tests.Infrastructure;

/// <summary>
/// One Postgres and one Redis shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// The TestKit ships a collection per container and a test class can only join one, so this suite
/// declares its own over the two it needs (following <c>DispatchCollection</c>, C034).
/// </para>
/// <para>
/// <b>Postgres carries the invariants, not just the rows.</b> "Debits once" is the composite primary
/// key of <c>billing.daily_fee_charges</c> and the UNIQUE on
/// <c>billing.journal_entries.idempotency_key</c>; "a waived day moved no money" is
/// <c>ck_daily_fee_charges_waiver</c>; "first month free cannot be re-claimed" is
/// <c>ux_monthly_subscriptions_vehicle_period</c> and
/// <c>ck_monthly_subscriptions_period_first_day</c>. Every one of those is a claim about the server.
/// </para>
/// <para>
/// <b>Redis is here for wallet-svc</b>, which this suite boots for real: its D-08 write-through
/// resolves an <c>IConnectionMultiplexer</c> on the first debit. Nothing in subscription-svc touches
/// Redis — it holds no cache and reads no balance.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SubscriptionCollection
    : ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedisFixture>
{
    public const string Name = "mageride-subscription";
}
