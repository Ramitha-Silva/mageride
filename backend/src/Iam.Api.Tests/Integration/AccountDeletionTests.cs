using System.Net;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// <c>DELETE /v1/users/me</c> — accepted, recorded, not performed (US-1.8, E-06).
/// </summary>
/// <remarks>
/// The C027 fence: PDPA fulfilment lives in admin-bff (C065); iam only records the request.
/// </remarks>
[Collection<IamCollection>]
public sealed class AccountDeletionTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task A_deletion_request_is_accepted_and_becomes_a_pdpa_row()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.DeleteAsync("/v1/users/me", session.AccessToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var requestId = (await IamHarness.ReadJsonAsync(response)).GetProperty("requestId").GetString();

        var rows = await harness.Seed.PdpaRequestsAsync(Guid.Parse(session.UserId));
        var row = Assert.Single(rows);

        Assert.Equal(requestId, row.Id.ToString());
        Assert.Equal("erasure", row.Kind);
        Assert.Equal("Received", row.Status);

        // The 30-day statutory clock is the column's own default, not something iam computes.
        Assert.InRange(row.DueBy, DateTimeOffset.UtcNow.AddDays(29), DateTimeOffset.UtcNow.AddDays(31));
    }

    /// <summary>
    /// Two open erasure requests for one person are two 30-day clocks against one obligation:
    /// whichever C065 fulfils leaves the other permanently overdue in the SLA queue.
    /// </summary>
    [Fact]
    public async Task A_second_request_while_one_is_open_is_a_conflict()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        await harness.DeleteAsync("/v1/users/me", session.AccessToken);

        var second = await harness.DeleteAsync("/v1/users/me", session.AccessToken);
        await ProblemDocument.AssertAsync(second, HttpStatusCode.Conflict, "conflict");

        Assert.Single(await harness.Seed.PdpaRequestsAsync(Guid.Parse(session.UserId)));
    }

    /// <summary>
    /// Erasure may be rejected or held (<c>FulfilledHold</c>), so a user whose request is refused
    /// must find their account exactly as they left it. iam changes nothing.
    /// </summary>
    [Fact]
    public async Task Requesting_erasure_does_not_touch_the_account_or_its_session()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        await harness.PutAsync("/v1/users/me", new { firstName = "Kamala" }, session.AccessToken);
        await harness.DeleteAsync("/v1/users/me", session.AccessToken);

        var profile = await harness.GetAsync("/v1/users/me", session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);

        var body = await IamHarness.ReadJsonAsync(profile);
        Assert.Equal("Kamala", body.GetProperty("firstName").GetString());

        // The session is still good, too — the token was not revoked.
        var refreshed = await harness.PostAsync("/v1/auth/refresh", new { refreshToken = session.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
    }

    [Fact]
    public async Task Deleting_an_account_needs_a_token()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        await ProblemDocument.AssertAsync(
            await harness.DeleteAsync("/v1/users/me"), HttpStatusCode.Unauthorized, "unauthorized");
    }
}
