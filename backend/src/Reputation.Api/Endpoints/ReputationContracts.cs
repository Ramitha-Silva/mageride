using MageRide.Reputation.Domain;
using MageRide.Shared.Primitives;

namespace MageRide.Reputation.Endpoints;

/// <summary>The <c>FraudFlag</c> schema of <c>backend/contracts/reputation.yaml</c>.</summary>
public sealed record FraudFlagResponse(
    string FlagId,
    string Kind,
    string? SubjectId,
    string? SubjectType,
    string? CounterpartyId,
    string Status,
    string? BlockStatus,
    string? Detail,
    string WindowKey,
    DateTimeOffset? ResolvedAt,
    string? ResolvedBy,
    DateTimeOffset CreatedAt)
{
    public static FraudFlagResponse From(FraudFlagRow row, string? blockStatus = null)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new FraudFlagResponse(
            FlagId: row.Id.ToString(),
            Kind: row.Kind,
            SubjectId: row.SubjectId?.ToString(),
            SubjectType: row.SubjectType,
            CounterpartyId: row.RelatedId?.ToString(),
            Status: row.Status,
            BlockStatus: blockStatus,
            Detail: row.Detail,
            WindowKey: row.WindowKey,
            ResolvedAt: row.ResolvedAt,
            ResolvedBy: row.ResolvedBy?.ToString(),
            CreatedAt: row.Ts);
    }
}

/// <summary>The counters half of <c>ReputationSubject</c>.</summary>
public sealed record CountersResponse(
    int CancellationsContinuous,
    int ReportsTotal,
    int NoShows,
    DateTimeOffset? WindowStartedAt);

/// <summary>The <c>ReputationSubject</c> schema — everything this service holds about one user.</summary>
public sealed record ReputationSubjectResponse(
    string UserId,
    string State,
    string? StateReason,
    string StateSource,
    DateTimeOffset? ExpiresAt,
    CountersResponse Counters,
    int? Level,
    int? Points)
{
    public static ReputationSubjectResponse From(ReputationStatus status, DriverLevelRow? level)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new ReputationSubjectResponse(
            UserId: status.UserId.ToString(),
            State: status.State,
            StateReason: status.Reason,
            StateSource: status.Source,
            ExpiresAt: status.ExpiresAt,
            Counters: new CountersResponse(
                status.CancellationsContinuous,
                status.ReportsTotal,
                status.NoShows,
                status.WindowStartedAt),
            Level: level?.Level,
            Points: level?.RatingPoints);
    }
}

/// <summary>The <c>POST /v1/admin/drivers/{driverId}/level/restore</c> response.</summary>
public sealed record LevelResponse(string DriverId, int Level, int Points)
{
    public static LevelResponse From(DriverLevelRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new LevelResponse(row.DriverId.ToString(), row.Level, row.RatingPoints);
    }
}

/// <summary>Request bodies. Nullable throughout so a missing member is a validation failure with a
/// field name rather than a model-binding 400 with none.</summary>
public sealed record RestoreLevelBody(int? Level, string? Reason);

public sealed record OverrideBlockStateBody(string? State, string? Reason, DateTimeOffset? ExpiresAt);

public sealed record ResolveFlagBody(string? Status, string? Note);

public sealed record NetworkObservationBody(
    string? UserId, string? RideId, string? Ip, int? Asn, string? UserAgent);

/// <summary>Shared parsing for the path and body identifiers D3' §0 types as <c>Ulid</c>.</summary>
internal static class RequestIds
{
    public static Guid Require(string? value, string field) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new Shared.Errors.MageRideValidationException(new Dictionary<string, string[]>
            {
                [field] = [$"{field} is required and must be a ULID or a UUID."],
            });

    public static Guid? Optional(string? value) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : null;
}
