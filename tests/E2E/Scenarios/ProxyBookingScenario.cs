using System.Net;
using Dapper;
using MageRide.E2E.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// <b>Proxy booking — somebody arranging a ride for somebody else (P-01..P-05, P-12, P-13).</b>
/// </summary>
/// <remarks>
/// <para>
/// The round-trip of ADD §11.15 in both of its branches: a registered rider answering in the app,
/// and — AL-45's later, narrower rule — an unregistered one answering from a browser they reached
/// through an SMS. Both end the same way, on the booker's WebSocket, and both are driven here
/// through the surfaces the two apps have.
/// </para>
/// <para>
/// The privacy half is the point of the feature and is asserted as hard as the happy path: a decline
/// transmits no coordinates and stores none, an expiry says nothing about where anybody was, and the
/// driver is never told who booked (P-05).
/// </para>
/// <para>
/// ADD §11.15, D5' §10, AL-45, US-8.16–8.21, US-25.3.
/// </para>
/// </remarks>
[Collection<ProxyPackageCollection>]
[Trait("Category", "ProxyPackage")]
public sealed class ProxyBookingScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
    : ProxyPackageScenario(postgres, redis, redpanda)
{
    /// <summary>
    /// The whole of §11.15's registered branch: ask, prompt, share, and a pickup pin on the booker's
    /// map that they never typed.
    /// </summary>
    [Fact]
    public Task A_registered_rider_shares_a_pickup_point_and_it_reaches_the_booker() =>
        RunAsync(async (fleet, rides) =>
        {
            var booker = await fleet.CreatePassengerAsync("Chaminda");
            var rider = await fleet.CreatePassengerAsync("Kamala");

            // The booker's app opens the socket first and joins the group it is about to be
            // answered on — which is what P-13 means by "no polling". The group name is built from
            // the caller's own subject, so joining is not evidence of anything except that the
            // booker is who the token says.
            await using var live = await LiveConnection.OpenAsync(fleet, booker.Bearer);

            // ---- ask ---------------------------------------------------------------------------
            var request = await fleet.RequestLocationAsync(booker, rider.Phone);

            // iam-svc was really asked, over HTTP, and really found the row this scenario seeded.
            // `Pending` is the whole difference between this test and the AL-45 one below.
            Assert.Equal("Pending", request.State);
            Assert.Equal(300, request.Ttl);

            await live.SubscribeLocationRequestAsync(request.RequestId);

            var row = await fleet.ReadLocationRowAsync(request.RequestId);
            Assert.Equal(rider.Id, row.RiderId);
            Assert.Equal(booker.Id, row.BookerId);

            // P-03's digest, not the number: an unregistered rider's MSISDN is stored this way and
            // so is a registered one's, because the column does not know the difference.
            Assert.Null(row.ResolvedLat);

            // ---- the prompt --------------------------------------------------------------------
            // notification-svc heard `location.request.issued` across Redpanda and queued the FCM
            // data message §11.15 draws — `{kind:'location_request', requestId, …}`, silent, so the
            // app can draw the prompt itself. The push provider in this fleet is the log channel
            // (there is no FCM project to hold), so what is asserted is the queue row: who it is
            // addressed to, and that it is a push rather than an SMS.
            var prompt = await AwaitNotificationAsync(fleet, rider.Id, "location_request");
            Assert.Equal("push", prompt.Channel);

            // The rider is pinged; the booker is not. P-12 meters the booker precisely because the
            // rider has done nothing.
            Assert.Empty(await ReadNotificationsAsync(fleet, booker.Id, "location_request"));

            // ---- share -------------------------------------------------------------------------
            var actual = new GeoPoint(6.9271, 79.8612);

            using (var confirmed = await fleet.ConfirmLocationAsync(rider, request.RequestId, actual))
            {
                Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

                var body = await ProxyPackageFleet.ReadJsonAsync(confirmed);
                Assert.Equal("Confirmed", body.GetProperty("state").GetString());
            }

            // ---- the answer, on the booker's socket ---------------------------------------------
            // ride-svc → rides.outbox → Redpanda → fanout-svc → this WebSocket. Nothing in this
            // assembly moved it along and the booker never asked again.
            var resolution = await live.AwaitResolutionAsync(request.RequestId);

            Assert.Equal("Confirmed", resolution.GetProperty("state").GetString());

            var geo = resolution.GetProperty("geo");
            Assert.Equal(actual.Latitude, geo.GetProperty("lat").GetDouble(), precision: 4);
            Assert.Equal(actual.Longitude, geo.GetProperty("lng").GetDouble(), precision: 4);

            // ---- and the diagnostic read agrees, for the two people it is for --------------------
            // `GET /v1/location-requests/{id}` is the booker's recovery path when the socket dropped
            // during the round-trip. Both parties may make it; **a stranger may not** — a request
            // names a third party's position, and a reader who is neither party has no business
            // learning that one was even asked for.
            var readBack = await fleet.ReadLocationRequestAsync(booker, request.RequestId);

            Assert.Equal("Confirmed", readBack.GetProperty("state").GetString());
            Assert.Equal(actual.Latitude, readBack.GetProperty("geo").GetProperty("lat").GetDouble(), precision: 4);

            Assert.Equal(
                "Confirmed",
                (await fleet.ReadLocationRequestAsync(rider, request.RequestId)).GetProperty("state").GetString());

            var stranger = await fleet.CreatePassengerAsync("Nobody");

            using (var refused = await fleet.ReadLocationRequestRawAsync(stranger, request.RequestId))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.DoesNotContain(
                    actual.Latitude.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                    await refused.Content.ReadAsStringAsync(),
                    StringComparison.Ordinal);
            }

            // ---- and the pin is the ride's -----------------------------------------------------
            // US-8.19's flow ends here: the booker books at the point the rider sent, and the ride
            // that results is an ordinary proxy booking that dispatch treats like any other.
            var driver = await fleet.CreateOnlineDriverAsync(Near(actual));
            var ride = await fleet.BookProxyAsync(
                booker, driver, actual, new GeoPoint(actual.Latitude - 0.083, actual.Longitude + 0.0225), rider.Phone);

            rides.Add(ride.RideId);

            var offer = await fleet.WaitForOfferAsync(ride.RideId);
            Assert.Equal(driver.DriverId, offer.DriverId);

            // The audit ledger P-12 reads. One entry, and it says what happened rather than that
            // something did.
            Assert.Equal(["Confirmed"], await fleet.ReadLocationAuditAsync(booker.Id));
        });

    /// <summary>
    /// <b>C122's second definition-of-done item: the decline path stores no coordinates anywhere.</b>
    /// </summary>
    /// <remarks>
    /// Asserted across every store on the platform that has a column one could land in — the request
    /// row, the outbox payload that leaves ride-svc, and the frame that arrives on the booker's own
    /// socket — because P-02's fence is that three components have no way to carry one, and a test of
    /// only the first would not notice the other two growing one.
    /// </remarks>
    [Fact]
    public Task A_declined_request_transmits_and_stores_no_coordinates() =>
        RunAsync(async (fleet, noRides) =>
        {
            var booker = await fleet.CreatePassengerAsync("Chaminda");
            var rider = await fleet.CreatePassengerAsync("Kamala");

            await using var live = await LiveConnection.OpenAsync(fleet, booker.Bearer);

            var request = await fleet.RequestLocationAsync(booker, rider.Phone);
            Assert.Equal("Pending", request.State);

            await live.SubscribeLocationRequestAsync(request.RequestId);

            // The route takes no body at all, so there is no parameter a coordinate could arrive in.
            using (var declined = await fleet.DeclineLocationAsync(rider, request.RequestId))
            {
                Assert.Equal(HttpStatusCode.OK, declined.StatusCode);
                Assert.Equal(
                    "Declined",
                    (await ProxyPackageFleet.ReadJsonAsync(declined)).GetProperty("state").GetString());
            }

            // (1) The row. `resolved_geo` and `resolved_accuracy_m` are the only columns on
            // `rides.location_requests` that could hold a position, and the statement that performed
            // the decline has neither in its SET list.
            var row = await fleet.ReadLocationRowAsync(request.RequestId);
            Assert.Equal("Declined", row.State);
            Assert.Null(row.ResolvedLat);
            Assert.Null(row.ResolvedLng);
            Assert.Null(row.ResolvedAccuracyM);

            // (2) The event. Read as text rather than as a shape: `geo` being absent is the claim,
            // and a member that is present and null would deserialise identically.
            var published = await fleet.ReadEventPayloadAsync(request.RequestId, "location.request.declined");
            var payload = published.GetProperty("payload");

            Assert.Equal("Declined", payload.GetProperty("state").GetString());
            Assert.False(payload.TryGetProperty("geo", out _), "A declined request published a geo member (P-02).");
            Assert.False(payload.TryGetProperty("accuracyM", out _));

            // (3) The booker's socket. They are told the request closed — a rider who taps Decline
            // and is told nothing happened is worse than one told the request is over — and they are
            // told nothing else.
            var resolution = await live.AwaitResolutionAsync(request.RequestId);

            Assert.Equal("Declined", resolution.GetProperty("state").GetString());
            Assert.False(
                resolution.TryGetProperty("geo", out var geo) && geo.ValueKind is not System.Text.Json.JsonValueKind.Null,
                "A decline reached the booker's socket carrying a position (P-02).");

            // (4) And nowhere else on the platform. `resolved_geo` is the only geography column any
            // component writes for a location request; this is the whole-table version of (1), so a
            // second row written by anybody would fail it.
            await using var connection = await fleet.OpenAsync();

            var stored = await connection.ExecuteScalarAsync<int>(
                """
                SELECT count(*)::int FROM rides.location_requests
                 WHERE booker_id = @BookerId AND resolved_geo IS NOT NULL;
                """,
                new { BookerId = booker.Id });

            Assert.Equal(0, stored);

            Assert.Equal(["Declined"], await fleet.ReadLocationAuditAsync(booker.Id));
        });

    /// <summary>
    /// The rider never picks the phone up: §11.15's expiry path, and US-8.19's fallback after it.
    /// </summary>
    /// <remarks>
    /// <b>The window is asserted before the clock is moved.</b> ADD §11.15's 300 s is the request
    /// row's own <c>ttl_seconds</c> — it cannot be a <c>rides.timers</c> row, because that table's
    /// <c>ride_id</c> is <c>NOT NULL</c> and the request exists before the ride — so what is brought
    /// forward is <c>issued_at</c>, the platform's record of when the booker asked. What fires is
    /// ride-svc's own sweep, riding in the same pass as the R-04 timers.
    /// </remarks>
    [Fact]
    public Task An_unanswered_request_expires_on_its_own_and_the_booker_falls_back() =>
        RunAsync(async (fleet, rides) =>
        {
            var booker = await fleet.CreatePassengerAsync("Chaminda");
            var rider = await fleet.CreatePassengerAsync("Kamala");

            await using var live = await LiveConnection.OpenAsync(fleet, booker.Bearer);

            var request = await fleet.RequestLocationAsync(booker, rider.Phone);
            await live.SubscribeLocationRequestAsync(request.RequestId);

            // P-02's window, off the running service's own row, before anything touches it.
            await fleet.AssertLocationRequestWindowAsync(request.RequestId);
            Assert.Equal(300, request.Ttl);
            Assert.Equal(
                300, (request.ExpiresAt - (await fleet.ReadLocationRowAsync(request.RequestId)).IssuedAt)
                    .TotalSeconds, precision: 0);

            await fleet.AgeLocationRequestAsync(request.RequestId);

            // Nobody called ExpireDueAsync. ride-svc's worker found the row over
            // `ix_location_requests_due` and closed it.
            var expired = await fleet.WaitForRequestStateAsync(request.RequestId, "Expired");
            Assert.Null(expired.ResolvedLat);

            var resolution = await live.AwaitResolutionAsync(request.RequestId);
            Assert.Equal("Expired", resolution.GetProperty("state").GetString());
            Assert.False(
                resolution.TryGetProperty("geo", out var geo) && geo.ValueKind is not System.Text.Json.JsonValueKind.Null,
                "An expired request told the booker where the rider was (P-02).");

            // ---- US-8.19's fallback, which AL-45 retained -----------------------------------
            // The booker types the pickup themselves and books the same proxy ride. Retained rather
            // than replaced is the whole of AL-45's finding, and this is what "retained" means.
            var (pickup, dropoff) = ModeCFleet.NextPlaces();
            var driver = await fleet.CreateOnlineDriverAsync(Near(pickup));

            var ride = await fleet.BookProxyAsync(booker, driver, pickup, dropoff, rider.Phone);
            rides.Add(ride.RideId);

            var offer = await fleet.WaitForOfferAsync(ride.RideId);
            Assert.Equal(driver.DriverId, offer.DriverId);

            // The abandoned request left nothing behind that says where anybody was. `resolved_at`
            // *is* stamped, and that is right: it records when the request closed, which is a fact
            // about the request rather than about the rider — the three columns that could describe
            // a position are the ones that stay empty.
            var closed = await fleet.ReadLocationRowAsync(request.RequestId);

            Assert.NotNull(closed.ResolvedAt);
            Assert.Null(closed.ResolvedLat);
            Assert.Null(closed.ResolvedLng);
            Assert.Null(closed.ResolvedAccuracyM);

            Assert.Equal(["Expired"], await fleet.ReadLocationAuditAsync(booker.Id));
        });

    /// <summary>
    /// <b>The C122 fence: the driver never sees the booker's identity or number (P-05).</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two people are on the passenger side of a proxy booking and only one of them is in the car.
    /// D3' <c>RideDetail</c> gives the driver a <c>counterpartyPhone</c>, and P-05 says which one it
    /// is: the rider, never the booker. The assertion is made on the response <em>text</em> and on
    /// every event payload the ride produced, because "the booker's number is not in the
    /// counterparty field" is a much weaker claim than "the booker's number is not there at all".
    /// </para>
    /// <para>
    /// The booker's <em>name</em> is checked the same way. <c>RideDetailResponse</c> carries a
    /// <c>riderName</c> and has no member for a booker's, which is the shape P-05 asks for — and a
    /// shape is only a fence while nothing fills it from somewhere else.
    /// </para>
    /// </remarks>
    [Fact]
    public Task The_driver_is_given_the_riders_number_and_never_the_bookers() =>
        RunAsync(async (fleet, rides) =>
        {
            // Distinctive names, so "the booker's name is absent" is a claim about this booker
            // rather than about a string that happens not to appear.
            var booker = await fleet.CreatePassengerAsync("Wickramasinghe");
            var rider = await fleet.CreatePassengerAsync("Abeyawardena");

            var ride = await AcceptedProxyAsync(fleet, rides, booker, rider.Phone, riderName: "Abeyawardena");

            var (status, driverView) = await fleet.ReadRideAsAsync(ride.RideId, ride.Driver.Bearer);
            Assert.Equal(HttpStatusCode.OK, status);

            using var document = System.Text.Json.JsonDocument.Parse(driverView);
            var view = document.RootElement;

            // The badge's source. `RideDetail` carries `kind` and no `isProxy` — one machine, three
            // kinds (ADD Appendix B.2 invariant 6), and a second boolean saying the same thing is a
            // copy that can disagree.
            Assert.Equal("proxy", view.GetProperty("kind").GetString());

            // P-05, positively: the counterparty is the rider.
            Assert.Equal(rider.Phone, view.GetProperty("counterpartyPhone").GetString());

            // P-05, negatively — and this is the half that matters. Neither the booker's number nor
            // their name is anywhere in what the driver was handed.
            Assert.DoesNotContain(booker.Phone, driverView, StringComparison.Ordinal);
            Assert.DoesNotContain("Wickramasinghe", driverView, StringComparison.OrdinalIgnoreCase);

            // The badge P-05 asks the driver app to draw, and the rider's name it draws beside it.
            Assert.Equal("Abeyawardena", view.GetProperty("riderName").GetString());

            // The rider's side is the mirror image: they are given the driver, and the driver only.
            var (riderStatus, riderView) = await fleet.ReadRideAsAsync(ride.RideId, rider.Bearer);
            Assert.Equal(HttpStatusCode.OK, riderStatus);
            Assert.Equal(ride.Driver.Phone, System.Text.Json.JsonDocument.Parse(riderView)
                .RootElement.GetProperty("counterpartyPhone").GetString());

            // And no `ride.events` payload carries the booker's number either — which is what stops
            // a consumer downstream putting it back on a screen ride-svc redacted it from.
            foreach (var payload in await fleet.ReadEventPayloadsAsync(ride.RideId))
            {
                Assert.DoesNotContain(booker.Phone, payload, StringComparison.Ordinal);
            }
        });

    /// <summary>
    /// P-12's 5-per-hour, enforced by the rows themselves rather than by a bucket.
    /// </summary>
    /// <remarks>
    /// Included because it is the gate that protects everything above it: the location request is a
    /// registration oracle — send a number, learn whether it is on the platform — and ride-svc checks
    /// the limit <em>before</em> the iam-svc lookup for exactly that reason. A booker out of requests
    /// must not be able to keep asking for free.
    /// </remarks>
    [Fact]
    public Task A_booker_may_ask_five_times_an_hour_and_the_sixth_is_refused() =>
        RunAsync(async (fleet, noRides) =>
        {
            var booker = await fleet.CreatePassengerAsync("Chaminda");
            var rider = await fleet.CreatePassengerAsync("Kamala");

            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var request = await fleet.RequestLocationAsync(booker, rider.Phone);
                Assert.Equal("Pending", request.State);
            }

            using var refused = await fleet.RequestLocationRawAsync(booker, rider.Phone);

            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.Equal("loc-request-rate-limited", await ProxyPackageFleet.ProblemCodeAsync(refused));

            // The refusal rolled back, so it spent no token of its own: exactly five rows, not six.
            await using var connection = await fleet.OpenAsync();

            Assert.Equal(
                5,
                await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*)::int FROM rides.location_requests WHERE booker_id = @BookerId;",
                    new { BookerId = booker.Id }));
        });

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Waits for notification-svc to have queued a message of <paramref name="type"/> for somebody.
    /// </summary>
    /// <remarks>
    /// <c>comms.notifications</c> rather than a captured FCM call: this fleet holds no FCM project,
    /// so the push channel is the log one and there is no wire to watch. What the row proves is
    /// everything upstream of the transport — the event crossed Redpanda, the handler branched on
    /// <c>state</c>, the recipient was resolved, the preference gate passed and the type is the one
    /// D5' §14.4 names. The transport itself is C051's own suite's.
    /// </remarks>
    private static async Task<(string Channel, string Type)> AwaitNotificationAsync(
        ProxyPackageFleet fleet, Guid userId, string type, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? TimeSpan.FromSeconds(45));

        do
        {
            if (await ReadNotificationsAsync(fleet, userId, type) is [var first, ..])
            {
                return first;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail($"notification-svc never queued a '{type}' for {userId}.");

        return default;
    }

    private static async Task<IReadOnlyList<(string Channel, string Type)>> ReadNotificationsAsync(
        ProxyPackageFleet fleet, Guid userId, string type)
    {
        await using var connection = await fleet.OpenAsync();

        return [.. await connection.QueryAsync<(string, string)>(
            """
            SELECT channel, notification_type FROM comms.notifications
             WHERE recipient_user_id = @UserId AND notification_type = @Type ORDER BY created_at;
            """,
            new { UserId = userId, Type = type })];
    }
}
