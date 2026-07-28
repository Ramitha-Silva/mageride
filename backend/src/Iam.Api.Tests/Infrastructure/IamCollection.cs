using MageRide.TestKit;

namespace MageRide.Iam.Tests.Infrastructure;

/// <summary>
/// One Postgres and one Redis shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// The TestKit ships a collection per container (<c>PostgresCollection</c>,
/// <c>RedisCollection</c>) and a test class can only join one. iam-svc needs both — the sessions
/// are in Postgres and the D-32 buckets are in Redis — so the collection is declared here over
/// the TestKit's fixtures rather than by hand-rolling containers.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class IamCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<RedisFixture>
{
    public const string Name = "mageride-iam";
}
