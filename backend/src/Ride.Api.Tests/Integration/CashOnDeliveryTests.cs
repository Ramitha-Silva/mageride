using System.Net;
using MageRide.Ride.Domain;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// Cash on delivery, both ways it can end: the driver banks it (P-08) or twenty-four hours pass and
/// it becomes a dispute (P-14, D5' §8.3).
/// </summary>
[Collection<RideCollection>]
public sealed class CashOnDeliveryTests(PostgresFixture postgres)
{
    /// <summary>
    /// P-08: "driver taps 'Cash received' → <c>CashOnDeliveryCollected</c> → earning posts". D5' §6
    /// draws it as an edge of the ride machine, which is why the driver's confirmation — and not a
    /// gateway callback — is what settles it.
    /// </summary>
    [Fact]
    public async Task The_driver_banking_the_cash_settles_the_ride_and_authorises_the_earning()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var package = await harness.DrivePackageToAsync("Delivered", paymentMethod: "cod");

        var (delivered, _) = await harness.ReadRideAsync(package.RideId);
        Assert.Equal("PaymentPending", delivered);

        // Armed at the pickup, not at the delivery: the clock is about money in transit, and the
        // delivery may itself be what never happens (ADD §11.16).
        Assert.Equal(1, await harness.CountLiveTimersAsync(package.RideId, RideTimerKinds.CodUncollected));

        var response = await harness.PostAsync(
            $"/v1/rides/{package.RideId}/cod-collected", new { collectedMinor = 74_000 }, package.Driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "CashOnDeliveryCollected",
            (await RideHarness.ReadJsonAsync(response)).GetProperty("state").GetString());

        var (settled, _) = await harness.ReadRideAsync(package.RideId);
        Assert.Equal("CashOnDeliveryCollected", settled);

        // The P-08 event, and the R-05 authorisation shape every other terminal carries.
        var events = await harness.ReadEventsAsync(package.RideId);
        Assert.Contains("payment.cod_collected", events);
        Assert.Contains("ride.settled", events);

        var settlement = (await harness.ReadEventPayloadAsync(package.RideId, "ride.settled")).GetProperty("payload");
        Assert.True(settlement.GetProperty("earningPayable").GetBoolean());
        Assert.Equal(74_000, settlement.GetProperty("settledMinor").GetInt64());
        Assert.Equal(package.Driver.DriverId, settlement.GetProperty("driverId").GetGuid());

        // Terminal, so the P-14 window is closed with it — the tap and the clock race, and whichever
        // lands first leaves the other with nothing to do.
        Assert.Equal(0, await harness.CountLiveTimersAsync(package.RideId, RideTimerKinds.CodUncollected));

        // A redelivered confirmation posts no second earning.
        var again = await harness.PostAsync(
            $"/v1/rides/{package.RideId}/cod-collected", new { collectedMinor = 74_000 }, package.Driver.Bearer);

        await ProblemDocument.AssertAsync(again, HttpStatusCode.Conflict, "payment-already-settled");
        Assert.Single(events, name => name == "ride.settled");
    }

    /// <summary>
    /// P-14: "if COD not collected within 24 h … ride moves to <c>Disputed</c> and falls into the
    /// existing refund/dispute workflow (§11.14); no new pipeline".
    /// </summary>
    [Fact]
    public async Task An_uncollected_cash_delivery_becomes_a_dispute_when_the_window_closes()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var package = await harness.DrivePackageToAsync("Delivered", paymentMethod: "cod");

        // The window is 24 hours; bringing the durable row forward is how a test reaches the far
        // side of it without waiting a day. The row is what decides, which is R-04's whole point.
        await harness.DueTimerAsync(package.RideId, RideTimerKinds.CodUncollected);

        var sweep = await harness.SweepTimersAsync();
        Assert.Equal(1, sweep.Applied);

        var (state, _) = await harness.ReadRideAsync(package.RideId);
        Assert.Equal("Disputed", state);

        Assert.Contains("ride.disputed", await harness.ReadEventsAsync(package.RideId));

        // No penalty rides along: the money owed is the *fare*, which the dispute resolves. Billing
        // the passenger on top would be charging them for a button the driver did not press.
        Assert.DoesNotContain("cancellation.penalty.accrued", await harness.ReadEventsAsync(package.RideId));

        var audit = await harness.ReadTransitionsAsync(package.RideId);
        Assert.Contains(audit, row => row.ReasonCode == "COD_UNCOLLECTED" && row.ToState == "Disputed");

        // Too late to bank it: the ride is terminal and the driver's earning is an operator's call.
        var late = await harness.PostAsync(
            $"/v1/rides/{package.RideId}/cod-collected", new { collectedMinor = 74_000 }, package.Driver.Bearer);

        await ProblemDocument.AssertAsync(late, HttpStatusCode.Conflict, "payment-already-settled");
    }

    /// <summary>A parcel paid for any other way arms no window at all.</summary>
    [Fact]
    public async Task A_package_that_is_not_cash_on_delivery_arms_no_window()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var package = await harness.DrivePackageToAsync("InProgress");

        Assert.Equal(0, await harness.CountLiveTimersAsync(package.RideId, RideTimerKinds.CodUncollected));

        var response = await harness.PostAsync(
            $"/v1/rides/{package.RideId}/cod-collected", new { collectedMinor = 1_000 }, package.Driver.Bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.PaymentRequired, "payment-method-invalid");
    }

    /// <summary>`cod` is package-only, exactly as D3' spells it.</summary>
    [Fact]
    public async Task Cash_on_delivery_cannot_be_chosen_for_a_passenger_ride()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = await harness.CreateUserAsync();

        var response = await harness.PostAsync(
            "/v1/rides/request",
            new
            {
                clientRequestId = Guid.NewGuid().ToString(),
                pickup = new { lat = RideHarness.Pickup.Latitude, lng = RideHarness.Pickup.Longitude },
                dropoff = new { lat = RideHarness.Dropoff.Latitude, lng = RideHarness.Dropoff.Longitude },
                vehicleType = "three_wheeler",
                fareEstimateToken = harness.IssueFareToken(),
                paymentMethod = "cod",
            },
            passenger.Bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.PaymentRequired, "payment-method-invalid");
    }

    [Fact]
    public async Task A_negative_amount_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var package = await harness.DrivePackageToAsync("Delivered", paymentMethod: "cod");

        var response = await harness.PostAsync(
            $"/v1/rides/{package.RideId}/cod-collected", new { collectedMinor = -1 }, package.Driver.Bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "invalid-amount");
    }
}
