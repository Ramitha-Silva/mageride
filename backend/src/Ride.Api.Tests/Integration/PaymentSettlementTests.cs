using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// <b>DoD 4 — "driver earning is never posted before the payment reaches a terminal state (R-05)".</b>
/// </summary>
/// <remarks>
/// ride-svc posts no earning itself — billing does, off <c>ride.settled</c> — so the guarantee this
/// service can make is the one the event carries: the ride reaches a settled state through exactly
/// one door, that door is fare-svc reporting a terminal <c>PaymentState</c>, and
/// <c>earningPayable</c> is true for precisely the three terminals D5' §8.1 names.
/// </remarks>
[Collection<RideCollection>]
public sealed class PaymentSettlementTests(PostgresFixture postgres)
{
    /// <summary>
    /// D5' §8.1: "driver earning posts only on terminal Paid / CashSettled /
    /// CashOnDeliveryCollected (R-05)".
    /// </summary>
    [Theory]
    [InlineData("Succeeded", "Paid", true)]
    [InlineData("FellBackToCash", "CashSettled", true)]
    [InlineData("CashOnDeliveryCollected", "CashOnDeliveryCollected", true)]
    // Disputed is a terminal of the *ride*, not of the money: §11.12 sends it to manual review, so
    // nothing is payable until somebody has looked.
    [InlineData("Disputed", "Disputed", false)]
    public async Task A_terminal_payment_settles_the_ride(string paymentState, string rideState, bool payable)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("PaymentPending");
        var paymentId = Guid.NewGuid();

        var settled = await SettleAsync(harness, ride.RideId, paymentState, paymentId, 74_000);

        Assert.Equal(rideState, settled.GetProperty("state").GetString());
        Assert.Equal(rideState, (await harness.ReadRideAsync(ride.RideId)).State);

        var payload = (await harness.ReadEventPayloadAsync(ride.RideId, "ride.settled")).GetProperty("payload");

        Assert.Equal(payable, payload.GetProperty("earningPayable").GetBoolean());
        Assert.Equal(paymentId, payload.GetProperty("paymentId").GetGuid());
        Assert.Equal(paymentState, payload.GetProperty("paymentState").GetString());
        Assert.Equal(ride.Driver.DriverId, payload.GetProperty("driverId").GetGuid());
        Assert.Equal(74_000, payload.GetProperty("settledMinor").GetInt64());

        await using var connection = await harness.OpenAsync();

        // Terminal, audited and no longer watched.
        Assert.NotNull(await connection.ExecuteScalarAsync<DateTimeOffset?>(
            "SELECT terminal_at FROM rides.rides WHERE id = @RideId;", new { RideId = ride.RideId }));

        Assert.Equal(rideState, await connection.ExecuteScalarAsync<string>(
            "SELECT to_state FROM rides.transitions WHERE ride_id = @RideId ORDER BY ts DESC, id DESC LIMIT 1;",
            new { RideId = ride.RideId }));

        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM rides.timers WHERE ride_id = @RideId AND fired_at IS NULL;",
            new { RideId = ride.RideId }));
    }

    /// <summary>
    /// <b>The heart of R-05.</b> A payment still in flight moves nothing, so there is no moment at
    /// which the ride looks settled and an earning could post against it.
    /// </summary>
    [Theory]
    [InlineData("Initiated")]
    [InlineData("Pending")]
    [InlineData("Failed")]
    [InlineData("Retried")]
    [InlineData("CashOnDelivery")]
    [InlineData("QrClaimedByPassenger")]
    [InlineData("DriverConfirmedQR")]
    public async Task A_payment_still_in_flight_settles_nothing(string paymentState)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("PaymentPending");

        var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{ride.RideId}/payment-settled",
            new { paymentId = Guid.NewGuid().ToString(), paymentState });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "illegal-transition");

        Assert.Equal("PaymentPending", (await harness.ReadRideAsync(ride.RideId)).State);
        Assert.DoesNotContain("ride.settled", await harness.ReadEventsAsync(ride.RideId));
    }

    /// <summary>
    /// §11.14 says it outright for the late-callback case: "UPDATE rides SET state='Disputed' is
    /// NOT done". <c>Overpaid</c> and <c>Refunded</c> happen to a ride that has already settled and
    /// must not drag it backwards.
    /// </summary>
    [Theory]
    [InlineData("Overpaid")]
    [InlineData("Refunded")]
    [InlineData("PartiallyRefunded")]
    public async Task A_post_settlement_correction_does_not_move_the_ride(string paymentState)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("PaymentPending");
        await SettleAsync(harness, ride.RideId, "FellBackToCash", Guid.NewGuid(), 74_000);

        var late = await harness.PostInternalAsync(
            $"/v1/internal/rides/{ride.RideId}/payment-settled",
            new { paymentId = Guid.NewGuid().ToString(), paymentState });

        await ProblemDocument.AssertAsync(late, HttpStatusCode.BadRequest, "illegal-transition");
        Assert.Equal("CashSettled", (await harness.ReadRideAsync(ride.RideId)).State);
    }

    /// <summary>
    /// Only a ride that has actually been driven settles. A ride still in progress reaching a
    /// terminal money state would post an earning for a trip that never finished.
    /// </summary>
    [Theory]
    [InlineData("Requested")]
    [InlineData("Accepted")]
    [InlineData("InProgress")]
    public async Task Only_a_ride_awaiting_payment_can_be_settled(string from)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync(from);

        var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{ride.RideId}/payment-settled",
            new { paymentId = Guid.NewGuid().ToString(), paymentState = "Succeeded" });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "illegal-transition");
        Assert.Equal(from, (await harness.ReadRideAsync(ride.RideId)).State);
    }

    /// <summary>
    /// fare-svc's delivery is at least once and R-14's replay only covers an identical
    /// <c>Idempotency-Key</c>, so the same terminal arriving again under a fresh key is answered
    /// with the settled ride — and writes no second transition and no second earning authorisation.
    /// </summary>
    [Fact]
    public async Task A_redelivered_settlement_is_answered_without_settling_twice()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("PaymentPending");
        var paymentId = Guid.NewGuid();

        var first = await SettleAsync(harness, ride.RideId, "Succeeded", paymentId, 74_000);
        var second = await SettleAsync(harness, ride.RideId, "Succeeded", paymentId, 74_000);

        Assert.Equal("Paid", second.GetProperty("state").GetString());
        Assert.Equal(first.GetProperty("version").GetInt64(), second.GetProperty("version").GetInt64());

        await using var connection = await harness.OpenAsync();

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM rides.outbox WHERE aggregate_id = @RideId AND event_type = 'ride.settled';",
            new { RideId = ride.RideId }));

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM rides.transitions WHERE ride_id = @RideId AND to_state = 'Paid';",
            new { RideId = ride.RideId }));
    }

    /// <summary>
    /// A ride settled as <c>CashSettled</c> cannot then be reported <c>Paid</c>: two terminals
    /// would be two earnings for one trip.
    /// </summary>
    [Fact]
    public async Task A_ride_cannot_be_settled_into_a_second_terminal()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("PaymentPending");

        // The passenger paid the driver in cash after the provider failed (D5' §8.1's
        // `FellBackToCash`), which lands the ride in CashSettled.
        await SettleAsync(harness, ride.RideId, "FellBackToCash", Guid.NewGuid(), 74_000);

        var again = await harness.PostInternalAsync(
            $"/v1/internal/rides/{ride.RideId}/payment-settled",
            new { paymentId = Guid.NewGuid().ToString(), paymentState = "Succeeded" });

        await ProblemDocument.AssertAsync(again, HttpStatusCode.BadRequest, "illegal-transition");
        Assert.Equal("CashSettled", (await harness.ReadRideAsync(ride.RideId)).State);
    }

    [Fact]
    public async Task An_unknown_ride_is_not_found()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{Guid.NewGuid()}/payment-settled",
            new { paymentId = Guid.NewGuid().ToString(), paymentState = "Succeeded" });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task A_settlement_without_a_payment_id_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("PaymentPending");

        var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{ride.RideId}/payment-settled", new { paymentState = "Succeeded" });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal("PaymentPending", (await harness.ReadRideAsync(ride.RideId)).State);
    }

    /// <summary>
    /// The settlement plane is mTLS-only in the contract and shared-secret-guarded until C042. An
    /// unauthenticated caller must not be able to mark a ride paid.
    /// </summary>
    [Fact]
    public async Task The_settlement_route_is_invisible_without_the_internal_key()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("PaymentPending");

        var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{ride.RideId}/payment-settled",
            new { paymentId = Guid.NewGuid().ToString(), paymentState = "Succeeded" },
            apiKey: null);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "not-found");
        Assert.Equal("PaymentPending", (await harness.ReadRideAsync(ride.RideId)).State);
    }

    private static async Task<JsonElement> SettleAsync(
        RideHarness harness, Guid rideId, string paymentState, Guid paymentId, long settledMinor)
    {
        var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{rideId}/payment-settled",
            new { paymentId = paymentId.ToString(), paymentState, settledMinor });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await RideHarness.ReadJsonAsync(response);
    }
}
