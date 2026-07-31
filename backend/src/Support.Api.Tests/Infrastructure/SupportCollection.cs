using MageRide.TestKit;

namespace MageRide.Support.Tests.Infrastructure;

/// <summary>
/// One Postgres shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// The TestKit ships a collection per container and a test class can only join one, so this suite
/// declares its own over the TestKit's fixture rather than hand-rolling a container.
/// </para>
/// <para>
/// <b>Postgres is load-bearing beyond "a place to put rows."</b> The guarded <c>UPDATE</c> that makes
/// two agents one decision, the <c>FOR UPDATE</c> that makes <c>from_status</c> exact,
/// <c>ck_tickets_resolution</c>, the foreign key from <c>support.tickets.screenshot_upload_id</c> onto
/// <c>docs.uploads</c>, and migration 1902's real Sinhala and Tamil FAQ strings are all claims about
/// the server.
/// </para>
/// <para>
/// <b>No Redis fixture</b>, because the service opens no Redis connection — see
/// <c>SupportApplication</c> for why. Asking for one here would start a container this suite has
/// nothing to assert against.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SupportCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "mageride-support";
}
