using MageRide.TestKit;

namespace MageRide.Fleet.Tests.Infrastructure;

/// <summary>
/// One Postgres shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// The TestKit ships a collection per container and a test class can only join one, so this suite
/// declares its own over the TestKit's fixture rather than hand-rolling a container.
/// </para>
/// <para>
/// <b>Postgres is the component, not a place to put rows.</b> Migration 1806's RESTRICTIVE
/// policies, the <c>mageride_fleet_reader</c> role, the three security-barrier views,
/// <c>ux_payout_profile_verified</c>, <c>ux_fleets_business_reg_active</c> and the
/// <c>superseded</c> status are all claims about the server — and the definition of done asks for
/// a refusal that comes from the database rather than from this service's SQL.
/// </para>
/// <para>
/// <b>No Redis and no Redpanda fixture</b>, because the service opens neither — see
/// <c>FleetApplication</c> for why each is off. Asking for one would start a container this suite
/// has nothing to assert against.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class FleetCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "mageride-fleet";
}
