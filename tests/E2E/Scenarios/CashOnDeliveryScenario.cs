using System.Net;
using MageRide.E2E.Infrastructure;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// <b>Cash on delivery, and what happens when it never arrives (P-08, P-14).</b>
/// </summary>
/// <remarks>
/// <para>
/// COD is the one settlement no service can observe. The three gateway terminals are states fare-svc
/// <em>watches</em>; cash in a driver's hand is visible to nobody, so D5' §6 draws
/// <c>PaymentPending → CashOnDeliveryCollected</c> as an edge of the <em>ride</em> machine and the
/// driver's tap is the settlement. P-14 is what happens when the tap does not come: a durable clock
/// armed at the pickup — because the money is in transit from that moment, and the delivery may
/// itself be what never happens — and a §11.12 matrix row that lands the ride in <c>Disputed</c>.
/// </para>
/// <para>
/// <b>Both are driven by the platform's own workers.</b> The tap goes through ride-svc's route; the
/// expiry is ride-svc's R-04 sweep firing a timer whose 24-hour window is asserted before anything
/// brings it forward.
/// </para>
/// <para>
/// ADD §11.16, D5' §8.3, D5' §6, AL-33.
/// </para>
/// </remarks>
[Collection<ProxyPackageCollection>]
[Trait("Category", "ProxyPackage")]
public sealed class CashOnDeliveryScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
    : ProxyPackageScenario(postgres, redis, redpanda)
{
    /// <summary>The driver hands the parcel over, takes the cash, and taps to say so.</summary>
    [Fact]
    public Task A_driver_who_banks_the_cash_settles_the_delivery() =>
        RunAsync(async (fleet, rides) =>
        {
            var ride = await AcceptedPackageAsync(
                fleet, rides, ProxyPackageFleet.UnregisteredPhone(), paymentMethod: "cod");

            // No clock yet: nothing is owed while the parcel is still on the sender's doorstep.
            Assert.Empty(await fleet.ReadRideTimersAsync(ride.RideId, "cod_uncollected"));

            using (var picked = await fleet.PickupOtpAsync(ride, ride.PickupOtp))
            {
                Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
            }

            // **Armed at the pickup, at the window D5' §8.3 gives.** The clock is about money in
            // transit, so it starts when the driver takes the parcel — not when they deliver it.
            await fleet.AssertTimerArmedAsync(ride.RideId, "cod_uncollected", ProxyPackageFleet.CodUncollectedGrace);

            var handover = await fleet.ReadEventPayloadAsync(ride.RideId, "package.picked_up");
            var deliveryOtp = handover.GetProperty("payload").GetProperty("deliveryOtp").GetString();

            using (var delivered = await fleet.DeliveryOtpAsync(ride, deliveryOtp))
            {
                Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
            }

            // Delivered is not paid. AL-33 decoupled the two sheets deliberately: "Delivery
            // completed" replaced "Cash received", and the money is reconciled separately.
            Assert.Equal("PaymentPending", (await fleet.ReadRideAsync(ride.RideId)).State);

            // The window survived the delivery — every lifecycle move retires the lifecycle timers
            // and this one is not among them, which is what makes P-14 a clock about the cash rather
            // than about the parcel.
            await fleet.AssertTimerArmedAsync(ride.RideId, "cod_uncollected", ProxyPackageFleet.CodUncollectedGrace);

            // ---- the tap -----------------------------------------------------------------------
            using (var banked = await fleet.CodCollectedAsync(ride, 45_000))
            {
                Assert.Equal(HttpStatusCode.OK, banked.StatusCode);
                Assert.Equal(
                    "CashOnDeliveryCollected",
                    (await ProxyPackageFleet.ReadJsonAsync(banked)).GetProperty("state").GetString());
            }

            var settled = await fleet.ReadRideAsync(ride.RideId);
            Assert.Equal("CashOnDeliveryCollected", settled.State);
            Assert.NotNull(settled.TerminalAt);

            // A terminal retires every timer this service owns, the P-14 window included: the tap
            // and the clock race, and whichever lands first leaves the other nothing to do.
            Assert.Empty(await fleet.ReadRideTimersAsync(ride.RideId, "cod_uncollected"));

            // P-08's own event *and* the ordinary settlement, so every terminal reaches a consumer
            // in one payload shape rather than in two.
            var events = await fleet.ReadEventsAsync(ride.RideId);
            Assert.Contains("payment.cod_collected", events);
            Assert.Contains("ride.settled", events);

            var authorisation = await fleet.ReadEventPayloadAsync(ride.RideId, "ride.settled");
            var payload = authorisation.GetProperty("payload");

            Assert.True(payload.GetProperty("earningPayable").GetBoolean());
            Assert.Equal(45_000, payload.GetProperty("settledMinor").GetInt64());
            Assert.Equal("CashOnDeliveryCollected", payload.GetProperty("paymentState").GetString());

            // A redelivered tap must not post a second earning.
            using var twice = await fleet.CodCollectedAsync(ride, 45_000);
            Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        });

    /// <summary>
    /// <b>P-14: twenty-four hours with nobody banking the cash, and the ride is Disputed.</b>
    /// </summary>
    /// <remarks>
    /// The window is asserted off the running service's own row before it is brought forward, which
    /// is what keeps the shortcut honest: what is moved is the platform's record of <em>when</em> the
    /// money went into transit, and what fires is the real R-04 sweep resolving the real §11.12 cell.
    /// </remarks>
    [Fact]
    public Task Cash_nobody_collects_within_a_day_puts_the_delivery_into_dispute() =>
        RunAsync(async (fleet, rides) =>
        {
            var ride = await AcceptedPackageAsync(
                fleet, rides, ProxyPackageFleet.UnregisteredPhone(), paymentMethod: "cod");

            using (var picked = await fleet.PickupOtpAsync(ride, ride.PickupOtp))
            {
                Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
            }

            var handover = await fleet.ReadEventPayloadAsync(ride.RideId, "package.picked_up");

            using (var delivered = await fleet.DeliveryOtpAsync(
                ride, handover.GetProperty("payload").GetProperty("deliveryOtp").GetString()))
            {
                Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
            }

            Assert.Equal("PaymentPending", (await fleet.ReadRideAsync(ride.RideId)).State);

            // D5' §8.3's twenty-four hours, off the row, before the clock moves.
            await fleet.AssertTimerArmedAsync(ride.RideId, "cod_uncollected", ProxyPackageFleet.CodUncollectedGrace);
            await fleet.PullForwardRideTimerAsync(ride.RideId, "cod_uncollected");

            // Nothing here cancelled anything. ride-svc's sweep claimed the row and resolved
            // (PaymentPending × CodUncollected) against the matrix, which is the only place the
            // target state is written down.
            var disputed = await fleet.WaitForStateAsync(ride.RideId, "Disputed");
            Assert.NotNull(disputed.TerminalAt);

            Assert.Contains("ride.disputed", await fleet.ReadEventsAsync(ride.RideId));

            var raised = await fleet.ReadEventPayloadAsync(ride.RideId, "ride.disputed");
            Assert.Equal("COD_UNCOLLECTED", raised.GetProperty("payload").GetProperty("reasonCode").GetString());

            // **No penalty accrued.** The money owed is the fare, which the dispute resolves;
            // charging something on top of it would bill the sender for the driver not having
            // pressed a button.
            Assert.DoesNotContain("cancellation.penalty.accrued", await fleet.ReadEventsAsync(ride.RideId));

            // And the tap is too late — a driver cannot bank cash against a ride that has already
            // been disputed, because that terminal is somebody's to resolve.
            using var late = await fleet.CodCollectedAsync(ride, 45_000);
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        });

    /// <summary>
    /// The clock fires against a delivery that never happened, and correctly does nothing.
    /// </summary>
    /// <remarks>
    /// A ride still <c>InProgress</c> a day after its pickup is a stuck delivery, not an uncollected
    /// debt: no money is owed until the parcel is handed over. §11.12 gives
    /// <c>CodUncollected</c> exactly one cell — <c>PaymentPending</c> — so the timer finds no row and
    /// retires, which is what <c>TryApplyAsync</c> is for. The failure this guards against is a sweep
    /// that disputed a ride whose driver was merely late.
    /// </remarks>
    [Fact]
    public Task A_parcel_still_in_transit_after_a_day_is_not_disputed() =>
        RunAsync(async (fleet, rides) =>
        {
            var ride = await AcceptedPackageAsync(
                fleet, rides, ProxyPackageFleet.UnregisteredPhone(), paymentMethod: "cod");

            using (var picked = await fleet.PickupOtpAsync(ride, ride.PickupOtp))
            {
                Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
            }

            Assert.Equal("InProgress", (await fleet.ReadRideAsync(ride.RideId)).State);

            await fleet.AssertTimerArmedAsync(ride.RideId, "cod_uncollected", ProxyPackageFleet.CodUncollectedGrace);
            await fleet.PullForwardRideTimerAsync(ride.RideId, "cod_uncollected");

            // The sweep fires and the ride does not move. Waiting on the timer being marked fired is
            // what makes this an assertion rather than a race — without it, "still InProgress" would
            // also be true of a sweep that had not run yet.
            await fleet.UntilAsync(
                ride.RideId,
                async () => (await fleet.ReadRideTimersAsync(ride.RideId, "cod_uncollected")).Count == 0,
                "ride-svc's sweep never claimed the cod_uncollected timer");

            Assert.Equal("InProgress", (await fleet.ReadRideAsync(ride.RideId)).State);
            Assert.DoesNotContain("ride.disputed", await fleet.ReadEventsAsync(ride.RideId));

            // The delivery still completes normally afterwards: the fired clock consumed nothing.
            var handover = await fleet.ReadEventPayloadAsync(ride.RideId, "package.picked_up");

            using var delivered = await fleet.DeliveryOtpAsync(
                ride, handover.GetProperty("payload").GetProperty("deliveryOtp").GetString());

            Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
            Assert.Equal("PaymentPending", (await fleet.ReadRideAsync(ride.RideId)).State);
        });
}
