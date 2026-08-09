using System.Net;
using MageRide.E2E.Infrastructure;
using MageRide.Shared.Caching;
using MageRide.TcpAdapter.Protocols;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// Epic 23's entitlement, end to end: the request, the grant, what a passenger can then <em>see</em>,
/// the unsubscribe, the revocation that reaches their socket, and the rejoin (US-4.9, US-23.1,
/// US-23.11, US-23.12, AL-23, AL-25, D-22, D-23).
/// </summary>
/// <remarks>
/// <para>
/// <b>"Visibility" is asserted on a passenger's own WebSocket, not in a table.</b> A Mode B van is
/// never on the public map — D6' §5.2 grants a geocell group Mode A unconditionally and entitled
/// Mode B only — so the van reaches exactly one audience: the <c>vehicle:{vehicleId}</c> group,
/// whose membership fanout-svc derives from <c>share:{userId}</c> and which no client can ask to
/// join. That makes the grant, the revocation and the rejoin observable as three things arriving,
/// stopping and arriving again on a socket, which is what the passenger's app actually experiences.
/// </para>
/// <para>
/// <b>And the positions are real.</b> The van has a tracker bound through US-13.12 and reports
/// through tcp-adapter, so a frame a scenario writes to a socket becomes a marker on a passenger's
/// map nine services later.
/// </para>
/// <para>
/// <b>The operator's half goes through the Fleet Portal's proxies</b> (C059), not through
/// subscription-svc directly: those routes add the organisation scope and forward the operator's own
/// bearer, so the queue an owner reads and the accept they press are the ones SCR-FP-011 renders.
/// </para>
/// </remarks>
[Collection<ModeAbCollection>]
[Trait("Category", "ModeAB")]
public sealed class ModeBEntitlementScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    : ModeAbScenario(postgres, redis, redpanda, emqx)
{
    /// <summary>The whole of Epic 23's lifecycle in one journey, because each step is the next one's setup.</summary>
    /// <remarks>
    /// <para>
    /// Split into five tests it would be five identical set-ups and one assertion each, and the
    /// interesting claims are all about the <em>transitions</em>: that visibility begins when the
    /// grant does, that it stops within D-22's budget rather than at the next cell crossing, and
    /// that a rejoin reuses the grant row while starting a new subscription.
    /// </para>
    /// <para>
    /// <b>An unsubscribed grant stays MUTED until the owner hard-deletes it (AL-25).</b>
    /// <c>ux_grant_active</c> is partial on <c>deleted_at</c> rather than on <c>status</c>, and that
    /// one index is what makes three requirements true at once: the roster keeps showing who left,
    /// the owner's delete is the only thing that frees the (vehicle, passenger) pair, and a rejoin
    /// reuses the row rather than colliding with it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_passenger_is_granted_a_van_sees_it_leaves_it_and_comes_back() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var van = await ArriveAsync(fleet, vehicles, mode: "B", vehicleType: "van");
            var passenger = await fleet.CreatePassengerAsync();

            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, van.Imei);
            await using var app = await LiveConnection.OpenAsync(fleet, passenger.Bearer);

            // The passenger opens the map where the van runs. A Mode B vehicle is not on it.
            await app.JoinAsync(van.Depot);

            var reported = await device.ReportAsync(van.Depot);
            await fleet.WaitForTelemetryAsync(van.VehicleId, new ReportedFix(van.Depot, reported));

            Assert.DoesNotContain(van.VehicleId, app.Positions);

            // US-4.9 — they ask the owner for access. AL-23 makes the request per vehicle and never
            // account-global: a driver with three vans works three queues.
            var requestId = await RequestAccessAsync(fleet, van, passenger);

            using (var queue = await ModeAbFleet.GetAsync(
                fleet.FleetClient,
                $"/v1/fleets/{van.Org.FleetId}/vehicles/{van.VehicleId}/requests?status=pending",
                van.Org.OwnerBearer))
            {
                await ModeAbFleet.AssertSuccessAsync(queue, "the owner reading their request queue");

                var waiting = (await ModeAbFleet.ReadJsonAsync(queue)).GetProperty("items").EnumerateArray().ToArray();

                Assert.Single(waiting);
                Assert.Equal(requestId.ToString(), waiting[0].GetProperty("requestId").GetString());
                Assert.Equal(passenger.Id.ToString(), waiting[0].GetProperty("passengerId").GetString());

                // The queue shows a masked number, not the passenger's own.
                var masked = waiting[0].GetProperty("passengerMobileMasked").GetString();

                Assert.NotNull(masked);
                Assert.NotEqual(passenger.Phone, masked);
            }

            // US-23.1 — the owner accepts, through the Fleet Portal's proxy.
            var grant = await AcceptAsync(fleet, van, requestId);

            // D-23: the entitlement SET fanout-svc builds from `share.granted` on `registry.events`.
            await fleet.UntilAsync(
                van.VehicleId,
                async () => await fleet.Cache.SetContainsAsync(
                    RedisKeys.Share(passenger.Id), van.VehicleId.ToString()),
                "the grant reaching the passenger's entitlement set");

            // And now the van appears — on the passenger's own socket, from a real GT06 frame.
            await app.JoinAsync(van.Depot);

            var moving = await DriveAsync(device, van.Depot, ModeAbFleet.MetresNorth(van.Depot, 80));
            await fleet.WaitForTelemetryAsync(van.VehicleId, moving);

            Assert.True(
                await app.SawVehicleAsync(van.VehicleId),
                "an entitled passenger was never shown the van they subscribe to (D-23).");

            // US-23.12 — the owner's roster, and SCR-PA-025's card list, agree that they are on it.
            Assert.Contains(passenger.Id, await SubscriberIdsAsync(fleet, van));
            Assert.Contains(grant.SubscriptionId, await SubscriptionIdsAsync(fleet, passenger));

            // US-23.11 / D-22 — the passenger leaves. The revocation is written inside the
            // transaction that mutes the grant, and BR-23.11 gives it 200 ms to reach the socket;
            // what is asserted here is that it arrives at all, on this connection, without the
            // passenger doing anything.
            app.Forget();

            using (var left = await ModeAbFleet.PostAsync(
                fleet.SubscriptionClient,
                $"/v1/mode-b/subscriptions/{grant.SubscriptionId}/unsubscribe",
                new { },
                passenger.Bearer))
            {
                await ModeAbFleet.AssertSuccessAsync(left, "the passenger unsubscribing");
            }

            Assert.True(
                await app.SawRevocationAsync(van.VehicleId),
                "the passenger was never told their Mode B grant had gone (D-22).");

            await fleet.UntilAsync(
                van.VehicleId,
                async () => !await fleet.Cache.SetContainsAsync(
                    RedisKeys.Share(passenger.Id), van.VehicleId.ToString()),
                "the revocation clearing the passenger's entitlement set");

            // The van keeps running and the ex-subscriber stops seeing it. `Forget` above is what
            // makes this an assertion about now rather than about the frames that arrived earlier.
            app.Forget();

            var afterwards = await DriveAsync(device, moving.At, ModeAbFleet.MetresNorth(moving.At, 80));
            await fleet.WaitForTelemetryAsync(van.VehicleId, afterwards);
            await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);

            Assert.DoesNotContain(van.VehicleId, app.Positions);

            // AL-25 — the roster still shows who left. The grant is MUTED, not gone: only the
            // owner's hard delete frees the (vehicle, passenger) pair.
            Assert.Contains(passenger.Id, await SubscriberIdsAsync(fleet, van));

            // US-23.11's other half — a rejoin needs a fresh request, and the accept reuses the
            // grant row while starting a *new* subscription (the old one is cancelled, and
            // `ux_subscriptions_grant_live` admits one live row per grant).
            var again = await RequestAccessAsync(fleet, van, passenger);

            Assert.NotEqual(requestId, again);

            var rejoined = await AcceptAsync(fleet, van, again);

            Assert.Equal(grant.GrantId, rejoined.GrantId);
            Assert.NotEqual(grant.SubscriptionId, rejoined.SubscriptionId);

            // One card, not two: the passenger's list filters on the grant being live *and* the
            // subscription not being cancelled, or every rejoin would leave a ghost beside the
            // new one.
            var cards = await SubscriptionIdsAsync(fleet, passenger);

            Assert.Equal([rejoined.SubscriptionId], cards);

            // And the van is back on their map.
            await app.JoinAsync(van.Depot);
            app.Forget();

            var back = await DriveAsync(device, afterwards.At, ModeAbFleet.MetresNorth(afterwards.At, 80));
            await fleet.WaitForTelemetryAsync(van.VehicleId, back);

            Assert.True(
                await app.SawVehicleAsync(van.VehicleId),
                "a rejoined passenger was not shown the van again.");
        });

    /// <summary>
    /// D-23's other half — a passenger nobody granted anything to sees nothing, however hard they
    /// look.
    /// </summary>
    /// <remarks>
    /// The complement of the scenario above, and the one that makes it mean something. There is no
    /// <c>SubscribeVehicle</c> on the hub at all: every membership of a <c>vehicle:{vehicleId}</c>
    /// group is derived from server-side state, so there is no request a client could make that the
    /// server would have to overrule. A stranger joining the geocells the van is standing in gets
    /// the Mode A traffic in them and nothing else.
    /// </remarks>
    [Fact]
    public async Task A_stranger_never_sees_a_Mode_B_van() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var van = await ArriveAsync(fleet, vehicles, mode: "B", vehicleType: "van");
            var stranger = await fleet.CreatePassengerAsync();

            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, van.Imei);
            await using var app = await LiveConnection.OpenAsync(fleet, stranger.Bearer);

            await app.JoinAsync(van.Depot);

            Assert.False(
                await fleet.Cache.KeyExistsAsync(RedisKeys.Share(stranger.Id)),
                "a passenger nobody granted anything to has an entitlement set");

            var moving = await DriveAsync(device, van.Depot, ModeAbFleet.MetresNorth(van.Depot, 120));
            await fleet.WaitForTelemetryAsync(van.VehicleId, moving);

            // Long enough for several of fanout's batch intervals to pass.
            await Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);

            Assert.DoesNotContain(van.VehicleId, app.Positions);
        });

    /// <summary>
    /// A Mode A bus is on the public map unconditionally, which is what the Mode B rule is measured
    /// against (D6' §5.2).
    /// </summary>
    /// <remarks>
    /// Without this the entitlement assertions would be satisfied by a fan-out that showed nobody
    /// anything. Same passenger, same socket, same cells — the only difference is the vehicle's
    /// mode.
    /// </remarks>
    [Fact]
    public async Task A_Mode_A_bus_is_on_the_public_map_for_everybody() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);
            var passenger = await fleet.CreatePassengerAsync();

            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, bus.Imei);
            await using var app = await LiveConnection.OpenAsync(fleet, passenger.Bearer);

            await app.JoinAsync(bus.Depot);

            var moving = await DriveAsync(device, bus.Depot, ModeAbFleet.MetresNorth(bus.Depot, 120));
            await fleet.WaitForTelemetryAsync(bus.VehicleId, moving);

            Assert.True(
                await app.SawVehicleAsync(bus.VehicleId),
                "a Mode A bus did not reach the public geocell group (D6' §5.2).");
        });

    // -------------------------------------------------------------------------------------------
    // The two Epic 23 calls every scenario here makes
    // -------------------------------------------------------------------------------------------

    private static async Task<Guid> RequestAccessAsync(
        ModeAbFleet fleet, TrackedVehicle van, ModeAbPassenger passenger)
    {
        using var response = await ModeAbFleet.PostAsync(
            fleet.SubscriptionClient,
            $"/v1/mode-b/{van.VehicleId}/access-requests",
            new { note = "School run, mornings" },
            passenger.Bearer);

        await ModeAbFleet.AssertSuccessAsync(response, "requesting Mode B access");

        return (await ModeAbFleet.ReadJsonAsync(response)).GetProperty("requestId").GetGuid();
    }

    private static async Task<ModeBGrant> AcceptAsync(ModeAbFleet fleet, TrackedVehicle van, Guid requestId)
    {
        using var response = await ModeAbFleet.PostAsync(
            fleet.FleetClient,
            $"/v1/fleets/{van.Org.FleetId}/vehicles/{van.VehicleId}/requests/{requestId}/accept",
            new { },
            van.Org.OwnerBearer);

        await ModeAbFleet.AssertSuccessAsync(response, "the owner accepting the request");

        var body = await ModeAbFleet.ReadJsonAsync(response);

        return new ModeBGrant(
            body.GetProperty("requestId").GetGuid(),
            body.GetProperty("grantId").GetGuid(),
            body.GetProperty("subscriptionId").GetGuid());
    }

    private static async Task<IReadOnlyList<Guid>> SubscriberIdsAsync(ModeAbFleet fleet, TrackedVehicle van)
    {
        using var response = await ModeAbFleet.GetAsync(
            fleet.FleetClient,
            $"/v1/fleets/{van.Org.FleetId}/vehicles/{van.VehicleId}/subscribers",
            van.Org.OwnerBearer);

        await ModeAbFleet.AssertSuccessAsync(response, "the owner reading the roster");

        return
        [
            .. (await ModeAbFleet.ReadJsonAsync(response)).GetProperty("items").EnumerateArray()
                .Select(row => row.GetProperty("passengerId").GetGuid()),
        ];
    }

    private static async Task<IReadOnlyList<Guid>> SubscriptionIdsAsync(
        ModeAbFleet fleet, ModeAbPassenger passenger)
    {
        using var response = await ModeAbFleet.GetAsync(
            fleet.SubscriptionClient, $"/v1/mode-b/subscriptions/{passenger.Id}", passenger.Bearer);

        await ModeAbFleet.AssertSuccessAsync(response, "the passenger reading their subscriptions");

        return
        [
            .. (await ModeAbFleet.ReadJsonAsync(response)).GetProperty("items").EnumerateArray()
                .Select(row => row.GetProperty("subscriptionId").GetGuid()),
        ];
    }
}
