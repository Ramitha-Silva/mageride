using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.E2E.Infrastructure;
using MageRide.Ride.Domain;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// <b>Directional Travel (D5' §12, DT-01…DT-08) — a driver heading home is only offered rides that
/// take them there, and DT-06 says that must never leave a passenger worse off than before the
/// feature existed.</b>
/// </summary>
/// <remarks>
/// <para>
/// The predicate itself is three lines of arithmetic and dispatch-svc's own suite asserts every
/// boundary of it. What this scenario asserts is the consequence, end to end: a driver sets a
/// filter through the standby route, and from then on the rides that reach them — and the rides
/// that do not — are decided inside a real candidate build, with the audit row that explains it and
/// the DT-08 badge on the offer.
/// </para>
/// <para>
/// The geometry is deliberately blunt. The driver's destination is due north; the matching ride
/// runs due north from a pickup 70 m away, and the non-matching one runs due south from the same
/// pickup. That is a bearing difference of 0° against 180° with a 45° threshold, so nothing here
/// turns on a boundary — this is about which drivers a round considers, not about where the
/// threshold is.
/// </para>
/// </remarks>
[Collection<ModeCCollection>]
[Trait("Category", "ModeC")]
public sealed class DirectionalTravelScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    : ModeCScenario(postgres, redis, redpanda, emqx)
{
    /// <summary>~20 km north — where the driver is trying to get to.</summary>
    private const double DestinationOffset = 0.18;

    /// <summary>~9.4 km — the length of every ride in this scenario, north or south.</summary>
    private const double RideLength = 0.085;

    /// <summary>
    /// <b>DT-02 / DT-08.</b> The ride that points the driver's way reaches them; the one that does
    /// not is filtered out, and the audit says which clause did it.
    /// </summary>
    [Fact]
    public Task A_ride_going_the_drivers_way_is_offered_and_one_going_the_other_way_is_not() =>
        RunAsync(async (fleet, rides) =>
        {
            var (pickup, _) = ModeCFleet.NextPlaces();

            var driver = await fleet.CreateOnlineDriverAsync(Near(pickup));
            await SetFilterAsync(fleet, driver, North(pickup, DestinationOffset), "Home");

            // ---- the wrong way -----------------------------------------------------------------
            // The only driver near this pickup is filtered out, so the round finds nobody and the
            // ride stays in Matching. Nothing is offered and — US-6A.23 — nothing is held against
            // the driver for it.
            var southbound = await fleet.BookAsync(
                await fleet.CreatePassengerAsync(), driver, pickup, South(pickup, RideLength));

            rides.Add(southbound.RideId);

            await fleet.WaitForStateAsync(southbound.RideId, RideStates.Matching);

            await fleet.UntilAsync(
                southbound.RideId,
                async () => await RejectedByAsync(fleet, southbound.RideId, driver.DriverId) is not null,
                "the candidate build never recorded a verdict for the filtered driver");

            Assert.Equal("directional", await RejectedByAsync(fleet, southbound.RideId, driver.DriverId));

            // R-11's audit is asked "why did this driver *not* get the ride" more often than the
            // converse, so the row carries the measurement and the threshold that refused it.
            var breakdown = await BreakdownAsync(fleet, southbound.RideId, driver.DriverId);
            var directional = breakdown.GetProperty("directional");

            Assert.False(directional.GetProperty("matched").GetBoolean());
            Assert.Equal("bearing", directional.GetProperty("failedOn").GetString());
            Assert.Equal(45, directional.GetProperty("thetaMaxDeg").GetDouble(), tolerance: 0.001);
            Assert.True(
                directional.GetProperty("bearingDiffDeg").GetDouble() > 45,
                "a due-south ride was measured as pointing the same way as a due-north destination");

            // Nothing about being filtered out costs the driver anything: no offer row exists, so
            // the US-6A.14 acceptance rate — counted from `dispatch.offers` — never sees it.
            Assert.Null(await fleet.ReadOfferAsync(southbound.RideId));

            // The passenger is not stranded either; the ride is still live and still theirs to
            // cancel. DT-06's promise begins here.
            Assert.Equal(RideStates.Matching, (await fleet.ReadRideAsync(southbound.RideId)).State);

            using (var abandoned = await fleet.CancelAsync(
                southbound.RideId, southbound.Passenger.Bearer, "RIDER_CHANGED_MIND"))
            {
                Assert.Equal(HttpStatusCode.OK, abandoned.StatusCode);
            }

            // ---- the driver's way --------------------------------------------------------------
            var northbound = await fleet.BookAsync(
                await fleet.CreatePassengerAsync(), driver, pickup, North(pickup, RideLength));

            rides.Add(northbound.RideId);

            var offer = await fleet.WaitForOfferAsync(northbound.RideId);
            Assert.Equal(driver.DriverId, offer.DriverId);

            // DT-08's badge, so the driver's screen can say why this one reached them — and it is
            // read off the same audit breakdown the operator reads back, so the two cannot disagree.
            // dispatch-svc's `offer.created`, not ride-svc's: the two share a name and say different
            // things (the aggregate moved, versus who was chosen and why).
            var created = await fleet.ReadDispatchEventPayloadAsync(northbound.RideId, "offer.created");
            Assert.True(created.GetProperty("directionalMatched").GetBoolean());

            Assert.True(
                (await BreakdownAsync(fleet, northbound.RideId, driver.DriverId))
                    .GetProperty("directional").GetProperty("matched").GetBoolean());
        });

    /// <summary>
    /// <b>DT-06, both halves.</b> A directional driver who is the only nearby candidate does not
    /// hold the ride hostage — it runs down the ordinary deadline into <c>ExpiredNoDriver</c>,
    /// exactly as it would have if nobody had been there at all — and a second, unfiltered driver
    /// nearby still gets it.
    /// </summary>
    /// <remarks>
    /// ADD §247 verbatim: "If a directional driver is the *only* nearby candidate, the ride proceeds
    /// to the next ring / <c>ExpiredNoDriver</c> exactly as today — directional state never blocks a
    /// passenger's ride from matching some *other* available driver." The empty-pool half is the
    /// risk the feature carries; the other half is the reason it is acceptable.
    /// </remarks>
    [Fact]
    public Task An_empty_pool_expires_the_ride_and_an_unfiltered_driver_still_gets_one() =>
        RunAsync(async (fleet, rides) =>
        {
            // ---- the only candidate is filtered out ---------------------------------------------
            var (lonely, _) = ModeCFleet.NextPlaces();

            var filtered = await fleet.CreateOnlineDriverAsync(Near(lonely));
            await SetFilterAsync(fleet, filtered, North(lonely, DestinationOffset), "Home");

            var stranded = await fleet.BookAsync(
                await fleet.CreatePassengerAsync(), filtered, lonely, South(lonely, RideLength));

            rides.Add(stranded.RideId);

            await fleet.WaitForStateAsync(stranded.RideId, RideStates.Matching);

            await fleet.UntilAsync(
                stranded.RideId,
                async () => await HasTimeoutAsync(fleet, stranded.RideId),
                "dispatch-svc never armed the cascade deadline for a ride whose only candidate was filtered out");

            await fleet.PullForwardDispatchTimerAsync(stranded.RideId, "ride_timeout");

            var expired = await fleet.WaitForStateAsync(stranded.RideId, RideStates.ExpiredNoDriver);
            Assert.NotNull(expired.TerminalAt);

            // "Exactly as today": the same terminal, the same reason code, and no penalty on
            // anybody — the passenger is not charged for a feature they never opted into and the
            // driver is not marked down for using one they did.
            Assert.Contains("ride.expired_no_driver", await fleet.ReadEventsAsync(stranded.RideId));
            Assert.Empty(await fleet.ReadPenaltiesAsync(stranded.Passenger.Id));
            Assert.Null(await fleet.ReadOfferAsync(stranded.RideId));

            var filteredStill = await ReadFilterAsync(fleet, filtered);
            Assert.Null(filteredStill.ClearedAt);

            // ---- and a driver without a filter is unaffected --------------------------------------
            var (shared, _) = ModeCFleet.NextPlaces();

            var homebound = await fleet.CreateOnlineDriverAsync(Near(shared));
            await SetFilterAsync(fleet, homebound, North(shared, DestinationOffset), "Home");

            // Same rank, same distance, no filter.
            var available = await fleet.CreateOnlineDriverAsync(Near(shared));

            var southbound = await fleet.BookAsync(
                await fleet.CreatePassengerAsync(), available, shared, South(shared, RideLength));

            rides.Add(southbound.RideId);

            var offer = await fleet.WaitForOfferAsync(southbound.RideId);

            Assert.Equal(available.DriverId, offer.DriverId);
            Assert.Equal("directional", await RejectedByAsync(fleet, southbound.RideId, homebound.DriverId));

            // The passenger's ride went ahead, which is the whole of DT-06's second clause.
            using var accepted = await fleet.AcceptAsync(
                southbound.RideId, available, offer.Id, (await fleet.ReadRideAsync(southbound.RideId)).Version);

            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        });

    // -----------------------------------------------------------------------------------------

    private static async Task SetFilterAsync(ModeCFleet fleet, Driver driver, GeoPoint destination, string label)
    {
        using var response = await ModeCFleet.PostAsync(
            fleet.DispatchClient,
            "/v1/standby/directional",
            new { destination = new { lat = destination.Latitude, lng = destination.Longitude }, label },
            driver.Bearer);

        if (response.StatusCode != HttpStatusCode.Created)
        {
            Assert.Fail($"setting a Destination Filter answered {(int)response.StatusCode}: "
                + await response.Content.ReadAsStringAsync());
        }
    }

    private static GeoPoint North(GeoPoint from, double degrees) => new(from.Latitude + degrees, from.Longitude);

    private static GeoPoint South(GeoPoint from, double degrees) => new(from.Latitude - degrees, from.Longitude);

    private static async Task<string?> RejectedByAsync(ModeCFleet fleet, Guid rideId, Guid driverId)
    {
        await using var connection = await fleet.OpenAsync();

        return await connection.ExecuteScalarAsync<string?>(
            """
            SELECT breakdown->>'rejectedBy' FROM dispatch.candidate_scores
             WHERE ride_id = @RideId AND driver_id = @DriverId;
            """,
            new { RideId = rideId, DriverId = driverId });
    }

    private static async Task<JsonElement> BreakdownAsync(ModeCFleet fleet, Guid rideId, Guid driverId)
    {
        await using var connection = await fleet.OpenAsync();

        var breakdown = await connection.ExecuteScalarAsync<string>(
            """
            SELECT breakdown::text FROM dispatch.candidate_scores
             WHERE ride_id = @RideId AND driver_id = @DriverId;
            """,
            new { RideId = rideId, DriverId = driverId });

        Assert.True(breakdown is not null, $"no candidate_scores row for driver {driverId} on ride {rideId}");

        using var document = JsonDocument.Parse(breakdown!);
        return document.RootElement.Clone();
    }

    private static async Task<(DateTimeOffset? ClearedAt, string? ClearedReason)> ReadFilterAsync(
        ModeCFleet fleet, Driver driver)
    {
        await using var connection = await fleet.OpenAsync();

        return await connection.QuerySingleAsync<(DateTimeOffset?, string?)>(
            """
            SELECT cleared_at, cleared_reason FROM dispatch.directional_filters
             WHERE driver_id = @DriverId ORDER BY created_at DESC LIMIT 1;
            """,
            new { DriverId = driver.DriverId });
    }

    private static async Task<bool> HasTimeoutAsync(ModeCFleet fleet, Guid rideId)
    {
        await using var connection = await fleet.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)::int FROM dispatch.timers
             WHERE ride_id = @RideId AND kind = 'ride_timeout' AND fired_at IS NULL;
            """,
            new { RideId = rideId }) == 1;
    }
}
