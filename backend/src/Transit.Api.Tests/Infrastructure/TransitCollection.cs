using MageRide.TestKit;

namespace MageRide.Transit.Tests.Infrastructure;

/// <summary>
/// One Postgres shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// The TestKit ships a collection per container and a test class can only join one, so this suite
/// declares its own over the TestKit's fixture rather than hand-rolling a container.
/// </para>
/// <para>
/// <b>Postgres is load-bearing beyond "a place to put rows."</b> The cache refresh is driven by
/// Postgres' own <c>LISTEN/NOTIFY</c> (D6' I-32.1 names the channel), the GTFS halts are
/// <c>GEOGRAPHY(POINT,4326)</c> columns that need PostGIS, and
/// <c>ux_gtfs_feed_one_active</c> is what makes "the active feed" a single row at all.
/// </para>
/// <para>
/// <b>No Redis and no Redpanda fixture</b>, because the service opens neither — see
/// <c>TransitApplication</c> for why each is off.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class TransitCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "mageride-transit";
}
