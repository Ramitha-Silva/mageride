using MageRide.E2E.Infrastructure;
using MageRide.Ride.Domain;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// <b>R-15 and R-16 — a last will starts a clock; it does not cancel a ride.</b>
/// </summary>
/// <remarks>
/// <para>
/// The four windows exist because a driver who drives into an underpass has not abandoned anybody:
/// 60 s after accept, 120 s after arrive, 5 min in progress, 10 min at payment (D5' §6.3). What
/// each of them terminates in is a cell of the §11.12 matrix and is driven by
/// <see cref="CancellationMatrixScenario"/>; what is here is the behaviour *around* the clock,
/// which no matrix cell can express — that coming back retires it, and that moving the ride along
/// re-plans it rather than earning a fresh one.
/// </para>
/// <para>
/// Every one of these runs on the real broker. The vehicle registers its will at CONNECT, which is
/// the only reason <c>acl.conf</c> grants a device publish rights on its own status topic, and EMQX
/// is what publishes it.
/// </para>
/// </remarks>
[Collection<ModeCCollection>]
[Trait("Category", "ModeC")]
public sealed class OfflineGraceScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    : ModeCScenario(postgres, redis, redpanda, emqx)
{
    /// <summary>
    /// The window a last will opens is the one the ride's state calls for — all four of them,
    /// against a live broker.
    /// </summary>
    [Theory]
    [InlineData(RideStates.Accepted, 60)]
    [InlineData(RideStates.DriverArrived, 120)]
    [InlineData(RideStates.InProgress, 300)]
    [InlineData(RideStates.PaymentPending, 600)]
    public Task A_dropped_session_opens_the_window_R16_gives_that_state(string state, int seconds) =>
        RunAsync(async (fleet, rides) =>
        {
            var ride = await DriveToAsync(fleet, rides, state);

            await fleet.WaitForPresenceSubscriptionAsync();

            await using (var device = await DeviceSession.ConnectAsync(fleet.Broker, ride.Driver.VehicleId))
            {
                await device.DropAsync();
            }

            await fleet.UntilAsync(
                ride.RideId,
                async () => (await fleet.ReadRideTimersAsync(ride.RideId, "offline_grace")).Count == 1,
                $"the last will never armed an offline grace on a ride in {state}");

            await fleet.AssertTimerArmedAsync(ride.RideId, "offline_grace", TimeSpan.FromSeconds(seconds));

            // And the ride has not moved. That is the whole of "a last will starts a clock": the
            // driver still holds the ride, and the only thing that takes it away is the clock
            // running out.
            Assert.Equal(state, (await fleet.ReadRideAsync(ride.RideId)).State);
        });

    /// <summary>
    /// The flap. A vehicle that reconnects inside the window keeps its ride — which is the
    /// difference between a connection that wobbled and a driver who is gone.
    /// </summary>
    [Fact]
    public Task Coming_back_inside_the_grace_keeps_the_ride() => RunAsync(async (fleet, rides) =>
    {
        var ride = await DriveToAsync(fleet, rides, RideStates.Accepted);

        await fleet.WaitForPresenceSubscriptionAsync();

        await using (var device = await DeviceSession.ConnectAsync(fleet.Broker, ride.Driver.VehicleId))
        {
            await device.DropAsync();
        }

        await fleet.UntilAsync(
            ride.RideId,
            async () => (await fleet.ReadRideTimersAsync(ride.RideId, "offline_grace")).Count == 1,
            "the last will never armed an offline grace");

        // The device is back on the air, and says so on its own status topic.
        await DeviceSession.ComeBackAsync(fleet.Broker, ride.Driver.VehicleId);

        await fleet.UntilAsync(
            ride.RideId,
            async () => (await fleet.ReadRideTimersAsync(ride.RideId, "offline_grace")).Count == 0,
            "the grace was never retired by the vehicle coming back");

        // Nothing is due, so the sweep that has been running underneath this whole scenario has
        // nothing to fire — and the ride is still the driver's.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(RideStates.Accepted, (await fleet.ReadRideAsync(ride.RideId)).State);
        Assert.Equal(ride.Driver.DriverId, (await fleet.ReadRideAsync(ride.RideId)).AcceptedDriverId);

        // …and it can still be driven to the end.
        var driven = await fleet.AdvanceAsync(ride, "arrive");
        driven = await fleet.AdvanceAsync(driven, "start");
        driven = await fleet.AdvanceAsync(driven, "complete");

        Assert.Equal("PaymentPending", (await fleet.ReadRideAsync(driven.RideId)).State);
    });

    /// <summary>
    /// <b>Moving the ride along is not a way to earn a fresh grace.</b> A driver who goes dark while
    /// <c>Accepted</c> and then taps Arrive gets <c>DriverArrived</c>'s 120 seconds — computed from
    /// the instant they went away, not from the tap.
    /// </summary>
    /// <remarks>
    /// The re-plan is what makes the four windows survive a ride that moves underneath them, and it
    /// has to terminate: <c>offlineSince</c> is fixed for the outage and the windows are constants,
    /// so every re-arm computes an absolute deadline from the same instant. A grace re-armed
    /// relative to <em>now</em> would let a driver who is not there push the deadline out for as
    /// long as they kept pressing buttons.
    /// </remarks>
    [Fact]
    public Task A_ride_that_moves_while_the_driver_is_dark_gets_the_new_window_from_the_same_instant() =>
        RunAsync(async (fleet, rides) =>
        {
            var ride = await DriveToAsync(fleet, rides, RideStates.Accepted);

            await fleet.WaitForPresenceSubscriptionAsync();

            var wentDark = DateTimeOffset.UtcNow;

            await using (var device = await DeviceSession.ConnectAsync(fleet.Broker, ride.Driver.VehicleId))
            {
                await device.DropAsync();
            }

            await fleet.UntilAsync(
                ride.RideId,
                async () => (await fleet.ReadRideTimersAsync(ride.RideId, "offline_grace")).Count == 1,
                "the last will never armed an offline grace");

            await fleet.AssertTimerArmedAsync(ride.RideId, "offline_grace", TimeSpan.FromSeconds(60));

            // The driver taps Arrive from a phone with no broker session. Nothing about the HTTP
            // surface depends on MQTT, so this is an ordinary move — and the grace survives it,
            // because retiring it here would silently forgive a driver who is still off the air.
            ride = await fleet.AdvanceAsync(ride, "arrive");

            // Settle before asserting, the same shape as the wait above. `ReadRideTimersAsync`
            // returns only LIVE timers (`fired_at IS NULL`), and there is a window in which two
            // are: the R-04 worker can reach the Accepted-window row, fire it and arm its
            // replacement while this assertion is being made. Sampling once inside that window
            // read two rows and failed on CI — 2026-08-15, `main` @ 92d7b50, the collection was
            // the carried row plus a re-planned one at `offlineSince + 120 s`.
            //
            // This is a settle, not a softening: a ride that ends up with two live offline_grace
            // timers still fails, on this wait's own timeout, and the `Single` below still has to
            // hold at the end of it. What it no longer does is fail for observing a transition
            // mid-flight.
            await fleet.UntilAsync(
                ride.RideId,
                async () => (await fleet.ReadRideTimersAsync(ride.RideId, "offline_grace")).Count == 1,
                "the arrival left more than one live offline grace");

            var carried = Assert.Single(await fleet.ReadRideTimersAsync(ride.RideId, "offline_grace"));
            Assert.True(
                carried.FireAt < wentDark.AddSeconds(75),
                "the Accepted window was extended by the arrival instead of being carried into it");

            // Bringing the 60-second deadline forward makes the worker look at it, and what it finds
            // is a ride that has moved: it re-plans to `offlineSince + 120 s` and fires nothing.
            await fleet.PullForwardRideTimerAsync(ride.RideId, "offline_grace");

            await fleet.UntilAsync(
                ride.RideId,
                async () => (await fleet.ReadRideTimersAsync(ride.RideId, "offline_grace"))
                    .Any(timer => timer.Id != carried.Id),
                "the grace was never re-planned for the state the ride had moved to");

            var replanned = Assert.Single(await fleet.ReadRideTimersAsync(ride.RideId, "offline_grace"));

            // From the same instant, not from the tap: 120 s after the vehicle went away.
            Assert.True(
                (replanned.FireAt - wentDark.AddSeconds(120)).Duration() < TimeSpan.FromSeconds(20),
                $"the re-planned grace is due at {replanned.FireAt:O}, which is not 120 s after the vehicle "
                + $"went dark at {wentDark:O}.");

            Assert.Equal(RideStates.DriverArrived, (await fleet.ReadRideAsync(ride.RideId)).State);

            // And when *that* one runs out, the §11.12 row for DriverArrived applies.
            await fleet.PullForwardRideTimerAsync(ride.RideId, "offline_grace");

            var terminated = await fleet.WaitForStateAsync(ride.RideId, RideStates.CancelledByDriver);
            Assert.NotNull(terminated.TerminalAt);

            Assert.Contains("reputation.driver_cancelled", await fleet.ReadEventsAsync(ride.RideId));
        });
}
