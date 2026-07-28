using System.Net;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// <c>/v1/me/emergency-contacts</c> — the SOS list (AL-13) and the denormalised primary
/// <c>POST /v1/sos</c> reads inside D-33's five-second budget.
/// </summary>
[Collection<IamCollection>]
public sealed class EmergencyContactTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task The_first_contact_becomes_the_primary_and_lands_on_iam_users()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset", "driver");

        var created = await harness.PostAsync(
            "/v1/me/emergency-contacts", new { name = "Amma", phone = "+94771234567" }, bearer: session.AccessToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await IamHarness.ReadJsonAsync(created);
        Assert.True(body.GetProperty("isPrimary").GetBoolean());

        var (name, phone) = await harness.Seed.PrimaryEmergencyContactAsync(Guid.Parse(session.UserId));
        Assert.Equal("Amma", name);
        Assert.Equal("+94771234567", phone);
    }

    /// <summary>
    /// An SOS dials this number, so the local spelling has to become E.164 here — not at three in
    /// the morning in safety-svc.
    /// </summary>
    [Fact]
    public async Task A_local_spelling_is_normalised_to_E164()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset", "driver");

        var body = await IamHarness.ReadJsonAsync(await harness.PostAsync(
            "/v1/me/emergency-contacts", new { name = "Thaththa", phone = "077 765 4321" }, bearer: session.AccessToken));

        Assert.Equal("+94777654321", body.GetProperty("phone").GetString());
    }

    [Fact]
    public async Task A_number_that_is_not_a_Sri_Lankan_mobile_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset", "driver");

        var response = await harness.PostAsync(
            "/v1/me/emergency-contacts", new { name = "Someone", phone = "+441234567890" }, bearer: session.AccessToken);

        var problem = await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.True(problem.Root.GetProperty("errors").TryGetProperty("phone", out _));
    }

    [Fact]
    public async Task Deleting_the_primary_promotes_the_next_one()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset", "driver");

        var first = await IamHarness.ReadJsonAsync(await harness.PostAsync(
            "/v1/me/emergency-contacts", new { name = "Amma", phone = "+94771111111" }, bearer: session.AccessToken));
        await harness.PostAsync(
            "/v1/me/emergency-contacts", new { name = "Nangi", phone = "+94772222222" }, bearer: session.AccessToken);

        var deleted = await harness.DeleteAsync(
            $"/v1/me/emergency-contacts/{first.GetProperty("contactId").GetString()}", session.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var items = (await IamHarness.ReadJsonAsync(
                await harness.GetAsync("/v1/me/emergency-contacts", session.AccessToken)))
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();

        Assert.Single(items);
        Assert.True(items[0].GetProperty("isPrimary").GetBoolean());

        var (name, phone) = await harness.Seed.PrimaryEmergencyContactAsync(Guid.Parse(session.UserId));
        Assert.Equal("Nangi", name);
        Assert.Equal("+94772222222", phone);
    }

    /// <summary>
    /// Removing everybody puts <c>POST /v1/sos</c> back to <c>400 no-emergency-contact</c>, which
    /// is the correct state for a driver who has cleared the list.
    /// </summary>
    [Fact]
    public async Task Deleting_the_last_contact_clears_the_denormalised_columns()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset", "driver");

        var only = await IamHarness.ReadJsonAsync(await harness.PostAsync(
            "/v1/me/emergency-contacts", new { name = "Amma", phone = "+94773333333" }, bearer: session.AccessToken));

        await harness.DeleteAsync(
            $"/v1/me/emergency-contacts/{only.GetProperty("contactId").GetString()}", session.AccessToken);

        var (name, phone) = await harness.Seed.PrimaryEmergencyContactAsync(Guid.Parse(session.UserId));
        Assert.Null(name);
        Assert.Null(phone);
    }

    [Fact]
    public async Task Editing_the_primary_rewrites_the_denormalised_copy()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset", "driver");

        var contact = await IamHarness.ReadJsonAsync(await harness.PostAsync(
            "/v1/me/emergency-contacts", new { name = "Amma", phone = "+94774444444" }, bearer: session.AccessToken));

        var updated = await harness.PutAsync(
            $"/v1/me/emergency-contacts/{contact.GetProperty("contactId").GetString()}",
            new { name = "Amma (new phone)", phone = "+94775555555" },
            session.AccessToken);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.True((await IamHarness.ReadJsonAsync(updated)).GetProperty("isPrimary").GetBoolean());

        var (name, phone) = await harness.Seed.PrimaryEmergencyContactAsync(Guid.Parse(session.UserId));
        Assert.Equal("Amma (new phone)", name);
        Assert.Equal("+94775555555", phone);
    }

    /// <summary>Two rows with one number means safety-svc SMSes the same person twice on an SOS.</summary>
    [Fact]
    public async Task The_same_number_cannot_be_saved_twice()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset", "driver");

        await harness.PostAsync(
            "/v1/me/emergency-contacts", new { name = "Amma", phone = "+94776666666" }, bearer: session.AccessToken);

        var duplicate = await harness.PostAsync(
            "/v1/me/emergency-contacts", new { name = "Amma again", phone = "077 666 6666" }, bearer: session.AccessToken);

        await ProblemDocument.AssertAsync(duplicate, HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task The_list_is_capped_at_the_SOS_fan_out_budget()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset", "driver");

        for (var i = 0; i < 5; i++)
        {
            var response = await harness.PostAsync(
                "/v1/me/emergency-contacts",
                new { name = $"Contact {i}", phone = $"+9477000000{i}" },
                bearer: session.AccessToken);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var sixth = await harness.PostAsync(
            "/v1/me/emergency-contacts", new { name = "One too many", phone = "+94770000009" }, bearer: session.AccessToken);

        await ProblemDocument.AssertAsync(sixth, HttpStatusCode.Conflict, "conflict");
    }

    /// <summary>
    /// A passenger's SOS is US-12.9 too — gating the list on the driver role would leave them with
    /// nobody to call.
    /// </summary>
    [Fact]
    public async Task A_passenger_may_keep_SOS_contacts_too()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var created = await harness.PostAsync(
            "/v1/me/emergency-contacts", new { name = "Aiya", phone = "+94778888888" }, bearer: session.AccessToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Another_users_contact_is_not_found()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var mine = await harness.SignInAsync(IamHarness.NextPhone(), "handset-a", "driver");
        var theirs = await harness.SignInAsync(IamHarness.NextPhone(), "handset-b", "driver");

        var contact = await IamHarness.ReadJsonAsync(await harness.PostAsync(
            "/v1/me/emergency-contacts", new { name = "Amma", phone = "+94779999999" }, bearer: mine.AccessToken));
        var path = $"/v1/me/emergency-contacts/{contact.GetProperty("contactId").GetString()}";

        await ProblemDocument.AssertAsync(
            await harness.DeleteAsync(path, theirs.AccessToken), HttpStatusCode.NotFound, "not-found");
        await ProblemDocument.AssertAsync(
            await harness.PutAsync(path, new { name = "Theirs", phone = "+94770000001" }, theirs.AccessToken),
            HttpStatusCode.NotFound,
            "not-found");
    }
}
