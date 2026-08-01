using MageRide.TestKit;

namespace MageRide.AdminBff.Tests.Infrastructure;

/// <summary>
/// One Postgres shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// The TestKit ships a collection per container and a test class can only join one, so this suite
/// declares its own over the TestKit's fixture rather than hand-rolling a container.
/// </para>
/// <para>
/// <b>Postgres is load-bearing beyond "a place to put rows."</b> <c>audit.events</c> is where the
/// D-35 fence is actually observable, the C061 rollup this dashboard reads is five aggregates over
/// six schemas, and <c>ux_vehicles_regno_active</c> is what makes a duplicate train number a 409
/// rather than a second live registration.
/// </para>
/// <para>
/// <b>No Redis and no Redpanda fixture</b>, because the service opens neither on these paths — see
/// <c>AdminBffApplication</c> for why each is off or unused.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class AdminBffCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "mageride-admin-bff";
}
