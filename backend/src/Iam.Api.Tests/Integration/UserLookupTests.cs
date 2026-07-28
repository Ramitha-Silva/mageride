using System.Net;
using System.Security.Cryptography;
using System.Text;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// DoD: "an unregistered phone lookup returns registered:false and stores only a hash" —
/// <c>GET /v1/users/lookup</c>, the P-03 proxy-booking oracle.
/// </summary>
[Collection<IamCollection>]
public sealed class UserLookupTests(PostgresFixture postgres, RedisFixture redis)
{
    private static byte[] ExpectedHash(string phoneE164) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(IamHarness.TestPhoneHashKey), Encoding.UTF8.GetBytes(phoneE164));

    [Fact]
    public async Task An_unregistered_number_answers_false_and_leaves_only_a_hash_behind()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        var response = await harness.GetInternalAsync($"/v1/users/lookup?phone={Uri.EscapeDataString(phone)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await IamHarness.ReadJsonAsync(response);
        Assert.False(body.GetProperty("registered").GetBoolean());
        Assert.False(body.TryGetProperty("userId", out _));

        var rows = await harness.Seed.PhoneLookupsAsync();
        var row = Assert.Single(rows, candidate => candidate.PhoneHash.SequenceEqual(ExpectedHash(phone)));

        Assert.False(row.Registered);
        Assert.Null(row.UserId);
        Assert.Equal("ride-svc", row.Caller);

        // The clear number is nowhere in the row — the whole point of P-03's "hashed at rest".
        Assert.DoesNotContain(phone, Encoding.UTF8.GetString(row.PhoneHash), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registered_number_answers_true_with_the_account_id()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();
        var session = await harness.SignInAsync(phone, "handset");

        var body = await IamHarness.ReadJsonAsync(
            await harness.GetInternalAsync($"/v1/users/lookup?phone={Uri.EscapeDataString(phone)}"));

        Assert.True(body.GetProperty("registered").GetBoolean());
        Assert.Equal(session.UserId, body.GetProperty("userId").GetString());

        var rows = await harness.Seed.PhoneLookupsAsync();
        var row = Assert.Single(rows, candidate => candidate.PhoneHash.SequenceEqual(ExpectedHash(phone)));

        Assert.True(row.Registered);
        Assert.Equal(Guid.Parse(session.UserId), row.UserId);
    }

    /// <summary>
    /// The digest is keyed and deterministic, so ride-svc asking twice about one number produces
    /// two rows that correlate — which is what the audit is for.
    /// </summary>
    [Fact]
    public async Task Two_lookups_of_one_number_hash_identically()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        await harness.GetInternalAsync($"/v1/users/lookup?phone={Uri.EscapeDataString(phone)}");
        await harness.GetInternalAsync($"/v1/users/lookup?phone={Uri.EscapeDataString(phone)}");

        var matching = (await harness.Seed.PhoneLookupsAsync())
            .Where(row => row.PhoneHash.SequenceEqual(ExpectedHash(phone)))
            .ToArray();

        Assert.Equal(2, matching.Length);
    }

    [Fact]
    public async Task A_local_spelling_resolves_to_the_same_account_as_its_E164_form()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();
        var session = await harness.SignInAsync(phone, "handset");

        // 0771234567 rather than +94771234567 — what a booker types.
        var local = "0" + phone[3..];

        var body = await IamHarness.ReadJsonAsync(
            await harness.GetInternalAsync($"/v1/users/lookup?phone={Uri.EscapeDataString(local)}"));

        Assert.True(body.GetProperty("registered").GetBoolean());
        Assert.Equal(session.UserId, body.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task A_number_that_is_not_a_Sri_Lankan_mobile_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        // The whole suite shares one database, so the assertion is on the delta, not the total.
        var before = (await harness.Seed.PhoneLookupsAsync()).Count;

        var response = await harness.GetInternalAsync("/v1/users/lookup?phone=%2B441234567890");

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "invalid-phone");
        Assert.Equal(before, (await harness.Seed.PhoneLookupsAsync()).Count);
    }

    /// <summary>
    /// The route is not under the <c>/v1/internal/**</c> prefix the gateway refuses, and the
    /// <c>iam-users</c> gateway route forwards it from the public internet — so the service has to
    /// authenticate it itself or it is a registration oracle anybody can query.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("the-wrong-key")]
    public async Task Without_the_internal_secret_the_lookup_is_refused(string? apiKey)
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();
        await harness.SignInAsync(phone, "handset");

        var response = await harness.GetInternalAsync(
            $"/v1/users/lookup?phone={Uri.EscapeDataString(phone)}", apiKey);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Unauthorized, "unauthorized");

        // Refused before the number was even normalised, so nothing about it was recorded.
        Assert.DoesNotContain(
            await harness.Seed.PhoneLookupsAsync(), row => row.PhoneHash.SequenceEqual(ExpectedHash(phone)));
    }

    /// <summary>A user's own bearer token is not a credential for a service-to-service route.</summary>
    [Fact]
    public async Task A_users_own_token_does_not_open_the_lookup()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();
        var session = await harness.SignInAsync(phone, "handset");

        var response = await harness.GetAsync(
            $"/v1/users/lookup?phone={Uri.EscapeDataString(phone)}", session.AccessToken);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    /// <summary>
    /// Unset <c>Auth:InternalApiKey</c> means the route is not mapped at all — a deployment that
    /// forgets it gets 404s, not an open door.
    /// </summary>
    /// <remarks>
    /// Asserted with a signed-in user's token rather than anonymously: the kernel's deny-by-default
    /// fallback policy also applies to requests that match no endpoint, so an anonymous caller sees
    /// <c>401</c> for a route that does not exist and for one that does. A token gets past the
    /// fallback and leaves routing to answer, which is the thing under test.
    /// </remarks>
    [Fact]
    public async Task Without_a_configured_secret_the_route_does_not_exist()
    {
        await using var harness = await IamHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Auth:InternalApiKey"] = string.Empty });

        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.GetAsync("/v1/users/lookup?phone=%2B94771234567", session.AccessToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A blocked account is still a registered one. Answering false would push a proxy rider down
    /// the unregistered SMS path and disclose the account's standing to a caller with no business
    /// knowing it.
    /// </summary>
    [Fact]
    public async Task A_blocked_account_still_reads_as_registered()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();
        var session = await harness.SignInAsync(phone, "handset");

        await harness.Seed.BlockAsync(Guid.Parse(session.UserId));

        var body = await IamHarness.ReadJsonAsync(
            await harness.GetInternalAsync($"/v1/users/lookup?phone={Uri.EscapeDataString(phone)}"));

        Assert.True(body.GetProperty("registered").GetBoolean());
    }
}
