using MageRide.TestKit;

namespace MageRide.Security.Tests.AntiSpoof;

/// <summary>
/// The infrastructure the <c>AntiSpoof</c> category needs: one EMQX, one Postgres, one Redis.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the exception to the rule in this project's CLAUDE.md, and it is a scoped one.</b>
/// C127's suite is docker-free and runs on a bare agent in under ten seconds, because everything it
/// asserts — route tables, bearer options, gateway policy — is a property of composed code. Three
/// of C128's four definition-of-done items are not: "a cross-vehicle publish attempt is refused by
/// EMQX", "a cloned IMEI quarantines both devices" and "revocation takes effect within 60 s" are
/// claims about a broker and a database doing something, and a version of them that ran without
/// either would be asserting the test double.
/// </para>
/// <para>
/// So the category is container-backed and every test in it <b>skips loudly</b> when Docker is
/// absent, in the platform's existing idiom
/// (<c>Assert.SkipWhen(!fixture.IsAvailable, fixture.SkipReason)</c>). The docker-free half of
/// C128 — the position corpus, the threshold fences and the broker-policy assertions — is
/// deliberately the larger half and needs none of this.
/// </para>
/// <para>
/// Nothing here is reset between tests. Every test works in its own namespace instead: fresh
/// vehicle ids, fresh IMEIs, fresh user ids.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class AntiSpoofCollection
    : ICollectionFixture<EmqxFixture>,
      ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedisFixture>
{
    public const string Name = "mageride-antispoof";
}
