using Dapper;
using MageRide.PublicBff.Domain;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;

namespace MageRide.PublicBff.Persistence;

/// <summary>
/// A ride as the holder of a <c>package_recipient</c> or <c>proxy_rider</c> token may see it.
/// </summary>
/// <remarks>
/// <b>What is not on this type is the point.</b> There is no booker id, no passenger id, no payment
/// instrument, no sender MSISDN and no <c>rider_phone_hash</c> — P-02/P-09's redaction is a property
/// of the read rather than of the projection that follows it, so a later change to the response
/// shape cannot reveal a column this query never selected. <c>SenderName</c> is a display name and
/// <c>DriverPhone</c> is AL-48's <c>tel:</c> target, which is the one number this surface carries.
/// </remarks>
public sealed record TrackedRide(
    Guid RideId,
    string State,
    short Kind,
    bool IsProxy,
    string PaymentMethod,
    string? SenderName,
    string? DriverName,
    string? DriverPhotoUrl,
    string? DriverPhone,
    string? VehicleType,
    string? RegistrationNumber,
    Guid? VehicleId,
    double PickupLat,
    double PickupLng,
    double DropoffLat,
    double DropoffLng,
    long? FareEstimateMinor,
    string Currency,
    DateTimeOffset? TerminalAt)
{
    /// <summary><c>rides.rides.kind</c> 2 (0601's <c>ck_rides_kind</c>).</summary>
    public bool IsPackage => Kind == 2;

    public GeoPoint Pickup => new(PickupLat, PickupLng);

    public GeoPoint Dropoff => new(DropoffLat, DropoffLng);
}

/// <summary>
/// What a <c>pickup_confirm</c> token addresses (AL-45): a live location request and the first name
/// of whoever raised it.
/// </summary>
/// <param name="SuggestedPin">
/// The booker's own pickup, when the request is already attached to a ride draft. Usually absent:
/// <c>rides.location_requests</c> is issued <em>before</em> the ride exists (0606), which is why
/// <c>ride_id</c> is nullable, and P-02's screen is an adjustable pin the rider drops rather than a
/// place the booker chose for them.
/// </param>
public sealed record TrackedPickupRequest(
    Guid Id,
    Guid RequestId,
    string State,
    DateTimeOffset IssuedAt,
    int TtlSeconds,
    string? BookerFirstName,
    GeoPoint? SuggestedPin)
{
    public DateTimeOffset ExpiresAt => IssuedAt.AddSeconds(TtlSeconds);

    /// <summary>
    /// The two states AL-45's web path may still answer.
    /// </summary>
    /// <remarks>
    /// <c>RiderNotRegistered</c> is the ordinary one — ADD §11.15 ended the round-trip there and
    /// AL-45 is later and re-opens it. <c>Pending</c> is admitted because a rider who *is*
    /// registered may still answer the SMS rather than the push, and refusing them would send the
    /// booker to the US-8.19 fallback for no reason.
    /// </remarks>
    public bool IsOpen => State is "Pending" or "RiderNotRegistered";
}

/// <summary>The settled facts behind SCR-WT-005.</summary>
/// <param name="ProofPhotoUrl">
/// The <c>rides.proof_artifacts</c> pointer, not a URL a browser can follow — it is presigned on the
/// way out, and only for a <c>delivery_photo</c>.
/// </param>
public sealed record TrackedReceipt(
    string State,
    short Kind,
    long? SettledMinor,
    string Currency,
    string? PaymentState,
    string? ProofPhotoUrl,
    DateTimeOffset? CompletedAt);

/// <summary>
/// Every read this surface makes, and it makes no writes.
/// </summary>
/// <remarks>
/// <b>Read-only across four bounded contexts, exactly as safety-svc's public view is.</b>
/// <c>rides.rides</c> is ride-svc's, <c>registry.vehicles</c> registry-svc's, <c>iam.users</c>
/// iam-svc's and <c>fares.ride_payments</c> fare-svc's. Nothing here is written by this service —
/// the only rows public-bff touches are the share token's meter and burn, and both are the token's
/// own.
/// </remarks>
public interface ITrackReadRepository
{
    Task<TrackedRide?> FindRideAsync(Guid rideId, CancellationToken cancellationToken);

    Task<TrackedPickupRequest?> FindPickupRequestAsync(Guid locationRequestId, CancellationToken cancellationToken);

    Task<TrackedReceipt?> FindReceiptAsync(Guid rideId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITrackReadRepository"/>
internal sealed class TrackReadRepository(INpgsqlConnectionFactory connections) : ITrackReadRepository
{
    private readonly INpgsqlConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    public async Task<TrackedRide?> FindRideAsync(Guid rideId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        // Coordinates come out as ST_Y/ST_X rather than as a mapped geography, which keeps
        // NetTopologySuite off this project's dependency list for two numbers a map pin needs.
        //
        // The driver is joined through the ACCEPTED vehicle rather than through the driver's current
        // one: the parcel is in the car that took it, and a driver who has since gone live on
        // another vehicle must not change the plate on somebody's tracking page mid-delivery.
        //
        // `registry.vehicles.driver_name` is what US-2.12 shows a passenger and is the name on the
        // registration; `registry.driver_profiles.display_name` is the same person as an account,
        // and either may be missing on a part-built record, so the read takes the first that is
        // there. `iam.users.first_name` is deliberately last — it is the account's own name and is
        // the weakest claim about who is at the wheel.
        return await connection.QuerySingleOrDefaultAsync<TrackedRide>(
            new CommandDefinition(
                """
                SELECT r.id                                       AS ride_id,
                       r.state,
                       r.kind,
                       r.is_proxy,
                       r.payment_method,
                       CASE WHEN r.kind = 2 THEN sender.first_name END AS sender_name,
                       COALESCE(v.driver_name, p.display_name, du.first_name) AS driver_name,
                       COALESCE(v.driver_photo_url, p.photo_url)   AS driver_photo_url,
                       du.phone                                    AS driver_phone,
                       v.vehicle_type,
                       v.registration_number,
                       r.accepted_vehicle_id                       AS vehicle_id,
                       ST_Y(r.pickup_geo::geometry)                AS pickup_lat,
                       ST_X(r.pickup_geo::geometry)                AS pickup_lng,
                       ST_Y(r.dropoff_geo::geometry)               AS dropoff_lat,
                       ST_X(r.dropoff_geo::geometry)               AS dropoff_lng,
                       r.fare_estimate_minor,
                       COALESCE(r.currency, 'LKR')                 AS currency,
                       r.terminal_at
                  FROM rides.rides r
                  LEFT JOIN registry.vehicles v          ON v.id = r.accepted_vehicle_id
                  LEFT JOIN iam.users du                 ON du.id = r.accepted_driver_id
                  LEFT JOIN registry.driver_profiles p   ON p.driver_id = r.accepted_driver_id
                  LEFT JOIN iam.users sender             ON sender.id = r.booker_id
                 WHERE r.id = @RideId;
                """,
                new { RideId = rideId },
                cancellationToken: cancellationToken));
    }

    public async Task<TrackedPickupRequest?> FindPickupRequestAsync(
        Guid locationRequestId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<PickupRequestRow>(
            new CommandDefinition(
                """
                SELECT lr.id,
                       lr.request_id,
                       lr.state,
                       lr.issued_at,
                       lr.ttl_seconds,
                       b.first_name                        AS booker_first_name,
                       ST_Y(rr.pickup_geo::geometry)       AS pickup_lat,
                       ST_X(rr.pickup_geo::geometry)       AS pickup_lng
                  FROM rides.location_requests lr
                  JOIN iam.users b        ON b.id = lr.booker_id
                  LEFT JOIN rides.rides rr ON rr.id = lr.ride_id
                 WHERE lr.id = @Id;
                """,
                new { Id = locationRequestId },
                cancellationToken: cancellationToken));

        if (row is null)
        {
            return null;
        }

        return new TrackedPickupRequest(
            row.Id,
            row.RequestId,
            row.State,
            row.IssuedAt,
            row.TtlSeconds,
            row.BookerFirstName,
            row.PickupLat is { } lat && row.PickupLng is { } lng ? new GeoPoint(lat, lng) : null);
    }

    public async Task<TrackedReceipt?> FindReceiptAsync(Guid rideId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        // **The settled attempt, not the last one.** D-10 makes a payment a chain of attempts
        // (1002: "one row per payment ATTEMPT"), so a card that failed and fell back to cash has two
        // rows and only one of them is the amount that was actually paid. Ordering by `updated_at`
        // and taking the first settled row is what makes the receipt agree with the ledger.
        //
        // The proof photograph is the delivery one and never the pickup one: SCR-WT-005 asks how the
        // parcel reached the recipient, and a photograph of it being collected from the sender
        // answers a different question.
        return await connection.QuerySingleOrDefaultAsync<TrackedReceipt>(
            new CommandDefinition(
                $"""
                 SELECT r.state,
                        r.kind,
                        (pay.amount_minor + pay.surcharge_minor + pay.tip_amount_minor)::bigint AS settled_minor,
                        COALESCE(pay.currency, r.currency, 'LKR')                     AS currency,
                        pay.state                                                     AS payment_state,
                        proof.storage_url                                             AS proof_photo_url,
                        r.terminal_at                                                 AS completed_at
                   FROM rides.rides r
                   LEFT JOIN LATERAL (
                        SELECT p.amount_minor, p.surcharge_minor, p.tip_amount_minor, p.currency, p.state
                          FROM fares.ride_payments p
                         WHERE p.ride_id = r.id
                           AND p.state = ANY(@Settled)
                         ORDER BY p.updated_at DESC
                         LIMIT 1) pay ON true
                   LEFT JOIN LATERAL (
                        SELECT a.storage_url
                          FROM rides.proof_artifacts a
                         WHERE a.ride_id = r.id AND a.kind = 'delivery_photo'
                         ORDER BY a.captured_at DESC
                         LIMIT 1) proof ON true
                  WHERE r.id = @RideId;
                 """,
                new { RideId = rideId, Settled = SettledPaymentStates },
                cancellationToken: cancellationToken));
    }

    /// <summary>
    /// The <c>fares.ride_payments.state</c> values that mean money changed hands.
    /// </summary>
    /// <remarks>
    /// The same list C061's <c>AnalyticsVocabulary.SettledPaymentStates</c> holds, spelled again
    /// rather than referenced: that type lives in a class library admin-bff hosts, and a
    /// <c>ProjectReference</c> from a passenger-facing pod to the back-office read model would be a
    /// worse coupling than a five-element array. Named here so the divergence is visible if either
    /// moves.
    /// </remarks>
    private static readonly string[] SettledPaymentStates =
        ["Succeeded", "CashOnDeliveryCollected", "DriverConfirmedQR", "Refunded", "PartiallyRefunded"];

    private sealed record PickupRequestRow(
        Guid Id,
        Guid RequestId,
        string State,
        DateTimeOffset IssuedAt,
        int TtlSeconds,
        string? BookerFirstName,
        double? PickupLat,
        double? PickupLng);
}
