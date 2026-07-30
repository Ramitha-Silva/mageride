using MageRide.TestKit;

namespace MageRide.Content.Tests.Infrastructure;

/// <summary>
/// One Postgres and one Redis shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// The TestKit ships a collection per container and a test class can only join one, so this suite
/// declares its own over the TestKit's fixtures rather than hand-rolling containers (following
/// <c>QueryCollection</c>, C042).
/// </para>
/// <para>
/// <b>Postgres is load-bearing beyond "a place to put rows."</b> The trilingual rule is a
/// <c>DEFERRABLE INITIALLY DEFERRED</c> constraint trigger that only fires at COMMIT; the active-city
/// filter is a <c>WHERE</c> clause; the seeded Sinhala and Tamil strings the endpoints serve are
/// migrations 1902 and 1903. Every one of those is a claim about the server.
/// </para>
/// <para>
/// <b>Redis is load-bearing too</b>, for one thing: the cross-replica cache purge is a pub/sub
/// message, and "a template change is visible to notification-svc" is only interesting if the replica
/// that did not publish it also sees it.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ContentCollection
    : ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedisFixture>
{
    public const string Name = "mageride-content";
}
