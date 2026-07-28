using System.Net;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// <c>/v1/me/saved-addresses</c> — Home, Work and the labelled places of SCR-PA/PI-026
/// (AL-14, AL-26, US-22.1/22.2).
/// </summary>
[Collection<IamCollection>]
public sealed class SavedAddressTests(PostgresFixture postgres, RedisFixture redis)
{
    private static object Body(
        string label, bool isHome = false, bool isWork = false, double lat = 6.9271, double lng = 79.8612) =>
        new
        {
            label,
            line1 = "42 Galle Road",
            line2 = "Kollupitiya",
            line3 = "Colombo 03",
            lat,
            lng,
            isHome,
            isWork,
        };

    [Fact]
    public async Task An_address_round_trips_with_all_three_lines_and_its_pin()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var created = await harness.PostAsync("/v1/me/saved-addresses", Body("Ammage gedara"), bearer: session.AccessToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await IamHarness.ReadJsonAsync(created);
        Assert.Equal("Ammage gedara", body.GetProperty("label").GetString());
        Assert.Equal("42 Galle Road", body.GetProperty("line1").GetString());
        Assert.Equal("Kollupitiya", body.GetProperty("line2").GetString());
        Assert.Equal("Colombo 03", body.GetProperty("line3").GetString());
        Assert.Equal(6.9271, body.GetProperty("lat").GetDouble(), 6);
        Assert.Equal(79.8612, body.GetProperty("lng").GetDouble(), 6);

        var list = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/me/saved-addresses", session.AccessToken));
        Assert.Equal(1, list.GetProperty("items").GetArrayLength());
    }

    /// <summary>
    /// The at-most-one-Home invariant, which only the <c>is_home</c>/<c>is_work</c> half of the
    /// C003 union can express (uq_saved_home / uq_saved_work).
    /// </summary>
    [Fact]
    public async Task Claiming_home_moves_it_rather_than_creating_a_second_one()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var first = await IamHarness.ReadJsonAsync(
            await harness.PostAsync("/v1/me/saved-addresses", Body("home", isHome: true), bearer: session.AccessToken));

        var second = await harness.PostAsync(
            "/v1/me/saved-addresses", Body("New place", isHome: true, lat: 7.2906, lng: 80.6337), bearer: session.AccessToken);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var items = (await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/me/saved-addresses", session.AccessToken)))
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(2, items.Length);
        Assert.Single(items, item => item.GetProperty("isHome").GetBoolean());
        Assert.False(
            items.Single(item => item.GetProperty("addressId").GetString() == first.GetProperty("addressId").GetString())
                .GetProperty("isHome").GetBoolean());
    }

    [Fact]
    public async Task Editing_an_address_can_move_the_work_flag_onto_it()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        await harness.PostAsync("/v1/me/saved-addresses", Body("work", isWork: true), bearer: session.AccessToken);
        var other = await IamHarness.ReadJsonAsync(
            await harness.PostAsync("/v1/me/saved-addresses", Body("Gym"), bearer: session.AccessToken));

        var moved = await harness.PutAsync(
            $"/v1/me/saved-addresses/{other.GetProperty("addressId").GetString()}",
            Body("Gym", isWork: true),
            session.AccessToken);

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var items = (await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/me/saved-addresses", session.AccessToken)))
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();

        Assert.Single(items, item => item.GetProperty("isWork").GetBoolean());
    }

    /// <summary>
    /// The label and the booleans are two spellings of one fact (C003 note (c)); a body that sets
    /// one adopts the other.
    /// </summary>
    [Fact]
    public async Task A_reserved_label_and_its_flag_stay_in_step()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var byLabel = await IamHarness.ReadJsonAsync(
            await harness.PostAsync("/v1/me/saved-addresses", Body("Home"), bearer: session.AccessToken));
        Assert.True(byLabel.GetProperty("isHome").GetBoolean());

        var byFlag = await IamHarness.ReadJsonAsync(
            await harness.PostAsync("/v1/me/saved-addresses", Body("Office", isWork: true), bearer: session.AccessToken));
        Assert.Equal("work", byFlag.GetProperty("label").GetString());
    }

    [Fact]
    public async Task A_label_and_a_flag_that_disagree_are_refused_rather_than_guessed_at()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.PostAsync(
            "/v1/me/saved-addresses", Body("work", isHome: true), bearer: session.AccessToken);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task An_address_cannot_be_both_home_and_work()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.PostAsync(
            "/v1/me/saved-addresses", Body("Both", isHome: true, isWork: true), bearer: session.AccessToken);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Theory]
    [InlineData(null, "42 Galle Road")]
    [InlineData("Label", null)]
    public async Task Label_and_line1_are_both_required(string? label, string? line1)
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.PostAsync(
            "/v1/me/saved-addresses",
            new { label, line1, lat = 6.9271, lng = 79.8612 },
            bearer: session.AccessToken);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task A_pin_outside_the_world_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.PostAsync(
            "/v1/me/saved-addresses", Body("Nowhere", lat: 99), bearer: session.AccessToken);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task An_address_is_deleted_and_then_unknown()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var created = await IamHarness.ReadJsonAsync(
            await harness.PostAsync("/v1/me/saved-addresses", Body("Gym"), bearer: session.AccessToken));
        var path = $"/v1/me/saved-addresses/{created.GetProperty("addressId").GetString()}";

        Assert.Equal(HttpStatusCode.NoContent, (await harness.DeleteAsync(path, session.AccessToken)).StatusCode);
        await ProblemDocument.AssertAsync(
            await harness.DeleteAsync(path, session.AccessToken), HttpStatusCode.NotFound, "not-found");
    }

    /// <summary>
    /// One user's address id is not a handle another user can reach. 404 rather than 403: a
    /// different answer would confirm the id exists.
    /// </summary>
    [Fact]
    public async Task Another_users_address_is_not_found_rather_than_forbidden()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var mine = await harness.SignInAsync(IamHarness.NextPhone(), "handset-a");
        var theirs = await harness.SignInAsync(IamHarness.NextPhone(), "handset-b");

        var created = await IamHarness.ReadJsonAsync(
            await harness.PostAsync("/v1/me/saved-addresses", Body("Gym"), bearer: mine.AccessToken));
        var path = $"/v1/me/saved-addresses/{created.GetProperty("addressId").GetString()}";

        // The second account sees an empty list of its own, then cannot touch the first's row.
        var theirList = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/me/saved-addresses", theirs.AccessToken));
        Assert.Equal(0, theirList.GetProperty("items").GetArrayLength());

        await ProblemDocument.AssertAsync(
            await harness.DeleteAsync(path, theirs.AccessToken), HttpStatusCode.NotFound, "not-found");

        await ProblemDocument.AssertAsync(
            await harness.PutAsync(path, Body("Stolen"), theirs.AccessToken), HttpStatusCode.NotFound, "not-found");

        // And the owner still has it.
        var stillThere = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/me/saved-addresses", mine.AccessToken));
        Assert.Equal(1, stillThere.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_malformed_address_id_is_not_found()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        await ProblemDocument.AssertAsync(
            await harness.DeleteAsync("/v1/me/saved-addresses/not-an-id", session.AccessToken),
            HttpStatusCode.NotFound,
            "not-found");
    }

    [Fact]
    public async Task The_list_puts_home_and_work_first()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        await harness.PostAsync("/v1/me/saved-addresses", Body("Gym"), bearer: session.AccessToken);
        await harness.PostAsync("/v1/me/saved-addresses", Body("work", isWork: true), bearer: session.AccessToken);
        await harness.PostAsync("/v1/me/saved-addresses", Body("home", isHome: true), bearer: session.AccessToken);

        var items = (await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/me/saved-addresses", session.AccessToken)))
            .GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal(["home", "work", "Gym"], items);
    }
}
