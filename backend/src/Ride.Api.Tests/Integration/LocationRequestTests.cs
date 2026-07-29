using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// DoD item 2: "a declined location request stores the decision with no coordinates and falls back
/// to the booker pin" — and the rest of the P-02 / P-12 / P-13 / AL-45 round-trip.
/// </summary>
[Collection<RideCollection>]
public sealed class LocationRequestTests(PostgresFixture postgres)
{
    /// <summary>Colombo Fort, as a rider's phone would report it.</summary>
    private const double RiderLat = 6.9344;
    private const double RiderLng = 79.8428;

    [Fact]
    public async Task A_registered_rider_is_asked_in_app_and_the_confirmed_pin_reaches_the_booker()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();
        var rider = await harness.CreateUserAsync();

        var issued = await harness.IssueLocationRequestAsync(booker.Bearer, rider.Phone);
        Assert.Equal(HttpStatusCode.Accepted, issued.StatusCode);

        var body = await RideHarness.ReadJsonAsync(issued);
        var requestId = body.GetProperty("requestId").GetGuid();

        Assert.Equal("Pending", body.GetProperty("state").GetString());
        Assert.Equal(300, body.GetProperty("ttl").GetInt32());

        // The event notification-svc turns into the rider's FCM data message (ADD §11.15).
        var envelope = await harness.ReadEventPayloadAsync(requestId, "location.request.issued");
        var payload = envelope.GetProperty("payload");

        Assert.Equal(requestId, envelope.GetProperty("requestId").GetGuid());
        Assert.Equal(booker.Id, payload.GetProperty("bookerId").GetGuid());
        Assert.Equal(rider.Id, payload.GetProperty("riderId").GetGuid());
        Assert.Equal("Pending", payload.GetProperty("state").GetString());

        // ---- the rider answers ------------------------------------------------------------
        var confirmed = await harness.PostAsync(
            $"/v1/location-requests/{requestId}/confirm",
            new { lat = RiderLat, lng = RiderLng, accuracy = 12.5 },
            rider.Bearer);

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var resolved = await RideHarness.ReadJsonAsync(confirmed);

        Assert.Equal("Confirmed", resolved.GetProperty("state").GetString());
        Assert.Equal(RiderLat, resolved.GetProperty("geo").GetProperty("lat").GetDouble(), 6);
        Assert.Equal(RiderLng, resolved.GetProperty("geo").GetProperty("lng").GetDouble(), 6);

        // P-13: fanout-svc publishes this to `booker:{bookerId}:loc-req:{requestId}`, so both
        // halves of the group key have to be on the event.
        var pushed = (await harness.ReadEventPayloadAsync(requestId, "location.request.confirmed"))
            .GetProperty("payload");

        Assert.Equal(booker.Id, pushed.GetProperty("bookerId").GetGuid());
        Assert.Equal(RiderLat, pushed.GetProperty("geo").GetProperty("lat").GetDouble(), 6);

        var (state, lat, lng) = await harness.ReadLocationRequestAsync(requestId);
        Assert.Equal("Confirmed", state);
        Assert.Equal(RiderLat, lat!.Value, 6);
        Assert.Equal(RiderLng, lng!.Value, 6);

        Assert.Equal(["Confirmed"], await harness.ReadLocationAuditAsync(booker.Id));
    }

    /// <summary>
    /// DoD item 2. The decision is durable, the audit records it, and no coordinate exists anywhere
    /// — not on the row, not on the event, not in the response (P-02).
    /// </summary>
    [Fact]
    public async Task A_declined_request_stores_the_decision_and_transmits_no_coordinates()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();
        var rider = await harness.CreateUserAsync();

        var issued = await RideHarness.ReadJsonAsync(
            await harness.IssueLocationRequestAsync(booker.Bearer, rider.Phone));

        var requestId = issued.GetProperty("requestId").GetGuid();

        // The route takes no body at all — there is no parameter a coordinate could arrive in.
        var declined = await harness.PostAsync(
            $"/v1/location-requests/{requestId}/decline", body: null, rider.Bearer);

        Assert.Equal(HttpStatusCode.OK, declined.StatusCode);

        var resolved = await RideHarness.ReadJsonAsync(declined);
        Assert.Equal("Declined", resolved.GetProperty("state").GetString());

        // Absent, not null: the serializer omits nulls, so "no position" is a member that is not
        // there at all rather than one that says nothing.
        Assert.False(resolved.TryGetProperty("geo", out _));

        var (state, lat, lng) = await harness.ReadLocationRequestAsync(requestId);
        Assert.Equal("Declined", state);
        Assert.Null(lat);
        Assert.Null(lng);

        var envelope = await harness.ReadEventPayloadAsync(requestId, "location.request.declined");
        Assert.False(envelope.GetProperty("payload").TryGetProperty("geo", out _));

        // P-12: the decline is the outcome the abuse audit most wants.
        Assert.Equal(["Declined"], await harness.ReadLocationAuditAsync(booker.Id));

        // And the booker is left to set the pin themselves (US-8.19) — the request is terminal, so
        // a second answer to it changes nothing.
        var again = await harness.PostAsync(
            $"/v1/location-requests/{requestId}/confirm",
            new { lat = RiderLat, lng = RiderLng },
            rider.Bearer);

        await ProblemDocument.AssertAsync(again, HttpStatusCode.Gone, "token-expired-or-revoked");

        var (unchanged, stillNoLat, _) = await harness.ReadLocationRequestAsync(requestId);
        Assert.Equal("Declined", unchanged);
        Assert.Null(stillNoLat);
    }

    /// <summary>
    /// AL-45: an unregistered rider is answered <c>RiderNotRegistered</c> and handed to
    /// notification-svc, which mints the <c>pickup_confirm</c> token and SMSes the link. The number
    /// travels on the event because an SMS cannot be addressed to a digest; the row keeps the digest.
    /// </summary>
    [Fact]
    public async Task An_unregistered_rider_is_handed_to_the_sms_path_with_the_number_on_the_event()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();
        var phone = IamLookupStub.UnregisteredPhone();

        var issued = await harness.IssueLocationRequestAsync(booker.Bearer, phone);

        // Still a 202: AL-45 makes this a live request on another channel, not a failure.
        Assert.Equal(HttpStatusCode.Accepted, issued.StatusCode);

        var body = await RideHarness.ReadJsonAsync(issued);
        var requestId = body.GetProperty("requestId").GetGuid();

        Assert.Equal("RiderNotRegistered", body.GetProperty("state").GetString());

        var payload = (await harness.ReadEventPayloadAsync(requestId, "location.request.issued"))
            .GetProperty("payload");

        Assert.Equal("RiderNotRegistered", payload.GetProperty("state").GetString());
        Assert.Equal(phone, payload.GetProperty("riderPhone").GetString());
        Assert.False(payload.TryGetProperty("riderId", out _));

        // The stored subject is a digest, not the number.
        await using var connection = await harness.OpenAsync();
        var hash = await connection.QuerySingleAsync<byte[]>(
            "SELECT rider_phone_hash FROM rides.location_requests WHERE request_id = @RequestId;",
            new { RequestId = requestId });

        Assert.Equal(32, hash.Length);

        Assert.Equal(["NotRegistered"], await harness.ReadLocationAuditAsync(booker.Id));
    }

    /// <summary>
    /// AL-45's other half: public-bff burns the <c>pickup_confirm</c> token and drives the same
    /// state machine through the internal plane, because ride-svc owns the row.
    /// </summary>
    [Fact]
    public async Task The_web_path_answers_the_same_request_as_the_app_would()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();

        var issued = await RideHarness.ReadJsonAsync(
            await harness.IssueLocationRequestAsync(booker.Bearer, IamLookupStub.UnregisteredPhone()));

        var requestId = issued.GetProperty("requestId").GetGuid();

        var confirmed = await harness.PostInternalAsync(
            $"/v1/internal/location-requests/{requestId}/confirm",
            new { lat = RiderLat, lng = RiderLng, accuracy = 30.0 });

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("Confirmed", (await RideHarness.ReadJsonAsync(confirmed)).GetProperty("state").GetString());

        var (state, lat, _) = await harness.ReadLocationRequestAsync(requestId);
        Assert.Equal("Confirmed", state);
        Assert.Equal(RiderLat, lat!.Value, 6);

        // The booker's socket hears it on the same channel as the in-app path (P-13).
        Assert.Contains(
            "location.request.confirmed",
            await harness.ReadEventsAsync(requestId));
    }

    /// <summary>Without the internal key the AL-45 pair is indistinguishable from an unmapped route.</summary>
    [Fact]
    public async Task The_web_path_is_not_open_to_the_internet()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();

        var issued = await RideHarness.ReadJsonAsync(
            await harness.IssueLocationRequestAsync(booker.Bearer, IamLookupStub.UnregisteredPhone()));

        var requestId = issued.GetProperty("requestId").GetGuid();

        var response = await harness.PostInternalAsync(
            $"/v1/internal/location-requests/{requestId}/confirm",
            new { lat = RiderLat, lng = RiderLng },
            apiKey: null);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "not-found");
    }

    /// <summary>One rider cannot answer another's prompt, and neither can a bystander read it.</summary>
    [Fact]
    public async Task Only_the_named_rider_may_answer_and_only_the_two_parties_may_read()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();
        var rider = await harness.CreateUserAsync();
        var stranger = await harness.CreateUserAsync();

        var issued = await RideHarness.ReadJsonAsync(
            await harness.IssueLocationRequestAsync(booker.Bearer, rider.Phone));

        var requestId = issued.GetProperty("requestId").GetGuid();

        var hijack = await harness.PostAsync(
            $"/v1/location-requests/{requestId}/confirm",
            new { lat = RiderLat, lng = RiderLng },
            stranger.Bearer);

        await ProblemDocument.AssertAsync(hijack, HttpStatusCode.Forbidden, "forbidden");

        var peek = await harness.GetAsync($"/v1/location-requests/{requestId}", stranger.Bearer);
        await ProblemDocument.AssertAsync(peek, HttpStatusCode.Forbidden, "forbidden");

        // Both parties may read it; the diagnostic exists for reconnect and support.
        foreach (var bearer in new[] { booker.Bearer, rider.Bearer })
        {
            var read = await harness.GetAsync($"/v1/location-requests/{requestId}", bearer);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        // Nothing was written by the stranger's attempt.
        var (state, lat, _) = await harness.ReadLocationRequestAsync(requestId);
        Assert.Equal("Pending", state);
        Assert.Null(lat);
    }

    /// <summary>
    /// P-12: five per hour. The sixth is refused, and — because the limit is checked before the
    /// lookup — it costs iam-svc nothing.
    /// </summary>
    [Fact]
    public async Task A_booker_gets_five_requests_an_hour_and_the_sixth_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();

        for (var i = 0; i < 5; i++)
        {
            var allowed = await harness.IssueLocationRequestAsync(booker.Bearer, IamLookupStub.UnregisteredPhone());
            Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
        }

        var lookupsBefore = harness.Iam.Lookups;

        var refused = await harness.IssueLocationRequestAsync(booker.Bearer, IamLookupStub.UnregisteredPhone());
        await ProblemDocument.AssertAsync(refused, HttpStatusCode.TooManyRequests, "loc-request-rate-limited");

        // The registration oracle is not a free query for a booker who has run out of requests.
        Assert.Equal(lookupsBefore, harness.Iam.Lookups);

        // And the refusal wrote nothing: five rows, not six.
        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            5,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM rides.location_requests WHERE booker_id = @Id;", new { Id = booker.Id }));
    }

    /// <summary>
    /// ADD §11.15's expiry path. The durable deadline is <c>issued_at + ttl_seconds</c> on the row —
    /// there is no <c>rides.timers</c> row and there cannot be — and the sweep is the same worker
    /// pass R-04's timers ride in.
    /// </summary>
    [Fact]
    public async Task An_unanswered_request_expires_on_the_sweep_and_the_booker_is_told()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        // A one-second window rather than five minutes; the contract pins the number a client is
        // told, and this is what makes the backstop assertable in a test.
        await using var harness = await RideHarness.StartAsync(
            postgres, new Dictionary<string, string?> { ["Ride:LocationRequestTtl"] = "00:00:01" });

        var booker = await harness.CreateUserAsync();
        var rider = await harness.CreateUserAsync();

        var issued = await RideHarness.ReadJsonAsync(
            await harness.IssueLocationRequestAsync(booker.Bearer, rider.Phone));

        var requestId = issued.GetProperty("requestId").GetGuid();

        // Not due yet: the sweep must not close a window the rider is still inside.
        var early = await harness.SweepTimersAsync();
        Assert.Equal(0, early.ExpiredLocationRequests);

        await Task.Delay(TimeSpan.FromSeconds(1.2), TestContext.Current.CancellationToken);

        var sweep = await harness.SweepTimersAsync();
        Assert.Equal(1, sweep.ExpiredLocationRequests);

        var (state, _, _) = await harness.ReadLocationRequestAsync(requestId);
        Assert.Equal("Expired", state);

        Assert.Contains("location.request.expired", await harness.ReadEventsAsync(requestId));
        Assert.Equal(["Expired"], await harness.ReadLocationAuditAsync(booker.Id));

        // A rider who taps Share after the window has closed is told so, and Postgres — not their
        // phone's clock — is what decided it.
        var late = await harness.PostAsync(
            $"/v1/location-requests/{requestId}/confirm",
            new { lat = RiderLat, lng = RiderLng },
            rider.Bearer);

        await ProblemDocument.AssertAsync(late, HttpStatusCode.Gone, "token-expired-or-revoked");
    }

    /// <summary>
    /// ADD §11.15: "only the first confirmation is honoured per booker session (subsequent
    /// confirmations transition to Expired)". A booker has one pickup pin.
    /// </summary>
    [Fact]
    public async Task The_first_confirmation_closes_the_bookers_other_open_requests()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();
        var first = await harness.CreateUserAsync();
        var second = await harness.CreateUserAsync();

        var one = (await RideHarness.ReadJsonAsync(
            await harness.IssueLocationRequestAsync(booker.Bearer, first.Phone))).GetProperty("requestId").GetGuid();

        var two = (await RideHarness.ReadJsonAsync(
            await harness.IssueLocationRequestAsync(booker.Bearer, second.Phone))).GetProperty("requestId").GetGuid();

        var confirmed = await harness.PostAsync(
            $"/v1/location-requests/{one}/confirm",
            new { lat = RiderLat, lng = RiderLng },
            first.Bearer);

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        var (winner, _, _) = await harness.ReadLocationRequestAsync(one);
        var (loser, loserLat, _) = await harness.ReadLocationRequestAsync(two);

        Assert.Equal("Confirmed", winner);
        Assert.Equal("Expired", loser);
        Assert.Null(loserLat);

        // The second rider's booker is told on the same channel, so a rider who taps Share and
        // finds nothing happened is not left guessing.
        Assert.Contains("location.request.expired", await harness.ReadEventsAsync(two));

        var late = await harness.PostAsync(
            $"/v1/location-requests/{two}/confirm",
            new { lat = RiderLat, lng = RiderLng },
            second.Bearer);

        await ProblemDocument.AssertAsync(late, HttpStatusCode.Gone, "token-expired-or-revoked");
    }

    [Fact]
    public async Task A_number_that_is_not_a_sri_lankan_mobile_is_refused_before_anything_is_written()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();

        var response = await harness.IssueLocationRequestAsync(booker.Bearer, "+441234567890");
        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "invalid-phone");

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM rides.location_requests WHERE booker_id = @Id;", new { Id = booker.Id }));
    }
}
