using MageRide.Registry.Domain;
using MageRide.Registry.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Logging;

namespace MageRide.Registry.Sharing;

/// <summary><c>POST /v1/vehicles/{vehicleId}/share</c> (US-4.1/4.2).</summary>
public sealed record GrantShareCommand(Guid OwnerId, Guid VehicleId, string? GranteeUserId, DateTimeOffset? ExpiresAt);

/// <summary>The subscriber roster page <c>GET /v1/vehicles/{vehicleId}/subscribers</c> returns.</summary>
public sealed record SubscriberPage(IReadOnlyList<Subscriber> Items, string? NextCursor);

/// <summary>
/// Mode B sharing: who may see a private vehicle's live position, and the roster of passengers
/// entitled to it (D-22, D-23; US-4.1–4.7, US-NEW.1).
/// </summary>
public interface IShareService
{
    Task<ShareGrant> GrantAsync(GrantShareCommand command, CancellationToken cancellationToken);

    /// <summary>US-4.3b — visibility begins here, not at grant creation.</summary>
    Task<ShareGrant> AcceptAsync(Guid granteeUserId, Guid vehicleId, Guid grantId, CancellationToken cancellationToken);

    /// <summary>Revokes a grant and emits <c>share.revoked</c> through the outbox (D-22).</summary>
    Task RevokeAsync(Guid ownerId, Guid vehicleId, Guid grantId, CancellationToken cancellationToken);

    Task<SubscriberPage> ListSubscribersAsync(
        Guid ownerId, Guid vehicleId, string? cursor, int limit, CancellationToken cancellationToken);

    /// <summary>US-NEW.1 — the passenger's own unsubscribe.</summary>
    Task UnsubscribeAsync(Guid callerId, Guid vehicleId, Guid passengerId, CancellationToken cancellationToken);

    /// <summary>US-4.5 — a passenger asks an owner for Mode B access.</summary>
    Task<AccessRequest> RequestAccessAsync(Guid passengerId, string? vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IShareService"/>
public sealed class ShareService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IVehicleRepository vehicles,
    IShareRepository shares,
    ISubscriptionRepository subscriptions,
    IOutboxWriter outbox,
    TimeProvider clock,
    ILogger<ShareService> logger) : IShareService
{
    /// <summary><c>_shared.yaml</c>'s <c>Limit</c> parameter default and ceiling.</summary>
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    public async Task<ShareGrant> GrantAsync(GrantShareCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var granteeUserId = RequireUserId(command.GranteeUserId, "userId");
        var now = clock.GetUtcNow();

        if (command.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["expiresAt"] = ["expiresAt must be in the future (US-4.2)."],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var vehicle = await RequireOwnedVehicleAsync(unitOfWork, command.OwnerId, command.VehicleId, cancellationToken);

        // Sharing a vehicle with yourself is a no-op that would occupy the ux_shares_active slot
        // and make the real grant a 409 later.
        if (granteeUserId == command.OwnerId)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["userId"] = ["A vehicle cannot be shared with its own owner."],
            });
        }

        var grant = await shares.CreateAsync(
            unitOfWork.Connection, unitOfWork.Transaction, vehicle.Id, granteeUserId, command.ExpiresAt, cancellationToken);

        if (grant is null)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                "That user already holds a pending or accepted grant on this vehicle. Revoke it before granting again.");
        }

        await unitOfWork.CommitAsync(cancellationToken);

        // No event yet. A PENDING grant confers nothing — US-4.3b puts visibility at acceptance —
        // so publishing here would have fanout add a passenger to a group they may never accept
        // into.
        logger.LogInformation(
            "Vehicle {VehicleId} shared with {GranteeUserId}, pending acceptance", vehicle.Id, granteeUserId);

        return grant;
    }

    public async Task<ShareGrant> AcceptAsync(
        Guid granteeUserId, Guid vehicleId, Guid grantId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var existing = await shares.FindAsync(
                           unitOfWork.Connection, unitOfWork.Transaction, vehicleId, grantId, cancellationToken)
                       ?? throw NotFound(grantId);

        // 403, not 404: the grant exists and names somebody else. The caller was given this id by
        // the owner, so confirming it exists tells them nothing they did not already know.
        if (existing.GranteeUserId != granteeUserId)
        {
            throw new MageRideException(MageRideErrors.Forbidden, "This grant was issued to another user.");
        }

        var accepted = await shares.AcceptAsync(
                           unitOfWork.Connection, unitOfWork.Transaction, grantId, granteeUserId, now, cancellationToken)
                       ?? throw new MageRideException(
                           MageRideErrors.Conflict,
                           $"Grant {grantId} is {existing.State} and cannot be accepted from there.");

        await outbox.WriteAsync(unitOfWork, ShareEvents.ShareGranted(accepted), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Grant {GrantId} on vehicle {VehicleId} accepted", grantId, vehicleId);

        return accepted;
    }

    public async Task RevokeAsync(Guid ownerId, Guid vehicleId, Guid grantId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        _ = await RequireOwnedVehicleAsync(unitOfWork, ownerId, vehicleId, cancellationToken);

        _ = await shares.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, vehicleId, grantId, cancellationToken)
            ?? throw NotFound(grantId);

        var revoked = await shares.RevokeAsync(
            unitOfWork.Connection, unitOfWork.Transaction, grantId, now, cancellationToken);

        if (revoked is null)
        {
            // Already revoked or expired. 204 rather than 409: the caller asked for the grant to be
            // gone and it is, and a second DELETE from a retrying client is not an error. The
            // conditional UPDATE is what stops a second `share.revoked` going out.
            await unitOfWork.RollbackAsync(cancellationToken);
            return;
        }

        // The event and the state change commit together. A revoke that committed and then failed
        // to publish would leave a passenger watching a vehicle they no longer have access to —
        // the exact leak D-22 exists to close, and the reason this is an outbox row and not a
        // direct publish (R-13).
        await outbox.WriteAsync(unitOfWork, ShareEvents.ShareRevoked(revoked, "revoked"), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Grant {GrantId} on vehicle {VehicleId} revoked; share.revoked queued for {GranteeUserId}",
            grantId, vehicleId, revoked.GranteeUserId);
    }

    public async Task<SubscriberPage> ListSubscribersAsync(
        Guid ownerId, Guid vehicleId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var before = DecodeCursor(cursor);
        var pageSize = Math.Clamp(limit <= 0 ? DefaultPageSize : limit, 1, MaxPageSize);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var vehicle = await vehicles.FindAsync(connection, null, vehicleId, cancellationToken)
                      ?? throw new MageRideException(MageRideErrors.VehicleNotFound, $"No vehicle {vehicleId}.");

        if (vehicle.OwnerId != ownerId)
        {
            throw new MageRideException(MageRideErrors.NotOwner, "This vehicle belongs to another driver.");
        }

        // One more than asked for, so "is there another page" is answered by the read rather than
        // by a second count query.
        var rows = await subscriptions.ListSubscribersAsync(
            connection, null, vehicleId, before, pageSize + 1, cancellationToken);

        var page = rows.Count > pageSize ? rows.Take(pageSize).ToArray() : [.. rows];
        var next = rows.Count > pageSize ? EncodeCursor(page[^1].GrantedAt) : null;

        return new SubscriberPage(page, next);
    }

    public async Task UnsubscribeAsync(
        Guid callerId, Guid vehicleId, Guid passengerId, CancellationToken cancellationToken)
    {
        // US-NEW.1 is the passenger's own action. The owner's hard delete is
        // `DELETE /v1/mode-b/{vehicleId}/subscribers/{subId}` in subscription.yaml, which keeps
        // the row MUTED rather than removing it (US-4.12) — a different verb on a different
        // service, and letting an owner through here would silently perform the wrong one.
        if (callerId != passengerId)
        {
            throw new MageRideException(
                MageRideErrors.Forbidden,
                "A passenger may only unsubscribe themselves. The owner's removal is DELETE " +
                "/v1/mode-b/{vehicleId}/subscribers/{subId} (subscription-svc, US-4.12).");
        }

        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var grant = await subscriptions.UnsubscribeAsync(
                        unitOfWork.Connection, unitOfWork.Transaction, vehicleId, passengerId, now, cancellationToken)
                    ?? throw new MageRideException(
                        MageRideErrors.NotFound, "No active subscription for this passenger on this vehicle.");

        // The same directed removal a revoke earns (D-22): the passenger stops seeing the vehicle
        // now, not at the next cell crossing. The row stays MUTED on the owner's roster.
        await outbox.WriteAsync(
            unitOfWork,
            ShareEvents.ShareRevoked(
                new ShareGrant(grant.GrantId, vehicleId, passengerId, ShareStates.Revoked, null, null, now, grant.GrantedAt),
                "unsubscribed"),
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Passenger {PassengerId} unsubscribed from vehicle {VehicleId}", passengerId, vehicleId);
    }

    public async Task<AccessRequest> RequestAccessAsync(
        Guid passengerId, string? vehicleId, CancellationToken cancellationToken)
    {
        var id = RequireUserId(vehicleId, "vehicleId");

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var vehicle = await vehicles.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, id, cancellationToken)
                      ?? throw new MageRideException(MageRideErrors.VehicleNotFound, $"No vehicle {id}.");

        // Asking for access to your own vehicle, or to one that is not shareable, is a client bug
        // rather than a queue entry an owner has to triage.
        if (vehicle.OwnerId == passengerId)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["vehicleId"] = ["You already own this vehicle."],
            });
        }

        if (vehicle.Mode != OperatingModes.B)
        {
            throw new MageRideException(
                MageRideErrors.ModeNotAllowed,
                $"Vehicle {id} is Mode {vehicle.Mode}. Mode B is the only mode with private tracking access (AL-23).");
        }

        var request = await subscriptions.RequestAccessAsync(
            unitOfWork.Connection, unitOfWork.Transaction, id, passengerId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return request;
    }

    private async Task<Vehicle> RequireOwnedVehicleAsync(
        IUnitOfWork unitOfWork, Guid ownerId, Guid vehicleId, CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, vehicleId, cancellationToken)
                      ?? throw new MageRideException(MageRideErrors.VehicleNotFound, $"No vehicle {vehicleId}.");

        return vehicle.OwnerId == ownerId
            ? vehicle
            : throw new MageRideException(MageRideErrors.NotOwner, "This vehicle belongs to another driver.");
    }

    /// <summary>
    /// The roster cursor is the last row's <c>granted_at</c>, encoded the way
    /// <c>_shared.yaml#/components/schemas/CursorPage</c> describes: opaque to the client.
    /// </summary>
    private static string EncodeCursor(DateTimeOffset grantedAt) =>
        Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(grantedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));

    private static DateTimeOffset? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));

            return DateTimeOffset.TryParse(
                text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : throw new FormatException("not a round-trip timestamp");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["cursor"] = ["cursor is not one this endpoint issued."],
            });
        }
    }

    private static Guid RequireUserId(string? value, string field) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                [field] = [$"{field} is required and must be an identifier."],
            });

    private static MageRideException NotFound(Guid grantId) =>
        new(MageRideErrors.NotFound, $"No share grant {grantId} on this vehicle.");
}
