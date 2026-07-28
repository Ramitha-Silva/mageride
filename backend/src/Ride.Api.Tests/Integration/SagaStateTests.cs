using System.Net;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// <c>GET /v1/internal/rides/{rideId}/saga-state</c> — the diagnostics ADD §13.4's
/// <c>runbooks/ride-stuck.md</c> tells an operator to read.
/// </summary>
/// <remarks>
/// "Inspect last <c>rides.transitions</c>, check driver MQTT session, manual force-transition only
/// with admin approval (always via <c>admin-bff</c>, never raw SQL)". This route is what makes the
/// first of those possible without the last: everything the runbook asks for, over HTTP, on the
/// internal plane.
/// </remarks>
[Collection<RideCollection>]
public sealed class SagaStateTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_saga_state_carries_the_whole_transition_log()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("PaymentPending");

        var response = await harness.GetAsync(
            $"/v1/internal/rides/{ride.RideId}/saga-state", bearer: null, apiKey: RideHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await RideHarness.ReadJsonAsync(response);

        Assert.Equal(ride.RideId, body.GetProperty("rideId").GetGuid());
        Assert.Equal("PaymentPending", body.GetProperty("state").GetString());
        Assert.Equal(ride.Version, body.GetProperty("version").GetInt64());

        var transitions = body.GetProperty("transitions").EnumerateArray().ToArray();

        // The whole walk, oldest first — including the automatic Completed → PaymentPending, which
        // is the pair a support engineer needs to see to tell "the driver never finished" from
        // "fare-svc never answered".
        Assert.Equal(
            new[] { "Requested", "Matching", "Offered", "Accepted", "DriverArrived", "InProgress", "Completed", "PaymentPending" },
            transitions.Select(t => t.GetProperty("to").GetString() ?? string.Empty).ToArray());

        Assert.False(transitions[0].TryGetProperty("from", out _));
        Assert.Equal("rider", transitions[0].GetProperty("actor").GetString());
        Assert.Equal("Completed", transitions[^1].GetProperty("from").GetString());
        Assert.Equal("system", transitions[^1].GetProperty("actor").GetString());
        Assert.Equal("FARE_HANDOFF", transitions[^1].GetProperty("reason").GetString());

        Assert.True(transitions[0].GetProperty("at").GetDateTimeOffset() <= transitions[^1].GetProperty("at").GetDateTimeOffset());

        // The dispatcher is off in tests, so everything this ride wrote is still waiting — which is
        // exactly the reading that tells an operator the outbox drain has stopped (ADD §13.4).
        Assert.Equal(6, body.GetProperty("pendingOutbox").GetInt32());
    }

    /// <summary>The reason code is what says *why* a stuck ride ended up where it is.</summary>
    [Fact]
    public async Task A_cancelled_ride_shows_the_matrix_reason_it_ended_on()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Accepted");

        var cancelled = await harness.PostAsync(
            $"/v1/rides/{ride.RideId}/cancel",
            new { version = ride.Version, reason = "EMERGENCY" },
            ride.PassengerBearer);

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var response = await harness.GetAsync(
            $"/v1/internal/rides/{ride.RideId}/saga-state", bearer: null, apiKey: RideHarness.InternalApiKey);

        var body = await RideHarness.ReadJsonAsync(response);
        var last = body.GetProperty("transitions").EnumerateArray().Last();

        Assert.Equal("CancelledByRiderAfterAccept", last.GetProperty("to").GetString());

        // The server-owned reason, not the client's "EMERGENCY" — the matrix decided this.
        Assert.Equal("RIDER_CANCELLED_AFTER_ACCEPT", last.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task An_unknown_ride_is_not_found()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var response = await harness.GetAsync(
            $"/v1/internal/rides/{Guid.NewGuid()}/saga-state", bearer: null, apiKey: RideHarness.InternalApiKey);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "not-found");
    }

    /// <summary>
    /// Diagnostics are still the internal plane: the log names both parties and every move they
    /// made, which is not a thing a passenger's token gets to read about somebody else's ride.
    /// </summary>
    [Fact]
    public async Task The_diagnostics_are_invisible_without_the_internal_key()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Accepted");

        // Even to a party to the ride, holding a valid bearer.
        var response = await harness.GetAsync(
            $"/v1/internal/rides/{ride.RideId}/saga-state", ride.PassengerBearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "not-found");
    }
}
