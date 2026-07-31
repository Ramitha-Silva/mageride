using System.Diagnostics;
using System.Net;
using MageRide.Safety.Domain;
using MageRide.Safety.Endpoints;
using MageRide.Safety.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Safety.Tests.Integration;

/// <summary>
/// D-33 / AL-13: the panic button, through two gateways at once, to the contact on file.
/// </summary>
/// <remarks>
/// <b>Against a real notification-svc.</b> Every SMS on the platform is C051's, so "reaches both
/// gateways in parallel" is a property of the two services together and a stubbed
/// <c>INotificationClient</c> would prove only that this one calls what it calls. The gateways are
/// real sockets for the same reason.
/// </remarks>
[Collection(SafetyCollection.Name)]
public sealed class SosTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>D5' §14.3 / D-33.</summary>
    private static readonly TimeSpan Slo = TimeSpan.FromSeconds(5);

    /// <summary>The first definition of done, in one test.</summary>
    [Fact]
    public async Task An_sos_reaches_both_gateways_in_parallel_and_the_row_records_each()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync(emergencyContactPhone: "+94770000001");
        var rideId = await harness.Seed.RideAsync(passenger.Id);

        // The primary is having a bad minute. A sequential fallback would spend two seconds on every
        // alert; the parallel send resolves on the secondary.
        harness.PrimarySms.Delay = TimeSpan.FromSeconds(2);

        var stopwatch = Stopwatch.StartNew();

        using var response = await harness.PostAsync(
            "/v1/sos",
            new { rideId, lat = 6.9271, lng = 79.8612, role = "passenger" },
            harness.Tokens.Passenger(passenger.Id));

        stopwatch.Stop();

        var raised = await SafetyHarness.OkAsync<RaiseSosResponse>(response, "POST /v1/sos");

        Assert.Equal(SosSmsStatuses.Dispatched, raised.SmsStatus);
        Assert.NotNull(raised.DispatchedAt);

        Assert.True(
            stopwatch.Elapsed < Slo,
            $"D-33 budgets {Slo.TotalSeconds} s from button tap to dispatch; this one took {stopwatch.ElapsedMilliseconds} ms.");

        // The row keeps one column per gateway: both were handed the message, and the one that
        // answered first is marked delivered. "Tried" and "delivered" are different facts.
        var row = await harness.SosAsync(raised.SosId);

        Assert.NotNull(row);
        Assert.Equal(SosSmsStatuses.Dispatched, row!.SmsStatus);
        Assert.Equal("+94770000001", row.EmergencyContact);
        Assert.Equal(SosSources.App, row.Source);
        Assert.NotNull(row.DispatchedAt);

        Assert.Equal("notifylk:attempted", row.PrimaryGateway);
        Assert.Equal("secondary:delivered", row.SecondaryGateway);

        // The measured SLO is on the row, so it survives the request and can be queried after an
        // incident rather than reconstructed from logs.
        Assert.True(row.DispatchedAt! - row.Ts < Slo);

        // The secondary answered first and the primary still received it — the half a percentile
        // alone would not show.
        Assert.Single(harness.SecondarySms.Sent);
        await WaitForAsync(() => harness.PrimarySms.Sent.Count == 1, TimeSpan.FromSeconds(10));
        Assert.Single(harness.PrimarySms.Sent);

        // Rendered by content-svc, carrying the raiser's name and a live tracking link.
        var sms = harness.SecondarySms.Sent[0];

        // The secondary gateway takes E.164 verbatim; only Notify.lk wants the national form.
        Assert.Equal("+94770000001", sms.To);
        Assert.Equal("94770000001", harness.PrimarySms.Sent[0].To);
        Assert.Contains("Nimal", sms.Message, StringComparison.Ordinal);
        Assert.Contains(SafetyHarness.ShareBaseUrl, sms.Message, StringComparison.Ordinal);
    }

    /// <summary>The second definition of done: AL-13's contact, read off the account.</summary>
    [Fact]
    public async Task A_driver_sos_goes_to_the_drivers_own_emergency_contact()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.UserAsync(
            role: "driver", emergencyContactPhone: "+94770000042", emergencyContactName: "Sunil");

        // A passenger with a different contact, to show the alert is addressed by *raiser* and not
        // by whoever else is on the ride.
        var passenger = await harness.Seed.UserAsync(emergencyContactPhone: "+94770000099");
        var rideId = await harness.Seed.RideAsync(passenger.Id, driver.Id);

        using var response = await harness.PostAsync(
            "/v1/sos",
            new { rideId, lat = 6.9271, lng = 79.8612, role = "driver" },
            harness.Tokens.Driver(driver.Id));

        var raised = await SafetyHarness.OkAsync<RaiseSosResponse>(response, "POST /v1/sos");

        Assert.Equal(SosSmsStatuses.Dispatched, raised.SmsStatus);

        var sms = Assert.Single(harness.SecondarySms.Sent);

        // Addressed to the driver's own contact, and naming the *driver* — "{{name}} has raised an
        // SOS" is about the raiser, and rendering Sunil's name there would tell Sunil that they had
        // raised it themselves.
        Assert.Equal("+94770000042", sms.To);
        Assert.Contains("Nimal", sms.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Sunil", sms.Message, StringComparison.Ordinal);

        var row = await harness.SosAsync(raised.SosId);

        Assert.Equal(SosRoles.Driver, row!.Role);
        Assert.Equal("+94770000042", row.EmergencyContact);
    }

    /// <summary>D3': `400 no-emergency-contact`, before anything is written.</summary>
    [Fact]
    public async Task An_sos_from_somebody_with_no_contact_is_refused_rather_than_silently_lost()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();

        using var response = await harness.PostAsync(
            "/v1/sos",
            new { lat = 6.9271, lng = 79.8612, role = "passenger" },
            harness.Tokens.Passenger(passenger.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, _) = await SafetyHarness.ProblemAsync(response);
        Assert.Equal("no-emergency-contact", code);

        // Nothing written: the user is told to add a contact while the alert still matters.
        Assert.Empty(await harness.OutboxAsync());
        Assert.Empty(harness.AllSms);
    }

    /// <summary>
    /// The admin live feed commits with the event, before any gateway is tried — an operator learns
    /// about an SOS whether or not an SMS went out, which is the case a human is most needed for.
    /// </summary>
    [Fact]
    public async Task An_sos_that_reaches_no_gateway_is_still_recorded_and_still_announced()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync(emergencyContactPhone: "+94770000001");

        harness.PrimarySms.Refuse = true;
        harness.SecondarySms.Refuse = true;

        using var response = await harness.PostAsync(
            "/v1/sos",
            new { lat = 6.9271, lng = 79.8612, role = "passenger" },
            harness.Tokens.Passenger(passenger.Id));

        var raised = await SafetyHarness.OkAsync<RaiseSosResponse>(response, "POST /v1/sos");

        // Answered honestly: no dispatch instant, and a status the SOS screen can draw.
        Assert.Equal(SosSmsStatuses.Failed, raised.SmsStatus);
        Assert.Null(raised.DispatchedAt);

        var row = await harness.SosAsync(raised.SosId);

        Assert.Equal(SosSmsStatuses.Failed, row!.SmsStatus);
        Assert.Null(row.DispatchedAt);

        var (eventType, payload) = Assert.Single(await harness.OutboxAsync());

        Assert.Equal(SafetyEventTypes.SosRaised, eventType);
        Assert.Contains(raised.SosId.ToString(), payload, StringComparison.Ordinal);

        // The emergency contact's number is deliberately NOT on the event (§0 PII): the console
        // reads it from the user's own record.
        Assert.DoesNotContain("+94770000001", payload, StringComparison.Ordinal);
        Assert.Contains("Kamala", payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// R-14, and the one place it matters most: the first thing somebody does when nothing appears
    /// to happen is press the button again.
    /// </summary>
    [Fact]
    public async Task A_double_tapped_panic_button_under_one_key_sends_one_message()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync(emergencyContactPhone: "+94770000001");
        var key = Guid.NewGuid().ToString();

        async Task<HttpResponseMessage> TapAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/sos")
            {
                Content = System.Net.Http.Json.JsonContent.Create(
                    new { lat = 6.9271, lng = 79.8612, role = "passenger" },
                    options: MageRide.Shared.Http.MageRideJson.Options),
            };

            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", harness.Tokens.Passenger(passenger.Id));

            request.Headers.TryAddWithoutValidation(MageRide.Shared.Http.MageRideHeaders.IdempotencyKey, key);

            return await harness.Client.SendAsync(request);
        }

        var first = await SafetyHarness.OkAsync<RaiseSosResponse>(await TapAsync(), "first tap");
        var second = await SafetyHarness.OkAsync<RaiseSosResponse>(await TapAsync(), "second tap");

        Assert.Equal(first.SosId, second.SosId);
        Assert.Single(harness.SecondarySms.Sent);

        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            1,
            await Dapper.SqlMapper.ExecuteScalarAsync<int>(
                connection, "SELECT count(*)::int FROM safety.sos_events;"));
    }

    /// <summary>An SOS history is readable only by its own subject.</summary>
    [Fact]
    public async Task One_user_cannot_read_another_users_sos_history()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var owner = await harness.Seed.UserAsync(emergencyContactPhone: "+94770000001");
        var stranger = await harness.Seed.UserAsync();

        using (var raised = await harness.PostAsync(
                   "/v1/sos",
                   new { lat = 6.9271, lng = 79.8612, role = "passenger" },
                   harness.Tokens.Passenger(owner.Id)))
        {
            Assert.Equal(HttpStatusCode.OK, raised.StatusCode);
        }

        using (var own = await harness.GetAsync($"/v1/sos/{owner.Id}/history", harness.Tokens.Passenger(owner.Id)))
        {
            var page = await SafetyHarness.OkAsync<CursorPageResponse<SosEventResponse>>(own, "own history");
            Assert.Single(page.Items);
        }

        using var forbidden = await harness.GetAsync(
            $"/v1/sos/{owner.Id}/history", harness.Tokens.Passenger(stranger.Id));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    /// <summary>
    /// With notification-svc unreachable the alert is still recorded and still announced, and the
    /// caller is told it did not go out — never that it did.
    /// </summary>
    [Fact]
    public async Task An_sos_with_no_notification_service_is_recorded_and_reported_undispatched()
    {
        await using var harness = await SafetyHarness.StartAsync(
            postgres, redis, withNotificationService: false);

        var passenger = await harness.Seed.UserAsync(emergencyContactPhone: "+94770000001");

        using var response = await harness.PostAsync(
            "/v1/sos",
            new { lat = 6.9271, lng = 79.8612, role = "passenger" },
            harness.Tokens.Passenger(passenger.Id));

        var raised = await SafetyHarness.OkAsync<RaiseSosResponse>(response, "POST /v1/sos");

        Assert.Equal(SosSmsStatuses.Failed, raised.SmsStatus);
        Assert.Null(raised.DispatchedAt);
        Assert.NotNull(await harness.SosAsync(raised.SosId));
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan budget)
    {
        var deadline = DateTimeOffset.UtcNow + budget;

        while (DateTimeOffset.UtcNow < deadline && !condition())
        {
            await Task.Delay(50);
        }
    }
}
