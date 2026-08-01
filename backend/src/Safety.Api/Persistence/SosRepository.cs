using Dapper;
using MageRide.Safety.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.Safety.Persistence;

/// <summary>One row of <c>safety.sos_events</c> (migrations 0902 + 0905).</summary>
public sealed record SosEvent(
    Guid Id,
    Guid? UserId,
    string Role,
    Guid? RideId,
    double Lat,
    double Lng,
    string? EmergencyContact,
    string? SmsStatus,
    string? PrimaryGateway,
    string? SecondaryGateway,
    DateTimeOffset? AdminAckedAt,
    string Source,
    string? ShareToken,
    DateTimeOffset Ts,
    DateTimeOffset? DispatchedAt);

/// <summary>What to record when the button is pressed.</summary>
public sealed record NewSosEvent(
    Guid? UserId,
    string Role,
    Guid? RideId,
    double Lat,
    double Lng,
    string? EmergencyContact,
    string Source,
    string? ShareToken);

/// <summary>Who to reach, denormalised onto the account (AL-13).</summary>
/// <remarks>
/// <b>Read from <c>iam.users</c> and never joined.</b> iam-svc's own CLAUDE.md states the reason
/// from the other side: "D-33 budgets five seconds for the whole SOS fan-out, so safety-svc reads
/// `iam.users.emergency_contact_name`/`_phone` and never joins", and every mutation of
/// `iam.emergency_contacts` re-derives those two columns inside the same transaction. A join to the
/// contacts table would be correct and slower on the one path that cannot afford it.
/// </remarks>
/// <param name="RaiserName">
/// The account holder — who the alert is <em>about</em>. Distinct from <paramref name="Name"/>, who
/// is the person being told: <c>sos_alert</c> reads "{{name}} has raised an SOS", and rendering the
/// contact's own name there would tell somebody that they had raised it themselves.
/// </param>
public sealed record EmergencyContact(
    Guid UserId, string? RaiserName, string? Name, string? Phone, string Language)
{
    public bool CanBeReached => !string.IsNullOrWhiteSpace(Phone);
}

/// <summary><c>safety.sos_events</c> — US-12.11's durable record of every alert.</summary>
public interface ISosRepository
{
    /// <summary>Writes the event inside <paramref name="unitOfWork"/>, beside its outbox row.</summary>
    Task<SosEvent> CreateAsync(
        IUnitOfWork unitOfWork, NewSosEvent sos, CancellationToken cancellationToken);

    /// <summary>
    /// Records the D-33 outcome once the gateways have answered.
    /// </summary>
    /// <remarks>
    /// A second statement, outside the transaction that wrote the row, and deliberately so: the
    /// dispatch is another service's work and cannot be rolled back with ours. The row exists from
    /// the moment the button is pressed, which is what makes an SOS that nobody could send still
    /// visible to an operator.
    /// </remarks>
    Task MarkDispatchedAsync(
        Guid id,
        string smsStatus,
        string? primaryGateway,
        string? secondaryGateway,
        DateTimeOffset? dispatchedAt,
        CancellationToken cancellationToken);

    Task<SosEvent?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>One user's history, newest first.</summary>
    Task<IReadOnlyList<SosEvent>> ListForUserAsync(
        Guid userId, DateTimeOffset? before, int limit, CancellationToken cancellationToken);

    /// <summary>AL-13's contact, or <see langword="null"/> when there is no such account.</summary>
    Task<EmergencyContact?> FindEmergencyContactAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Who an AL-44 web SOS is about and who it reaches, resolved from the share token alone.
    /// </summary>
    /// <remarks>
    /// <b>Δ C066.</b> One query rather than a token read followed by a ride read: the alert is on
    /// D-33's five-second budget and the two facts come out of one join.
    /// </remarks>
    Task<WebSosSubject?> FindWebSosSubjectAsync(string shareToken, CancellationToken cancellationToken);
}

/// <summary>
/// What a share token says about the alert raised through it (AL-44, US-25.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The recipient is the booker, and D6' I-29.4 says so in as many words</b> — "recipient = the
/// booker's registered mobile". Not an emergency contact: the person on an SCR-WT page has no
/// MageRide account and therefore no <c>iam.users.emergency_contact_phone</c> to read, and the one
/// party the platform knows is connected to them is whoever arranged the journey.
/// </para>
/// <para>
/// <b>The number never leaves this service.</b> public-bff hands over a token and coordinates and is
/// told an id and an outcome; the booker's MSISDN is resolved here, used here and returned to
/// nobody. That is P-02/P-09's fence held by where the column is read rather than by a redaction
/// step on the way out.
/// </para>
/// </remarks>
/// <param name="RaiserName">
/// Whoever is on the page — the proxy rider or the package recipient, as the ride names them. Goes
/// into <c>sos_alert</c>'s <c>{{name}}</c>, which reads "{{name}} has raised an SOS": the booker
/// needs to know <em>who</em> is in trouble, and on a proxy ride that is not the account holder.
/// </param>
public sealed record WebSosSubject(
    string Token,
    string Scope,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    Guid? TripId,
    string? BookerPhone,
    string? RaiserName)
{
    public bool IsLiveAt(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public bool CanBeReached => !string.IsNullOrWhiteSpace(BookerPhone);
}

/// <inheritdoc cref="ISosRepository"/>
internal sealed class SosRepository(INpgsqlConnectionFactory connections) : ISosRepository
{
    private readonly INpgsqlConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private const string Columns =
        """
        id, user_id, role, ride_id, lat, lng, emergency_contact, sms_status,
        primary_gateway, secondary_gateway, admin_acked_at, source, share_token, ts, dispatched_at
        """;

    public async Task<SosEvent> CreateAsync(
        IUnitOfWork unitOfWork, NewSosEvent sos, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(sos);

        return await unitOfWork.Connection.QuerySingleAsync<SosEvent>(
            new CommandDefinition(
                $"""
                 INSERT INTO safety.sos_events
                   (user_id, role, ride_id, lat, lng, emergency_contact, source, share_token)
                 VALUES
                   (@UserId, @Role, @RideId, @Lat, @Lng, @EmergencyContact, @Source, @ShareToken)
                 RETURNING {Columns};
                 """,
                sos,
                unitOfWork.Transaction,
                cancellationToken: cancellationToken));
    }

    public async Task MarkDispatchedAsync(
        Guid id,
        string smsStatus,
        string? primaryGateway,
        string? secondaryGateway,
        DateTimeOffset? dispatchedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE safety.sos_events
                   SET sms_status = @SmsStatus,
                       primary_gateway = @PrimaryGateway,
                       secondary_gateway = @SecondaryGateway,
                       dispatched_at = @DispatchedAt
                 WHERE id = @Id;
                """,
                new { Id = id, SmsStatus = smsStatus, PrimaryGateway = primaryGateway, SecondaryGateway = secondaryGateway, DispatchedAt = dispatchedAt },
                cancellationToken: cancellationToken));
    }

    public async Task<SosEvent?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<SosEvent>(
            new CommandDefinition(
                $"SELECT {Columns} FROM safety.sos_events WHERE id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<SosEvent>> ListForUserAsync(
        Guid userId, DateTimeOffset? before, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        // `ix_sos_user` is (user_id, ts DESC), which is exactly this page. The cursor is the
        // timestamp rather than an offset: an SOS raised mid-scroll must not shift the page.
        var rows = await connection.QueryAsync<SosEvent>(
            new CommandDefinition(
                $"""
                 SELECT {Columns}
                   FROM safety.sos_events
                  WHERE user_id = @UserId
                    AND (@Before::timestamptz IS NULL OR ts < @Before)
                  ORDER BY ts DESC
                  LIMIT @Limit;
                 """,
                new { UserId = userId, Before = before, Limit = limit },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<EmergencyContact?> FindEmergencyContactAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<EmergencyContact>(
            new CommandDefinition(
                """
                SELECT id AS user_id,
                       first_name AS raiser_name,
                       emergency_contact_name AS name,
                       emergency_contact_phone AS phone,
                       language
                  FROM iam.users
                 WHERE id = @UserId;
                """,
                new { UserId = userId },
                cancellationToken: cancellationToken));
    }

    public async Task<WebSosSubject?> FindWebSosSubjectAsync(
        string shareToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shareToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);

        // The raiser is whoever the ride says is being carried or delivered to; the recipient is the
        // booker. `booker_id` is NOT NULL on every ride (0601), so a live trip-scoped token always
        // resolves to somebody — a NULL phone here means an account with only an email, which
        // `ck_users_credential` permits and which no passenger has.
        return await connection.QuerySingleOrDefaultAsync<WebSosSubject>(
            new CommandDefinition(
                """
                SELECT t.token,
                       t.scope,
                       t.expires_at,
                       t.revoked_at,
                       t.trip_id,
                       b.phone                                     AS booker_phone,
                       CASE WHEN r.kind = 2 THEN r.recipient_name ELSE r.rider_name END AS raiser_name
                  FROM safety.trip_share_tokens t
                  LEFT JOIN rides.rides r ON r.id = t.trip_id
                  LEFT JOIN iam.users b   ON b.id = r.booker_id
                 WHERE t.token = @Token;
                """,
                new { Token = shareToken },
                cancellationToken: cancellationToken));
    }
}

/// <summary>The <c>safety.events</c> payloads (migration 0905).</summary>
public static class SafetyEvents
{
    /// <summary>
    /// US-12.11's admin live feed.
    /// </summary>
    /// <remarks>
    /// <b>Written inside the transaction that records the event, before any SMS is attempted</b>
    /// (R-13). An operator learns about an SOS whether or not a gateway took it — which is the
    /// case where a human being is most needed, and the one an "emit after a successful dispatch"
    /// ordering would silently drop.
    /// </remarks>
    public static MageRide.Shared.Messaging.OutboxRecord SosRaised(SosEvent sos, string? contactName) =>
        Record(
            SafetyEventTypes.SosRaised,

            // Keyed by the person who raised it. A web SOS has no account, and the token stands in:
            // two alerts from one source have to reach the console in order, and the alternative
            // (the ride) is null on an SOS raised with no ride at all.
            sos.UserId ?? sos.Id,
            new
            {
                sosId = sos.Id,
                userId = sos.UserId,
                role = sos.Role,
                rideId = sos.RideId,
                position = new { lat = sos.Lat, lng = sos.Lng },
                source = sos.Source,

                // The number itself is NOT on the event. The console shows who to call from the
                // user's own record; putting an MSISDN on a topic several services consume would
                // spread it for no gain (§0 PII).
                emergencyContactName = contactName,
                raisedAt = sos.Ts,
            });

    /// <summary>US-12.5. Keyed by the driver the report counts against.</summary>
    public static MageRide.Shared.Messaging.OutboxRecord VehicleReported(
        Guid reportId, Guid vehicleId, Guid? driverId, Guid reporterId, Guid? rideId, string reason) =>
        Record(
            SafetyEventTypes.VehicleReported,
            driverId ?? vehicleId,
            new { reportId, vehicleId, driverId, reporterId, rideId, reason, status = VehicleReportStatuses.Pending });

    /// <summary>US-12.6. <paramref name="confirmedTotal"/> is what makes the third one the delisting.</summary>
    public static MageRide.Shared.Messaging.OutboxRecord VehicleReportResolved(
        Guid reportId, Guid vehicleId, Guid? driverId, string status, int confirmedTotal, bool delisted) =>
        Record(
            SafetyEventTypes.VehicleReportResolved,
            driverId ?? vehicleId,
            new { reportId, vehicleId, driverId, status, confirmedTotal, delisted });

    private static MageRide.Shared.Messaging.OutboxRecord Record(string eventType, Guid aggregateId, object payload) =>
        new(aggregateId, eventType,
            System.Text.Json.JsonSerializer.Serialize(payload, MageRide.Shared.Http.MageRideJson.StorageOptions));
}
