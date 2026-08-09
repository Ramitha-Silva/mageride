using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.E2E.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// <b>The no-app path: SMS → SCR-WT-003 → the booker's pickup pin (AL-45, US-25.3).</b>
/// </summary>
/// <remarks>
/// <para>
/// A rider with no MageRide account is asked where they are. D1'/D5' (US-8.19) said that could not
/// happen — unregistered meant the booker typed the pickup themselves — and AL-45 is later and
/// resolved it the other way: <c>notification-svc</c> mints a <c>pickup_confirm</c> token, SMSes the
/// link, and <b>the same <c>rides.location_requests</c> state machine</b> is fed from a browser
/// through public-bff.
/// </para>
/// <para>
/// <b>Everything here is reached from the message.</b> The token is never read from
/// <c>safety.trip_share_tokens</c> to open a page: AL-44 makes it mint-and-SMS with no API that can
/// return one, so taking it from the table would be asserting about a page no rider could have
/// opened. It comes out of the body the platform composed — rendered by the real content-svc from
/// what migration 1902 seeded — and arrives at the gateway addressed to the rider's own number.
/// </para>
/// <para>
/// ADD §11.15, AL-45, BR-29.1, US-25.3, D6' I-29.2.
/// </para>
/// </remarks>
[Collection<ProxyPackageCollection>]
[Trait("Category", "ProxyPackage")]
public sealed class WebPickupConfirmScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
    : ProxyPackageScenario(postgres, redis, redpanda)
{
    /// <summary>
    /// The whole AL-45 path, from a number nobody has an account for to a pin on the booker's map.
    /// </summary>
    [Fact]
    public Task An_unregistered_rider_confirms_a_pickup_from_a_browser() =>
        RunAsync(async (fleet, rides) =>
        {
            var booker = await fleet.CreatePassengerAsync("Chaminda");

            // Nothing is written for this number. `iam.users` not having the row is the entire
            // difference between this scenario and the FCM one, and iam-svc is asked over HTTP
            // rather than assumed.
            var riderPhone = ProxyPackageFleet.UnregisteredPhone();

            await using var live = await LiveConnection.OpenAsync(fleet, booker.Bearer);

            // ---- ask ---------------------------------------------------------------------------
            var request = await fleet.RequestLocationAsync(booker, riderPhone);

            // **202, not an error.** `RiderNotRegistered` is a live request the rider can still
            // answer — ADD §11.15 ended the round-trip here and AL-45 is later and wins — so a
            // status that said otherwise would have the booker's app show a failure for a request
            // that is running.
            Assert.Equal("RiderNotRegistered", request.State);
            Assert.Equal(300, request.Ttl);

            await live.SubscribeLocationRequestAsync(request.RequestId);

            var row = await fleet.ReadLocationRowAsync(request.RequestId);
            Assert.Null(row.RiderId);

            // ---- the SMS -----------------------------------------------------------------------
            // ride-svc committed, its dispatcher published to Redpanda, notification-svc's consumer
            // branched on `state`, minted a token, rendered a body against content-svc over HTTP and
            // queued it; the delivery worker handed it to the gateway. None of that was called from
            // here.
            var message = await fleet.Sms.AwaitSmsAsync(riderPhone);
            var token = SmsGateway.TokenIn(message);

            // The credential is what the row holds, and the window is AL-45's — the token may not
            // outlive the request it stands in for.
            var minted = Assert.Single(await fleet.ReadShareTokensAsync(null, row.Id));

            Assert.Equal("pickup_confirm", minted.Scope);
            Assert.Equal(token, minted.Token);
            Assert.Null(minted.RevokedAt);
            Assert.Equal(300, (minted.ExpiresAt - row.IssuedAt).TotalSeconds, precision: 0);

            // ---- SCR-WT-003 --------------------------------------------------------------------
            var page = await fleet.Web.OpenAsync(token);

            Assert.Equal(HttpStatusCode.OK, page.Status);
            Assert.Equal("pickup_confirm", page.Json.GetProperty("kind").GetString());
            Assert.Equal("Chaminda", page.Json.GetProperty("bookerFirstName").GetString());

            // The countdown SCR-WT-003 draws, off the request row rather than off the token.
            Assert.InRange(page.Json.GetProperty("ttlRemainingSec").GetInt32(), 1, 300);

            // **The narrowest of the three snapshots, and meant to be.** A rider being asked for
            // their live position is not owed an identity file on the person asking: no ride, no
            // driver, no vehicle, no plate, no position (P-02).
            foreach (var absent in new[] { "rideId", "driver", "vehicle", "position", "state", "fare" })
            {
                Assert.False(
                    page.Json.TryGetProperty(absent, out _),
                    $"SCR-WT-003 carried '{absent}', which a pickup_confirm holder may not see (P-02).");
            }

            Assert.False(page.Mentions(booker.Phone), "SCR-WT-003 showed the booker's number.");

            // The live feed for this scope is a branch that produces no position frame — the one
            // coordinate this token exists to *ask* for cannot come back down it.
            var feed = await fleet.Web.PollAsync(token);

            Assert.Equal(HttpStatusCode.OK, feed.Status);

            foreach (var frame in feed.Json.GetProperty("events").EnumerateArray())
            {
                // Absent rather than null: the serializer omits nulls, so "has no position" and
                // "says its position is nothing" are the same wire fact and both are acceptable.
                Assert.True(
                    !frame.TryGetProperty("position", out var carried)
                    || carried.ValueKind is JsonValueKind.Null,
                    $"A pickup_confirm feed carried a position (P-02): {frame}");
            }

            // ---- Share -------------------------------------------------------------------------
            var actual = new GeoPoint(6.9271, 79.8612);
            var shared = await fleet.Web.ConfirmPickupAsync(token, actual.Latitude, actual.Longitude);

            Assert.Equal(HttpStatusCode.OK, shared.Status);
            Assert.Equal("Confirmed", shared.Json.GetProperty("state").GetString());

            // public-bff forwarded rather than wrote: the row it changed is ride-svc's, through the
            // internal route ride-svc built for this caller, so `rides.location_requests` still has
            // exactly one writer.
            var confirmed = await fleet.WaitForRequestStateAsync(request.RequestId, "Confirmed");

            Assert.Equal(actual.Latitude, confirmed.ResolvedLat!.Value, precision: 4);
            Assert.Equal(actual.Longitude, confirmed.ResolvedLng!.Value, precision: 4);

            // ---- the booker's socket, identically to the in-app path ----------------------------
            var resolution = await live.AwaitResolutionAsync(request.RequestId);

            Assert.Equal("Confirmed", resolution.GetProperty("state").GetString());
            Assert.Equal(
                actual.Latitude, resolution.GetProperty("geo").GetProperty("lat").GetDouble(), precision: 4);

            // ---- BR-29.1: single use -----------------------------------------------------------
            // The token is burned *before* the coordinate is forwarded, which is what makes the
            // single use hold under a double tap: the loser of the burn never reaches ride-svc.
            var burned = Assert.Single(await fleet.ReadShareTokensAsync(null, row.Id));
            Assert.NotNull(burned.RevokedAt);

            var again = await fleet.Web.ConfirmPickupAsync(token, 7.29, 80.63);
            Assert.Equal(HttpStatusCode.Gone, again.Status);

            // And the second tap changed nothing — the pin is still the one the rider sent.
            var unchanged = await fleet.ReadLocationRowAsync(request.RequestId);
            Assert.Equal(actual.Latitude, unchanged.ResolvedLat!.Value, precision: 4);

            // ---- the booker books at the pin they never typed ------------------------------------
            var driver = await fleet.CreateOnlineDriverAsync(Near(actual));
            var ride = await fleet.BookProxyAsync(
                booker,
                driver,
                actual,
                new GeoPoint(actual.Latitude - 0.083, actual.Longitude + 0.0225),
                riderPhone);

            rides.Add(ride.RideId);

            var offer = await fleet.WaitForOfferAsync(ride.RideId);
            Assert.Equal(driver.DriverId, offer.DriverId);

            // P-03 and AL-48 conflict in exactly one cell and P-03 wins: this rider has no account,
            // so there is no number to hand the driver and the field is absent — never the booker's,
            // which P-05 forbids outright.
            var accepted = await AcceptAsync(fleet, ride, driver);
            var (_, driverView) = await fleet.ReadRideAsAsync(accepted.RideId, driver.Bearer);

            Assert.DoesNotContain(booker.Phone, driverView, StringComparison.Ordinal);
            Assert.DoesNotContain(riderPhone, driverView, StringComparison.Ordinal);
        });

    /// <summary>
    /// The web decline, with a coordinate posted anyway — P-02 held by three components.
    /// </summary>
    /// <remarks>
    /// The handler takes no body parameter, <c>IRideClient.DeclineAsync</c> sends no content, and
    /// ride-svc's statement has no <c>resolved_geo</c> in its <c>SET</c> list. A browser that sent
    /// one has all three to get past and gets past none of them, which is what makes this a property
    /// of the code rather than of a reviewer's care.
    /// </remarks>
    [Fact]
    public Task A_web_decline_carrying_coordinates_still_transmits_and_stores_none() =>
        RunAsync(async (fleet, noRides) =>
        {
            var booker = await fleet.CreatePassengerAsync("Chaminda");
            var riderPhone = ProxyPackageFleet.UnregisteredPhone();

            await using var live = await LiveConnection.OpenAsync(fleet, booker.Bearer);

            var request = await fleet.RequestLocationAsync(booker, riderPhone);
            Assert.Equal("RiderNotRegistered", request.State);

            await live.SubscribeLocationRequestAsync(request.RequestId);

            var token = SmsGateway.TokenIn(await fleet.Sms.AwaitSmsAsync(riderPhone));

            var declined = await fleet.Web.DeclinePickupAsync(
                token, new { lat = 6.9271, lng = 79.8612, accuracy = 8 });

            Assert.Equal(HttpStatusCode.OK, declined.Status);
            Assert.Equal("Declined", declined.Json.GetProperty("state").GetString());

            var row = await fleet.WaitForRequestStateAsync(request.RequestId, "Declined");

            Assert.Null(row.ResolvedLat);
            Assert.Null(row.ResolvedLng);
            Assert.Null(row.ResolvedAccuracyM);

            // Nothing published one either, and nothing reached the booker carrying one.
            var published = await fleet.ReadEventPayloadAsync(request.RequestId, "location.request.declined");
            Assert.False(published.GetProperty("payload").TryGetProperty("geo", out _));

            var resolution = await live.AwaitResolutionAsync(request.RequestId);
            Assert.Equal("Declined", resolution.GetProperty("state").GetString());
            Assert.False(
                resolution.TryGetProperty("geo", out var geo) && geo.ValueKind is not JsonValueKind.Null);

            // The 6.9271 the browser posted exists nowhere on the platform for this request.
            await using var connection = await fleet.OpenAsync();

            Assert.Equal(
                0,
                await connection.ExecuteScalarAsync<int>(
                    """
                    SELECT count(*)::int FROM rides.location_requests
                     WHERE request_id = @RequestId AND resolved_geo IS NOT NULL;
                    """,
                    new { RequestId = request.RequestId }));

            // Burned all the same: a decline is an answer, and BR-29.1 makes the link single-use
            // whichever way it was answered.
            var burned = Assert.Single(await fleet.ReadShareTokensAsync(null, row.Id));
            Assert.NotNull(burned.RevokedAt);

            Assert.Equal(HttpStatusCode.Gone, (await fleet.Web.DeclinePickupAsync(token)).Status);

            // **Two entries, not one, and that is the AL-45 path said out loud.** An unregistered
            // rider is an outcome on arrival, so it is audited on arrival; the answer that arrives
            // five minutes later from a browser is a second outcome of the same request. P-12's
            // pattern — "this booker keeps pinging somebody who keeps refusing" — needs both.
            Assert.Equal(["NotRegistered", "Declined"], await fleet.ReadLocationAuditAsync(booker.Id));
        });

    /// <summary>
    /// A generous token does not extend a closed request: the 300 s deadline is the request row.
    /// </summary>
    /// <remarks>
    /// The two windows are set from the same instant and could drift — public-bff reads
    /// <c>issued_at + ttl_seconds</c> rather than the token's own <c>expires_at</c> precisely so that
    /// a token minted a second late cannot buy the rider a second more. Driven here by expiring the
    /// request while its token is still live, which is the only way to tell the two apart.
    /// </remarks>
    [Fact]
    public Task An_expired_request_refuses_a_token_that_is_still_live() =>
        RunAsync(async (fleet, noRides) =>
        {
            var booker = await fleet.CreatePassengerAsync("Chaminda");
            var riderPhone = ProxyPackageFleet.UnregisteredPhone();

            var request = await fleet.RequestLocationAsync(booker, riderPhone);
            var token = SmsGateway.TokenIn(await fleet.Sms.AwaitSmsAsync(riderPhone));

            var row = await fleet.ReadLocationRowAsync(request.RequestId);
            var minted = Assert.Single(await fleet.ReadShareTokensAsync(null, row.Id));

            // The page opens while both windows are live.
            Assert.Equal(HttpStatusCode.OK, (await fleet.Web.OpenAsync(token)).Status);

            await fleet.AgeLocationRequestAsync(request.RequestId);
            await fleet.WaitForRequestStateAsync(request.RequestId, "Expired");

            // The token itself has not expired and has not been burned — so if the request's own
            // deadline were not the one being read, this would still work.
            var stillLive = Assert.Single(await fleet.ReadShareTokensAsync(null, row.Id));
            Assert.Null(stillLive.RevokedAt);
            Assert.True(stillLive.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(-1));
            Assert.Equal(minted.Token, stillLive.Token);

            var late = await fleet.Web.ConfirmPickupAsync(token, 6.9271, 79.8612);

            Assert.Equal(HttpStatusCode.Gone, late.Status);

            // Still nothing stored, which is the assertion that matters — a refusal that had
            // written the coordinate first would answer identically.
            var closed = await fleet.ReadLocationRowAsync(request.RequestId);
            Assert.Null(closed.ResolvedLat);
            Assert.Null(closed.ResolvedLng);
        });

    /// <summary>
    /// Waits for the real offer and takes it — a local copy of the base class's private walk,
    /// because this scenario books its ride at a pin it was given rather than one it chose.
    /// </summary>
    private static async Task<LiveRide> AcceptAsync(ProxyPackageFleet fleet, LiveRide ride, Driver driver)
    {
        var offer = await fleet.WaitForOfferAsync(ride.RideId);
        var offered = await fleet.ReadRideAsync(ride.RideId);

        using var accepted = await fleet.AcceptAsync(ride.RideId, driver, offer.Id, offered.Version);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        return ride with
        {
            Version = (await ProxyPackageFleet.ReadJsonAsync(accepted)).GetProperty("version").GetInt64(),
        };
    }
}
