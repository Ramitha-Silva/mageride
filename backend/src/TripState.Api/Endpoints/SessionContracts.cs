using MageRide.TripState.Domain;

namespace MageRide.TripState.Endpoints;

// The wire shapes of backend/contracts/trip-state.yaml. Records rather than the domain types so
// the contract and the schema can move independently — `state` in particular is stored as two
// values and served as three (see SessionViews).

/// <summary><c>POST /v1/sessions/start</c> request body.</summary>
internal sealed record StartSessionBody(string? VehicleId, string? Mode, string? RouteId, bool AutoEndAtDestination);

/// <summary><c>POST /v1/internal/sessions/{sessionId}/auto-end</c> request body.</summary>
internal sealed record AutoEndBody(string? Reason);

/// <summary>
/// <c>POST /v1/internal/sessions/ignition</c> request body — the tracker plane's ACC report.
/// </summary>
/// <param name="State"><c>on</c> or <c>off</c>. Spelled rather than boolean because it is a device
/// state a human reads in a log, and a bare <c>true</c> would not say what of.</param>
/// <param name="At">When the ignition changed, as the device timestamped it. Falls back to receipt
/// time — a tracker with a flat backup cell reports 2016.</param>
internal sealed record IgnitionBody(string? VehicleId, string? State, DateTimeOffset? At);

/// <summary><c>RatingInput</c>, plus the passenger the driver is rating.</summary>
internal sealed record RatingBody(int? Stars, string? Text, string? PassengerId);

/// <summary><c>trip-state.yaml#/components/schemas/Session</c>.</summary>
internal sealed record SessionResponse(
    Guid SessionId,
    Guid VehicleId,
    Guid DriverId,
    string Mode,
    Guid? RouteId,
    string State,
    bool AutoEndAtDestination,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? EndReason,
    DateTimeOffset? RestartableUntil)
{
    public static SessionResponse From(Session session, TimeSpan restartGrace)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new SessionResponse(
            session.Id,
            session.VehicleId,
            session.DriverId,
            session.Mode,
            session.RouteId,
            // Two stored values, three on the wire. The reconciliation lives in SessionViews and
            // nowhere else.
            SessionViews.From(session),
            session.AutoEndAtDestination,
            session.StartedAt,
            session.EndedAt,
            session.EndReason,
            session.RestartableUntil(restartGrace));
    }
}

/// <summary><c>trip-state.yaml#/components/schemas/Rating</c>.</summary>
internal sealed record RatingResponse(Guid RatingId, int Stars, string? Text, DateTimeOffset CreatedAt)
{
    public static RatingResponse From(SessionRating rating)
    {
        ArgumentNullException.ThrowIfNull(rating);

        return new RatingResponse(rating.Id, rating.Stars, rating.Comment, rating.CreatedAt);
    }
}
