using MageRide.TestKit;

namespace MageRide.Analytics.Tests.Infrastructure;

/// <summary>
/// One migrated Postgres shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// Postgres only. This component holds no Redis connection, publishes nothing to Redpanda and
/// speaks to no broker: it reads six tables and writes one. A fixture for something the component
/// does not use would cost a container per test run and prove nothing.
/// </para>
/// <para>
/// <b>The database is where the claims live.</b> Idempotency is a primary key, the Colombo day
/// boundary is a half-open range over real <c>TIMESTAMPTZ</c> columns, the non-negative money is a
/// CHECK, and the reconciliation is two different SQL formulations of the same question having to
/// agree. None of those is assertable against a fake.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class AnalyticsCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "mageride-analytics";
}
