using System.Net;
using MageRide.Shared.Primitives;
using MageRide.Subscriptions.Endpoints;

namespace MageRide.Subscriptions.Tests.Infrastructure;

/// <summary>A fleet-owned Mode B vehicle with an owner who can be authenticated as one.</summary>
internal sealed record ModeBFleet(Guid OwnerId, string OwnerBearer, Guid FleetId, SeededVehicle Vehicle)
{
    public Guid VehicleId => Vehicle.Id;
}

/// <summary>
/// The Epic 23 set-up every integration test in this suite starts from, and the request → accept
/// walk it repeats.
/// </summary>
/// <remarks>
/// Written against SQL and this service's own HTTP surface rather than fleet-svc's (C059, which does
/// not exist) or registry-svc's onboarding: standing those up would test other components and make
/// this suite fail for reasons that are not C048's. The rows written are exactly the ones those
/// services would leave behind.
/// </remarks>
internal static class ModeBScenario
{
    /// <summary>An APPROVED Mode B vehicle on an APPROVED fleet whose owner is on the roster.</summary>
    public static async Task<ModeBFleet> FleetAsync(
        SubscriptionHarness harness,
        string? billing = "paid",
        long? defaultFareMinor = 250_000,
        string? payoutProfileStatus = "verified",
        Guid? lankaqrUploadId = null)
    {
        ArgumentNullException.ThrowIfNull(harness);

        var ownerId = await harness.Seed.UserAsync("fleet_owner");

        var vehicle = await harness.Seed.VehicleAsync(
            ownerId,
            "mini_van",
            mode: "B",
            modeBBilling: billing,
            defaultMonthlyFareMinor: billing == "paid" ? defaultFareMinor : null);

        var fleetId = await harness.Seed.FleetAsync(ownerId, vehicle.Id);
        await harness.Seed.FleetMemberAsync(fleetId, ownerId, "owner");

        if (payoutProfileStatus is not null)
        {
            await harness.Seed.PayoutProfileAsync(fleetId, payoutProfileStatus, lankaqrUploadId);
        }

        return new ModeBFleet(ownerId, harness.Tokens.FleetOwner(ownerId), fleetId, vehicle);
    }

    /// <summary>A passenger who has asked for access and been accepted (BR-23.7).</summary>
    public static async Task<(SeededDriver Passenger, AcceptModeBAccessResponse Accepted)> SubscribeAsync(
        SubscriptionHarness harness, ModeBFleet fleet)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(fleet);

        var passenger = await harness.Seed.PassengerAsync();
        var accepted = await SubscribeAsync(harness, fleet, passenger);

        return (passenger, accepted);
    }

    /// <summary>The same walk for a passenger who already exists — a rejoin uses this.</summary>
    public static async Task<AcceptModeBAccessResponse> SubscribeAsync(
        SubscriptionHarness harness, ModeBFleet fleet, SeededDriver passenger)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(passenger);

        var request = await harness.OkAsync<AccessRequestResponse>(
            await harness.PostAsync(
                $"/v1/mode-b/{fleet.VehicleId}/access-requests", new { }, passenger.Bearer),
            "request Mode B access");

        return await harness.OkAsync<AcceptModeBAccessResponse>(
            await harness.PostAsync(
                $"/v1/mode-b/access-requests/{request.RequestId}/accept", null, fleet.OwnerBearer),
            "accept Mode B access");
    }

    /// <summary>The passenger's own subscription cards (SCR-PA-025).</summary>
    public static Task<CursorPage<ModeBSubscriptionResponse>> SubscriptionsAsync(
        SubscriptionHarness harness, SeededDriver passenger)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(passenger);

        return harness.GetAsync<CursorPage<ModeBSubscriptionResponse>>(
            $"/v1/mode-b/subscriptions/{passenger.Id}", passenger.Bearer);
    }

    /// <summary>The owner's roster, muted rows included (item 16).</summary>
    public static Task<CursorPage<SubscriberRowResponse>> RosterAsync(
        SubscriptionHarness harness, ModeBFleet fleet)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(fleet);

        return harness.GetAsync<CursorPage<SubscriberRowResponse>>(
            $"/v1/mode-b/{fleet.VehicleId}/subscribers", fleet.OwnerBearer);
    }

    /// <summary>Asserts a failed response's RFC 7807 code.</summary>
    public static async Task AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code)
    {
        ArgumentNullException.ThrowIfNull(response);

        var (actual, body) = await SubscriptionHarness.ProblemAsync(response);

        Assert.True(
            response.StatusCode == status && actual == code,
            $"expected {(int)status} {code}, got {(int)response.StatusCode} {actual}: {body}");
    }
}
