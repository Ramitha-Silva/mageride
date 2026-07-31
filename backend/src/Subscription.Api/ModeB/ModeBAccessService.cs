using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.Shared.Time;
using MageRide.Subscriptions.Domain;
using MageRide.Subscriptions.Fees;
using MageRide.Subscriptions.Persistence;

namespace MageRide.Subscriptions.ModeB;

/// <summary>What an accept produced (item 15).</summary>
public sealed record AcceptedAccess(AccessRequestRow Request, GrantRow Grant, SubscriptionRow Subscription);

/// <summary>One roster line with the passenger it names.</summary>
public sealed record RosterEntry(SubscriberRosterRow Row, UserContact? Contact);

/// <summary>One queued request with the passenger it names.</summary>
public sealed record PendingRequest(AccessRequestRow Row, UserContact? Contact);

/// <summary>
/// Epic 23's access half: who may track a Mode B vehicle, and the subscription that starts when they
/// may (AL-23, AL-25, BR-23.7, BR-23.11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is per vehicle.</b> AL-23 is the fence and it is held by shape rather than by
/// care: every method takes a vehicle or something that resolves to one, and
/// <see cref="IModeBAccessRepository"/> exposes no query that could produce an account-global grant.
/// A driver with three vehicles works three queues.
/// </para>
/// <para>
/// <b>The grant and the event commit together.</b> An accept writes the grant, the subscription and
/// a <c>share.granted</c> row inside one transaction; an unsubscribe writes the mute, the
/// cancellation and a <c>share.revoked</c> row inside another (R-13). The alternative — publishing
/// after the commit — is how a passenger keeps watching a vehicle they have left, which is the leak
/// D-22 exists to close.
/// </para>
/// </remarks>
internal sealed class ModeBAccessService(
    INpgsqlConnectionFactory connections,
    IUnitOfWorkFactory unitOfWorkFactory,
    IModeBRegistryRepository registry,
    IModeBAccessRepository access,
    IOutboxWriter outbox,
    TimeProvider clock,
    ILogger<ModeBAccessService> logger)
{
    /// <summary>US-4.9 / item 8 — the marker tap.</summary>
    public async Task<AccessRequestRow> RequestAccessAsync(
        Guid passengerId, Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var vehicle = await RequireVehicleAsync(connection, null, vehicleId, passengerId, cancellationToken);

        // Asking for access to your own vehicle is a client bug rather than a queue entry the owner
        // has to triage, and a Mode A/C vehicle has no private tracking to ask for.
        if (vehicle.IsVehicleOwner)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["vehicleId"] = ["You already own this vehicle."],
            });
        }

        if (!string.Equals(vehicle.Mode, OperatingModes.PrivateTransport, StringComparison.Ordinal))
        {
            throw new MageRideException(
                MageRideErrors.ModeNotAllowed,
                $"Vehicle {vehicleId} is Mode {vehicle.Mode}. Mode B is the only mode with private tracking "
                + "access (AL-23).");
        }

        var existing = await access.FindGrantForPairAsync(connection, null, vehicleId, passengerId, cancellationToken);

        // A muted grant is not a reason to refuse: rejoining is a fresh request the owner accepts
        // (BR-23.11), and the request is how it starts.
        if (existing is { Status: GrantStatuses.Active })
        {
            throw new MageRideException(
                MageRideErrors.Conflict, "You already have access to this vehicle.");
        }

        return await access.RequestAccessAsync(connection, null, vehicleId, passengerId, cancellationToken);
    }

    /// <summary>The driver's / owner's per-vehicle queue (SCR-DA-028, SCR-FP-011).</summary>
    public async Task<IReadOnlyList<PendingRequest>> ListRequestsAsync(
        Guid callerId,
        Guid vehicleId,
        (DateTimeOffset RequestedAt, Guid RequestId)? after,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var vehicle = await RequireVehicleAsync(connection, null, vehicleId, callerId, cancellationToken);
        RequireManage(vehicle);

        var rows = await access.ListPendingRequestsAsync(connection, vehicleId, after, limit, cancellationToken);
        var contacts = await registry.ReadContactsAsync(
            connection, null, [.. rows.Select(static row => row.PassengerId)], cancellationToken);

        return [.. rows.Select(row => new PendingRequest(row, contacts.GetValueOrDefault(row.PassengerId)))];
    }

    /// <summary>
    /// Item 15's accept: the grant and the subscription, in one transaction, with the event that
    /// makes the vehicle visible.
    /// </summary>
    public async Task<AcceptedAccess> AcceptAsync(
        Guid callerId, Guid requestId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var today = BusinessCalendar.BusinessDate(now);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var request = await access.FindRequestAsync(
                          unitOfWork.Connection, unitOfWork.Transaction, requestId, cancellationToken)
                      ?? throw NoSuchRequest(requestId);

        var vehicle = await RequireVehicleAsync(
            unitOfWork.Connection, unitOfWork.Transaction, request.VehicleId, callerId, cancellationToken);

        RequireManage(vehicle);

        var decided = await access.DecideRequestAsync(
                          unitOfWork, requestId, AccessRequestStatuses.Accepted, callerId, now, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.Conflict,
                          $"Request {requestId} is {request.Status} and cannot be accepted from there.");

        var grant = await access.GrantAccessAsync(
            unitOfWork, request.VehicleId, request.PassengerId, now, cancellationToken);

        var (billing, fareMinor) = ClassifyFrom(vehicle);

        // The schema's own default. BR-23.9 offers month_first as the alternative and nothing in
        // Epic 23 sets it at accept time — the Fleet Portal changes it afterwards.
        const string cycle = SubscriptionCycles.JoinAnniversary;

        var subscription = await access.StartSubscriptionAsync(
            unitOfWork,
            grant,
            billing,
            fareMinor,
            Currencies.Lkr,
            cycle,
            today.Day,
            // A Free subscription has no due date, because nothing is ever due. Writing one would put
            // a date on the passenger's card for money the vehicle does not collect.
            string.Equals(billing, SubscriptionBilling.Paid, StringComparison.Ordinal)
                ? SubscriptionCycles.FirstDue(today, cycle)
                : null,
            now,
            cancellationToken);

        await outbox.WriteAsync(unitOfWork, ModeBShareEvents.ShareGranted(grant), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Passenger {PassengerId} accepted onto vehicle {VehicleId}: grant {GrantId}, subscription "
            + "{SubscriptionId} ({Billing}), next due {NextDue}",
            request.PassengerId,
            request.VehicleId,
            grant.GrantId,
            subscription.SubscriptionId,
            subscription.Billing,
            subscription.NextDue);

        return new AcceptedAccess(decided, grant, subscription);
    }

    /// <summary>Item 15's reject. Terminal, and it creates nothing.</summary>
    public async Task<AccessRequestRow> RejectAsync(
        Guid callerId, Guid requestId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var request = await access.FindRequestAsync(
                          unitOfWork.Connection, unitOfWork.Transaction, requestId, cancellationToken)
                      ?? throw NoSuchRequest(requestId);

        var vehicle = await RequireVehicleAsync(
            unitOfWork.Connection, unitOfWork.Transaction, request.VehicleId, callerId, cancellationToken);

        RequireManage(vehicle);

        var decided = await access.DecideRequestAsync(
                          unitOfWork, requestId, AccessRequestStatuses.Rejected, callerId, now, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.Conflict,
                          $"Request {requestId} is {request.Status} and cannot be rejected from there.");

        await unitOfWork.CommitAsync(cancellationToken);

        return decided;
    }

    /// <summary>SCR-PA-025 — the passenger's own cards.</summary>
    public Task<IReadOnlyList<SubscriptionRow>> ListSubscriptionsAsync(
        Guid passengerId,
        (DateTimeOffset CreatedAt, Guid SubscriptionId)? after,
        int limit,
        CancellationToken cancellationToken) =>
        access.ListPassengerSubscriptionsAsync(passengerId, after, limit, cancellationToken);

    /// <summary>
    /// BR-23.11's unsubscribe: visibility ends now, billing stops, the roster row stays muted.
    /// </summary>
    public async Task<SubscriptionRow> UnsubscribeAsync(
        Guid passengerId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var subscription = await access.FindSubscriptionAsync(
                               unitOfWork.Connection, unitOfWork.Transaction, subscriptionId, cancellationToken)
                           ?? throw NoSuchSubscription(subscriptionId);

        // 403 rather than 404: US-23.11 is the passenger's own action, and the owner's removal is a
        // different verb with a different effect (DELETE .../subscribers/{id}, which hard-deletes a
        // row that is already muted). Letting an owner through here would silently perform the wrong
        // one — the same rule registry-svc applies to its half of this pair.
        if (subscription.PassengerId != passengerId)
        {
            throw new MageRideException(
                MageRideErrors.Forbidden, "A subscription may only be ended by the passenger it belongs to.");
        }

        var grant = await access.FindGrantAsync(
                        unitOfWork.Connection,
                        unitOfWork.Transaction,
                        subscription.VehicleId,
                        subscription.GrantId,
                        cancellationToken)
                    ?? throw NoSuchSubscription(subscriptionId);

        var cancelled = await access.UnsubscribeAsync(unitOfWork, subscriptionId, now, cancellationToken)
                        ?? throw new MageRideException(
                            MageRideErrors.Conflict,
                            "This subscription has already ended. Rejoining needs a fresh access request "
                            + "the driver or owner accepts (BR-23.11).");

        // D-22: the directed removal fanout-svc turns into a RemoveFromGroupAsync in under 200 ms.
        // Inside the transaction, so a revocation that could not be published rolls the unsubscribe
        // back rather than leaving the passenger watching.
        await outbox.WriteAsync(unitOfWork, ModeBShareEvents.ShareRevoked(grant, now), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Passenger {PassengerId} unsubscribed from vehicle {VehicleId}; share.revoked queued (D-22). "
            + "Grant {GrantId} stays muted on the owner's roster until they delete it.",
            passengerId,
            subscription.VehicleId,
            grant.GrantId);

        return cancelled;
    }

    /// <summary>Item 17 — the owner's hard delete of a muted row.</summary>
    public async Task DeleteSubscriberAsync(
        Guid callerId, Guid vehicleId, Guid subscriberId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var vehicle = await RequireVehicleAsync(
            unitOfWork.Connection, unitOfWork.Transaction, vehicleId, callerId, cancellationToken);

        RequireOwn(vehicle);

        var grant = await access.FindGrantAsync(
                        unitOfWork.Connection, unitOfWork.Transaction, vehicleId, subscriberId, cancellationToken)
                    ?? throw NoSuchSubscriber(subscriberId);

        var deleted = await access.DeleteGrantAsync(unitOfWork, vehicleId, subscriberId, now, cancellationToken);

        if (deleted is null)
        {
            // The row is still active. AL-25 puts the order the other way round — the passenger
            // unsubscribes and only then may the owner remove them — so this is a 409 rather than a
            // silent revocation the passenger did not ask for.
            throw new MageRideException(
                MageRideErrors.Conflict,
                "This subscriber is still active. Only a passenger can end their own subscription; the row "
                + "becomes deletable once they have (US-4.12).");
        }

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Owner {CallerId} deleted muted subscriber {SubscriberId} from vehicle {VehicleId}",
            callerId,
            subscriberId,
            vehicleId);
    }

    /// <summary>US-23.7 — the per-subscriber fare override.</summary>
    public async Task<RosterEntry> SetFareAsync(
        Guid callerId, Guid vehicleId, Guid subscriberId, long monthlyFareMinor, CancellationToken cancellationToken)
    {
        if (monthlyFareMinor < 0)
        {
            throw new MageRideException(MageRideErrors.InvalidAmount, "monthlyFareMinor must not be negative.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);

        var vehicle = await RequireVehicleAsync(connection, null, vehicleId, callerId, cancellationToken);
        RequireOwn(vehicle);

        _ = await access.FindGrantAsync(connection, null, vehicleId, subscriberId, cancellationToken)
            ?? throw NoSuchSubscriber(subscriberId);

        var updated = await access.SetFareAsync(subscriberId, monthlyFareMinor, cancellationToken);

        if (updated is null)
        {
            // Either the subscription is Free — ck_subscriptions_fare refuses a fare on one, and
            // BR-23.8 says a Free vehicle has no payment UI at all — or it has been cancelled by an
            // unsubscribe, in which case there is nothing to bill.
            throw new MageRideException(
                MageRideErrors.Conflict,
                "This subscriber has no live Paid subscription to set a fare on. A Free service payment "
                + "carries no fare (BR-23.8).");
        }

        return await RequireRosterEntryAsync(connection, vehicleId, subscriberId, cancellationToken);
    }

    /// <summary>Item 16 — the owner's roster, muted rows included.</summary>
    public async Task<IReadOnlyList<RosterEntry>> ListRosterAsync(
        Guid callerId,
        Guid vehicleId,
        (DateTimeOffset GrantedAt, Guid SubscriberId)? after,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var vehicle = await RequireVehicleAsync(connection, null, vehicleId, callerId, cancellationToken);
        RequireManage(vehicle);

        var rows = await access.ListRosterAsync(
            vehicleId, CurrentPeriod(), after, limit, cancellationToken);

        var contacts = await registry.ReadContactsAsync(
            connection, null, [.. rows.Select(static row => row.PassengerId)], cancellationToken);

        return [.. rows.Select(row => new RosterEntry(row, contacts.GetValueOrDefault(row.PassengerId)))];
    }

    /// <summary>The vehicle and the caller's rights over it, or the right 404/403.</summary>
    private async Task<ModeBVehicle> RequireVehicleAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid callerId,
        CancellationToken cancellationToken) =>
        await registry.ReadVehicleAsync(connection, transaction, vehicleId, callerId, cancellationToken)
        ?? throw new MageRideException(MageRideErrors.VehicleNotFound, $"No vehicle {vehicleId}.");

    /// <summary>The current Colombo month, as <c>subscription.payments.period_month</c> spells it.</summary>
    private DateOnly CurrentPeriod()
    {
        var today = BusinessCalendar.Today(clock);
        return new DateOnly(today.Year, today.Month, 1);
    }

    private async Task<RosterEntry> RequireRosterEntryAsync(
        Npgsql.NpgsqlConnection connection, Guid vehicleId, Guid subscriberId, CancellationToken cancellationToken)
    {
        var rows = await access.ListRosterAsync(
            vehicleId, CurrentPeriod(), null, 1, cancellationToken, subscriberId);

        var row = rows.Count > 0 ? rows[0] : throw NoSuchSubscriber(subscriberId);

        var contacts = await registry.ReadContactsAsync(connection, null, [row.PassengerId], cancellationToken);

        return new RosterEntry(row, contacts.GetValueOrDefault(row.PassengerId));
    }

    /// <summary>
    /// US-23.1's audience: the vehicle's owner, the org's Owner or Manager, or the driver it is
    /// assigned to.
    /// </summary>
    internal static void RequireManage(ModeBVehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        if (!vehicle.CanManage)
        {
            throw new MageRideException(
                MageRideErrors.NotOwner, "This vehicle's subscribers are somebody else's to manage.");
        }
    }

    /// <summary>
    /// The narrower audience for money and membership — owner only (US-23.6, US-23.7, item 17).
    /// </summary>
    internal static void RequireOwn(ModeBVehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        if (!vehicle.CanOwn)
        {
            throw new MageRideException(
                MageRideErrors.NotOwner,
                "Only the vehicle's owner may do this. A fleet Manager can work the request queue and read "
                + "the roster, but the subscription money is the Owner's (US-23.6).");
        }
    }

    /// <summary>
    /// The Paid/Free classification a new subscription inherits (AL-24, BR-23.8).
    /// </summary>
    /// <remarks>
    /// <b>An unclassified Mode B vehicle is Free, and a Paid one with no default fare is refused.</b>
    /// <c>registry.vehicles.mode_b_billing</c> is nullable and US-13.1b captures it at onboarding, so
    /// a NULL is a vehicle onboarded before the setting existed — treating it as Paid would start
    /// charging subscribers of a vehicle whose owner never named a price. A Paid vehicle with no
    /// <c>default_monthly_fare_minor</c> is the opposite mistake and cannot be papered over:
    /// <c>ck_subscriptions_fare</c> refuses the row, and inventing a number would bill a passenger an
    /// amount nobody chose.
    /// </remarks>
    private static (string Billing, long? FareMinor) ClassifyFrom(ModeBVehicle vehicle)
    {
        if (!string.Equals(vehicle.ModeBBilling, SubscriptionBilling.Paid, StringComparison.Ordinal))
        {
            return (SubscriptionBilling.Free, null);
        }

        return vehicle.DefaultMonthlyFareMinor is { } fare
            ? (SubscriptionBilling.Paid, fare)
            : throw new MageRideException(
                MageRideErrors.Conflict,
                $"Vehicle {vehicle.VehicleId} is Service payment = Paid but carries no default monthly fare, "
                + "so a subscription started on it would bill nothing for ever. Set one on the vehicle first "
                + "(US-13.1b).");
    }

    private static MageRideException NoSuchRequest(Guid requestId) =>
        new(MageRideErrors.NotFound, $"No Mode B access request {requestId}.");

    private static MageRideException NoSuchSubscription(Guid subscriptionId) =>
        new(MageRideErrors.NotFound, $"No Mode B subscription {subscriptionId}.");

    private static MageRideException NoSuchSubscriber(Guid subscriberId) =>
        new(MageRideErrors.NotFound, $"No subscriber {subscriberId} on this vehicle.");
}
