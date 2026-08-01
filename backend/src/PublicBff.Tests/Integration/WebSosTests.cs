using MageRide.PublicBff.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.PublicBff.Tests.Integration;

/// <summary>
/// US-25.5 / D-33: the alert is recorded as <c>source='web'</c> against its token, and the SMS goes
/// to the booker.
/// </summary>
/// <remarks>
/// <b>Driven through a real safety-svc.</b> The row, the <c>sos.raised</c> outbox event that puts it
/// on the admin live feed and the booker lookup are all facts about that service's transaction; only
/// the notification hop is a stub, and only so the test can read back which number the alert was
/// aimed at — a number public-bff never sees.
/// </remarks>
[Collection<PublicBffCollection>]
public sealed class WebSosTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task A_web_sos_is_recorded_against_its_token_and_SMSed_to_the_booker()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 1);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "proxy_rider", harness.Now.AddHours(2));

        var response = await harness.PostAsync(
            $"/public/track/{token}/sos", new { lat = 6.9271, lng = 79.8612, accuracy = 20.0 });

        Assert.Equal(202, (int)response.StatusCode);

        var body = await PublicBffHarness.OkAsync(response, "the web SOS");

        Assert.NotEqual(Guid.Empty, body.GetProperty("sosId").GetGuid());
        Assert.Equal("Dispatched", body.GetProperty("smsStatus").GetString());

        var events = await harness.SosEventsAsync();
        var raised = Assert.Single(events);

        // `ck_sos_events_actor` demands a user id or a token, and a web guest has only the token.
        Assert.Null(raised.UserId);
        Assert.Equal("web", raised.Source);
        Assert.Equal(token, raised.ShareToken);
        Assert.Equal(ride.BookerPhone, raised.Contact);

        // The browser's Geolocation API supplies the position, and the row records where the
        // *person* said they were rather than where the car was.
        Assert.Equal(6.9271, raised.Lat, 4);
        Assert.Equal(79.8612, raised.Lng, 4);

        // D6' I-29.4: the recipient is the booker's registered mobile.
        var alert = Assert.Single(harness.Notifications.Sent);
        Assert.Equal("SOS_TRIGGERED", alert.Type);
        Assert.Equal([ride.BookerPhone], alert.Phones);

        // "{{name}} has raised an SOS" — the raiser is the person on the page, not the account
        // holder being told about it.
        Assert.Equal("Tharindu", alert.Data["name"]);

        // R-13: the admin live feed commits with the row.
        Assert.Contains("sos.raised", await harness.SafetyOutboxAsync());
    }

    [Fact]
    public async Task A_package_recipients_alert_names_the_recipient_and_reaches_the_sender()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 2, recipientName: "Nimali");
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(4));

        await PublicBffHarness.OkAsync(
            await harness.PostAsync($"/public/track/{token}/sos", new { lat = 6.9, lng = 79.8 }),
            "the recipient's SOS");

        var alert = Assert.Single(harness.Notifications.Sent);

        Assert.Equal([ride.BookerPhone], alert.Phones);
        Assert.Equal("Nimali", alert.Data["name"]);
    }

    [Fact]
    public async Task The_bookers_number_never_appears_in_a_public_response()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 1);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "proxy_rider", harness.Now.AddHours(2));

        var sos = await harness.PostAsync($"/public/track/{token}/sos", new { lat = 6.9, lng = 79.8 });
        var sosBody = await sos.Content.ReadAsStringAsync();
        sos.Dispose();

        using var snapshot = await harness.GetAsync($"/public/track/{token}");
        var snapshotBody = await snapshot.Content.ReadAsStringAsync();

        // P-02/P-09 held by where the column is read: safety-svc resolves the booker, uses the
        // number and returns an id and an outcome. There is no field on any type in public-bff that
        // could carry it.
        Assert.DoesNotContain(ride.BookerPhone, sosBody, StringComparison.Ordinal);
        Assert.DoesNotContain(ride.BookerPhone, snapshotBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_double_tapped_button_sends_one_message()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 1);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "proxy_rider", harness.Now.AddHours(2));

        // No `Idempotency-Key` from the page, which is the case a derived key exists for: the first
        // thing somebody does when nothing appears to happen is press it again.
        var first = await harness.PostAsync($"/public/track/{token}/sos", new { lat = 6.9, lng = 79.8 });
        var second = await harness.PostAsync($"/public/track/{token}/sos", new { lat = 6.9, lng = 79.8 });

        Assert.Equal(202, (int)first.StatusCode);
        Assert.Equal(202, (int)second.StatusCode);

        first.Dispose();
        second.Dispose();

        Assert.Single(await harness.SosEventsAsync());
        Assert.Single(harness.Notifications.Sent);
    }

    [Fact]
    public async Task An_alert_nobody_could_send_is_still_recorded_and_still_announced()
    {
        await using var harness = await StartAsync();

        harness.Notifications.Refuse = true;

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 1);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "proxy_rider", harness.Now.AddHours(2));

        var body = await PublicBffHarness.OkAsync(
            await harness.PostAsync($"/public/track/{token}/sos", new { lat = 6.9, lng = 79.8 }),
            "a web SOS no gateway took");

        // The status is the honest half of the answer — without it a caller cannot tell "the alert
        // went out" from "the alert is on the admin console and nowhere else", and on this surface
        // that is the difference between somebody having been told and nobody having been.
        Assert.Equal("Failed", body.GetProperty("smsStatus").GetString());

        Assert.Single(await harness.SosEventsAsync());
        Assert.Contains("sos.raised", await harness.SafetyOutboxAsync());
    }

    [Fact]
    public async Task A_pickup_confirm_token_cannot_raise_an_alert()
    {
        await using var harness = await StartAsync();

        var (token, _, _, _) = await harness.Seed.PickupRequestAsync(
            issuedAt: harness.Now.AddSeconds(-30));

        var (status, _, _) = await PublicBffHarness.ProblemAsync(
            await harness.PostAsync($"/public/track/{token}/sos", new { lat = 6.9, lng = 79.8 }));

        // There is no ride, no booker and nobody in a vehicle — and SCR-WT-003 has no SOS button,
        // because the person reading it has not been picked up by anybody yet. safety-svc refuses
        // it against the table it owns, which is what makes the refusal structural.
        Assert.Equal(410, status);
        Assert.Empty(await harness.SosEventsAsync());
    }

    [Fact]
    public async Task An_unconfigured_safety_svc_refuses_rather_than_pretending()
    {
        await using var harness = await PublicBffHarness.StartAsync(
            postgres, redis, withSafetyService: false);

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 1);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "proxy_rider", harness.Now.AddHours(2));

        var (status, code, _) = await PublicBffHarness.ProblemAsync(
            await harness.PostAsync($"/public/track/{token}/sos", new { lat = 6.9, lng = 79.8 }));

        // An SOS that goes nowhere must not look like one that worked. 503, not 202.
        Assert.Equal(503, status);
        Assert.Equal("dependency-unavailable", code);
        Assert.Empty(await harness.SosEventsAsync());
    }

    [Fact]
    public async Task An_alert_with_no_position_is_refused()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 1);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "proxy_rider", harness.Now.AddHours(2));

        var (status, code, _) = await PublicBffHarness.ProblemAsync(
            await harness.PostAsync($"/public/track/{token}/sos", new { accuracy = 10.0 }));

        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);
    }

    private Task<PublicBffHarness> StartAsync() => PublicBffHarness.StartAsync(postgres, redis);
}
