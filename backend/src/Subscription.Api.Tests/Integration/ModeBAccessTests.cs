using System.Net;
using System.Text.Json;
using MageRide.Shared.Primitives;
using MageRide.Subscriptions.Endpoints;
using MageRide.Subscriptions.ModeB;
using MageRide.Subscriptions.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Subscriptions.Tests.Integration;

/// <summary>
/// Epic 23's access half: the per-vehicle request queue (AL-23), the grant and subscription an
/// accept starts, the D-22 revocation an unsubscribe publishes, and AL-25's muted-until-deleted row.
/// </summary>
[Collection<SubscriptionCollection>]
public sealed class ModeBAccessTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>Item 15 end to end: the marker tap, the queue, the accept, the card.</summary>
    [Fact]
    public async Task An_accepted_request_creates_a_grant_a_subscription_and_a_share_granted_event()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var passenger = await harness.Seed.PassengerAsync();

        var request = await harness.OkAsync<AccessRequestResponse>(
            await harness.PostAsync($"/v1/mode-b/{fleet.VehicleId}/access-requests", new { }, passenger.Bearer),
            "request access");

        Assert.Equal("pending", request.Status);
        Assert.Equal(fleet.VehicleId, request.VehicleId);
        Assert.Equal(passenger.Id, request.PassengerId);

        // The owner's queue names the requester, with the mobile role-masked (AL-40/41/42).
        var queue = await harness.GetAsync<CursorPage<AccessRequestResponse>>(
            $"/v1/mode-b/{fleet.VehicleId}/access-requests", fleet.OwnerBearer);

        var queued = Assert.Single(queue.Items);
        Assert.Equal(request.RequestId, queued.RequestId);
        Assert.NotNull(queued.PassengerMobileMasked);
        Assert.Contains('*', queued.PassengerMobileMasked);
        Assert.DoesNotContain("1234567", queued.PassengerMobileMasked, StringComparison.Ordinal);

        var accepted = await harness.OkAsync<AcceptModeBAccessResponse>(
            await harness.PostAsync(
                $"/v1/mode-b/access-requests/{request.RequestId}/accept", null, fleet.OwnerBearer),
            "accept");

        // The subscription inherits the vehicle's Paid classification and its default fare (AL-24).
        var card = Assert.Single((await ModeBScenario.SubscriptionsAsync(harness, passenger)).Items);

        Assert.Equal(accepted.SubscriptionId, card.SubscriptionId);
        Assert.Equal("paid", card.Billing);
        Assert.Equal(250_000, card.MonthlyFareMinor);
        Assert.Equal("LKR", card.Currency);
        Assert.Equal("join_anniversary", card.Cycle);
        Assert.Equal("active", card.Status);
        Assert.NotNull(card.NextDue);

        // The queue is empty afterwards — a decided request is not a pending one.
        Assert.Empty((await harness.GetAsync<CursorPage<AccessRequestResponse>>(
            $"/v1/mode-b/{fleet.VehicleId}/access-requests", fleet.OwnerBearer)).Items);

        // fanout-svc's entitlement cache is built from this, and it needs the passenger id: a
        // share event naming only a vehicle is one it skips (C041).
        var events = await harness.OutboxAsync(fleet.VehicleId);
        var granted = Assert.Single(events, e => e.EventType == ModeBShareEventTypes.ShareGranted);

        AssertNames(granted.Payload, fleet.VehicleId, passenger.Id);
    }

    /// <summary>
    /// Definition of done: "a passenger who unsubscribes immediately loses Mode B visibility via the
    /// fanout revocation path" — and the roster row survives, muted, until the owner deletes it.
    /// </summary>
    [Fact]
    public async Task Unsubscribing_revokes_visibility_and_leaves_the_row_muted_for_the_owner()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        var cancelled = await harness.OkAsync<ModeBSubscriptionResponse>(
            await harness.PostAsync(
                $"/v1/mode-b/subscriptions/{accepted.SubscriptionId}/unsubscribe", null, passenger.Bearer),
            "unsubscribe");

        Assert.Equal("cancelled", cancelled.Status);

        // The revocation D-22 turns into a directed RemoveFromGroupAsync, committed with the mute.
        var events = await harness.OutboxAsync(fleet.VehicleId);
        var revoked = Assert.Single(events, e => e.EventType == ModeBShareEventTypes.ShareRevoked);

        AssertNames(revoked.Payload, fleet.VehicleId, passenger.Id);

        // The passenger can no longer see the vehicle (US-23.11) — the list is grant-scoped, the
        // same rule fanout applies to the map.
        Assert.Empty((await ModeBScenario.SubscriptionsAsync(harness, passenger)).Items);

        // The owner still can, muted (US-4.12 / US-13.16).
        var muted = Assert.Single((await ModeBScenario.RosterAsync(harness, fleet)).Items);

        Assert.True(muted.Muted);
        Assert.Equal("unsubscribed", muted.Status);
        Assert.Equal(passenger.Id, muted.PassengerId);
        Assert.Equal(accepted.GrantId, muted.SubscriberId);

        // "what they were paying" survives the cancellation, because the roster line has to render.
        Assert.Equal("paid", muted.Billing);
        Assert.Equal(250_000, muted.MonthlyFareMinor);

        var grant = await harness.GrantAsync(accepted.GrantId);
        Assert.Equal(("unsubscribed", false), grant);
    }

    /// <summary>
    /// Definition of done: "re-joining requires a fresh request → accept; the muted row is still
    /// visible to the owner until deleted".
    /// </summary>
    [Fact]
    public async Task Rejoining_needs_a_fresh_request_and_reuses_the_muted_roster_row()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (passenger, first) = await ModeBScenario.SubscribeAsync(harness, fleet);

        await harness.OkAsync<ModeBSubscriptionResponse>(
            await harness.PostAsync(
                $"/v1/mode-b/subscriptions/{first.SubscriptionId}/unsubscribe", null, passenger.Bearer),
            "unsubscribe");

        // Unsubscribing does not put the passenger back on the vehicle by itself, and neither does
        // asking: the driver or owner has to accept again (BR-23.11).
        var second = await ModeBScenario.SubscribeAsync(harness, fleet, passenger);

        Assert.NotEqual(first.SubscriptionId, second.SubscriptionId);

        // ux_grant_active is partial on deleted_at, so the muted grant still holds the (vehicle,
        // passenger) slot — the rejoin reuses that row rather than failing on it.
        Assert.Equal(first.GrantId, second.GrantId);

        var roster = Assert.Single((await ModeBScenario.RosterAsync(harness, fleet)).Items);
        Assert.False(roster.Muted);
        Assert.Equal("active", roster.Status);

        var card = Assert.Single((await ModeBScenario.SubscriptionsAsync(harness, passenger)).Items);
        Assert.Equal(second.SubscriptionId, card.SubscriptionId);
    }

    /// <summary>
    /// AL-25's order: the passenger ends their own subscription, and only then may the owner remove
    /// the row.
    /// </summary>
    [Fact]
    public async Task An_owner_may_delete_a_muted_subscriber_and_never_an_active_one()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        await ModeBScenario.AssertProblemAsync(
            await harness.DeleteAsync(
                $"/v1/mode-b/{fleet.VehicleId}/subscribers/{accepted.GrantId}", fleet.OwnerBearer),
            HttpStatusCode.Conflict,
            "conflict");

        await harness.OkAsync<ModeBSubscriptionResponse>(
            await harness.PostAsync(
                $"/v1/mode-b/subscriptions/{accepted.SubscriptionId}/unsubscribe", null, passenger.Bearer),
            "unsubscribe");

        using (var deleted = await harness.DeleteAsync(
            $"/v1/mode-b/{fleet.VehicleId}/subscribers/{accepted.GrantId}", fleet.OwnerBearer))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.Empty((await ModeBScenario.RosterAsync(harness, fleet)).Items);

        var grant = await harness.GrantAsync(accepted.GrantId);
        Assert.Equal(("unsubscribed", true), grant);
    }

    /// <summary>
    /// AL-23's fence: access is per vehicle, never per fleet or per account. Two vehicles on one
    /// fleet are two queues and two grants.
    /// </summary>
    [Fact]
    public async Task Access_to_one_vehicle_confers_nothing_on_the_fleets_other_vehicle()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness);

        var second = await harness.Seed.VehicleAsync(
            fleet.OwnerId, "van", mode: "B", modeBBilling: "paid", defaultMonthlyFareMinor: 300_000);

        await harness.Seed.FleetAsync(fleet.OwnerId, second.Id);

        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        var onFirst = Assert.Single((await ModeBScenario.RosterAsync(harness, fleet)).Items);
        Assert.Equal(accepted.GrantId, onFirst.SubscriberId);

        Assert.Empty((await harness.GetAsync<CursorPage<SubscriberRowResponse>>(
            $"/v1/mode-b/{second.Id}/subscribers", fleet.OwnerBearer)).Items);

        var cards = await ModeBScenario.SubscriptionsAsync(harness, passenger);
        Assert.Equal(fleet.VehicleId, Assert.Single(cards.Items).VehicleId);

        // And the second vehicle takes its own request, which the first one's grant does not answer.
        var onSecond = await harness.OkAsync<AccessRequestResponse>(
            await harness.PostAsync($"/v1/mode-b/{second.Id}/access-requests", new { }, passenger.Bearer),
            "request access to the second vehicle");

        Assert.Equal("pending", onSecond.Status);
    }

    /// <summary>
    /// US-23.1's audience. A stranger sees nothing; the assigned driver works the queue from the
    /// Driver App (SCR-DA-028); a Manager can accept but cannot delete a subscriber.
    /// </summary>
    [Fact]
    public async Task The_queue_belongs_to_the_owner_the_manager_and_the_assigned_driver()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var passenger = await harness.Seed.PassengerAsync();

        var stranger = await harness.Seed.PassengerAsync();

        await ModeBScenario.AssertProblemAsync(
            await harness.GetAsync($"/v1/mode-b/{fleet.VehicleId}/access-requests", stranger.Bearer),
            HttpStatusCode.Forbidden,
            "not-owner");

        var request = await harness.OkAsync<AccessRequestResponse>(
            await harness.PostAsync($"/v1/mode-b/{fleet.VehicleId}/access-requests", new { }, passenger.Bearer),
            "request access");

        // The driver the vehicle is assigned to (US-13.9) accepts from their own app.
        var driver = await harness.Seed.DriverAsync();
        await harness.Seed.AssignDriverAsync(fleet.FleetId, fleet.VehicleId, driver.Id);

        var accepted = await harness.OkAsync<AcceptModeBAccessResponse>(
            await harness.PostAsync(
                $"/v1/mode-b/access-requests/{request.RequestId}/accept", null, driver.Bearer),
            "accept as the assigned driver");

        // A Manager reads the roster and cannot remove anybody from it — the money and the
        // membership are the Owner's (US-23.6).
        var manager = await harness.Seed.UserAsync("fleet_owner");
        await harness.Seed.FleetMemberAsync(fleet.FleetId, manager, "manager");
        var managerBearer = harness.Tokens.FleetOwner(manager);

        Assert.Single((await harness.GetAsync<CursorPage<SubscriberRowResponse>>(
            $"/v1/mode-b/{fleet.VehicleId}/subscribers", managerBearer)).Items);

        await ModeBScenario.AssertProblemAsync(
            await harness.DeleteAsync(
                $"/v1/mode-b/{fleet.VehicleId}/subscribers/{accepted.GrantId}", managerBearer),
            HttpStatusCode.Forbidden,
            "not-owner");
    }

    /// <summary>
    /// A second ask is the first one, not a second queue entry (<c>ux_access_request_open</c>), and
    /// a request that has already been decided cannot be decided again.
    /// </summary>
    [Fact]
    public async Task Asking_twice_yields_one_request_and_deciding_twice_is_a_conflict()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var passenger = await harness.Seed.PassengerAsync();

        var first = await harness.OkAsync<AccessRequestResponse>(
            await harness.PostAsync($"/v1/mode-b/{fleet.VehicleId}/access-requests", new { }, passenger.Bearer),
            "first ask");

        var second = await harness.OkAsync<AccessRequestResponse>(
            await harness.PostAsync($"/v1/mode-b/{fleet.VehicleId}/access-requests", new { }, passenger.Bearer),
            "second ask");

        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Single((await harness.GetAsync<CursorPage<AccessRequestResponse>>(
            $"/v1/mode-b/{fleet.VehicleId}/access-requests", fleet.OwnerBearer)).Items);

        await harness.OkAsync<AcceptModeBAccessResponse>(
            await harness.PostAsync(
                $"/v1/mode-b/access-requests/{first.RequestId}/accept", null, fleet.OwnerBearer),
            "accept");

        await ModeBScenario.AssertProblemAsync(
            await harness.PostAsync(
                $"/v1/mode-b/access-requests/{first.RequestId}/reject", new { }, fleet.OwnerBearer),
            HttpStatusCode.Conflict,
            "conflict");
    }

    /// <summary>Mode B is the only mode with private tracking access to ask for (AL-23).</summary>
    [Fact]
    public async Task A_mode_c_vehicle_has_no_access_to_request()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync();
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);
        var passenger = await harness.Seed.PassengerAsync();

        await ModeBScenario.AssertProblemAsync(
            await harness.PostAsync($"/v1/mode-b/{vehicle.Id}/access-requests", new { }, passenger.Bearer),
            HttpStatusCode.Forbidden,
            "mode-not-allowed");
    }

    /// <summary>
    /// A Mode B vehicle with no Service payment set is Free (AL-24): a subscription starts, and it
    /// carries no fare at all.
    /// </summary>
    [Fact]
    public async Task An_unclassified_vehicle_starts_a_free_subscription_with_no_fare_and_no_due_date()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness, billing: null, defaultFareMinor: null);
        var (passenger, _) = await ModeBScenario.SubscribeAsync(harness, fleet);

        var card = Assert.Single((await ModeBScenario.SubscriptionsAsync(harness, passenger)).Items);

        Assert.Equal("free", card.Billing);
        Assert.Null(card.MonthlyFareMinor);
        Assert.Null(card.NextDue);
    }

    /// <summary>
    /// A Paid vehicle with no default fare is refused rather than given an invented one: a
    /// subscription with no fare would bill nothing for ever, and <c>ck_subscriptions_fare</c>
    /// refuses the row anyway.
    /// </summary>
    [Fact]
    public async Task A_paid_vehicle_with_no_default_fare_cannot_start_a_subscription()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness, billing: "paid", defaultFareMinor: null);
        var passenger = await harness.Seed.PassengerAsync();

        var request = await harness.OkAsync<AccessRequestResponse>(
            await harness.PostAsync($"/v1/mode-b/{fleet.VehicleId}/access-requests", new { }, passenger.Bearer),
            "request access");

        await ModeBScenario.AssertProblemAsync(
            await harness.PostAsync(
                $"/v1/mode-b/access-requests/{request.RequestId}/accept", null, fleet.OwnerBearer),
            HttpStatusCode.Conflict,
            "conflict");

        // Nothing half-committed: the request is still pending and the queue still has it.
        Assert.Single((await harness.GetAsync<CursorPage<AccessRequestResponse>>(
            $"/v1/mode-b/{fleet.VehicleId}/access-requests", fleet.OwnerBearer)).Items);
    }

    /// <summary>Only the passenger ends their own subscription (US-23.11); the owner has a delete.</summary>
    [Fact]
    public async Task An_owner_cannot_unsubscribe_a_passenger()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (_, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        await ModeBScenario.AssertProblemAsync(
            await harness.PostAsync(
                $"/v1/mode-b/subscriptions/{accepted.SubscriptionId}/unsubscribe", null, fleet.OwnerBearer),
            HttpStatusCode.Forbidden,
            "forbidden");

        // And nothing was published: a revocation that did not happen must not reach fanout.
        Assert.DoesNotContain(
            await harness.OutboxAsync(fleet.VehicleId), e => e.EventType == ModeBShareEventTypes.ShareRevoked);
    }

    /// <summary>One passenger's card list is not another's (SCR-PA-025).</summary>
    [Fact]
    public async Task A_passenger_cannot_read_another_passengers_subscriptions()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (passenger, _) = await ModeBScenario.SubscribeAsync(harness, fleet);
        var other = await harness.Seed.PassengerAsync();

        await ModeBScenario.AssertProblemAsync(
            await harness.GetAsync($"/v1/mode-b/subscriptions/{passenger.Id}", other.Bearer),
            HttpStatusCode.Forbidden,
            "forbidden");
    }

    /// <summary>The payload fanout-svc reads: the vehicle it is about and the passenger it names.</summary>
    private static void AssertNames(string payload, Guid vehicleId, Guid passengerId)
    {
        using var document = JsonDocument.Parse(payload);

        Assert.Equal(vehicleId, document.RootElement.GetProperty("vehicleId").GetGuid());
        Assert.Equal(passengerId, document.RootElement.GetProperty("passengerId").GetGuid());
    }
}
