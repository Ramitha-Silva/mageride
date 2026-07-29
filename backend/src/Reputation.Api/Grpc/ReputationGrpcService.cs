using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MageRide.Reputation.Counters;
using MageRide.Reputation.Domain;
using MageRide.Shared.Primitives;

namespace MageRide.Reputation.Grpc;

/// <summary>
/// <c>reputation.v1.Reputation</c> — the D-04 surface dispatch-svc gates on.
/// </summary>
/// <remarks>
/// <para>
/// The service and RPC names are D3' verbatim
/// (<c>backend/contracts/proto/reputation.v1.proto</c>). This class is deliberately thin: it maps
/// the wire types, and every rule lives in <see cref="ReputationRules"/> behind
/// <see cref="IReputationService"/>. A gate that answered slightly differently over gRPC than over
/// the admin route would be the worst kind of bug in this component, because only one of the two
/// is ever looked at by a human.
/// </para>
/// <para>
/// <b>Errors are status codes, not exceptions with a body.</b> gRPC has no problem+json, so a
/// malformed id is <see cref="StatusCode.InvalidArgument"/> and an unreachable database surfaces as
/// <see cref="StatusCode.Internal"/> through the interceptor-free default — dispatch-svc's D6' §8.3
/// resilience policy is what decides whether to retry, and a fabricated OK would silently open the
/// gate.
/// </para>
/// </remarks>
public sealed class ReputationGrpcService(IReputationService reputation) : Reputation.ReputationBase
{
    public override async Task<BlockStatus> GetBlockStatus(DriverRef request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var status = await reputation.GetStatusAsync(ParseId(request.UserId, "user_id"), context.CancellationToken);

        return ToBlockStatus(status);
    }

    public override async Task<Level> GetDriverLevel(DriverRef request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var level = await reputation.GetLevelAsync(ParseId(request.UserId, "user_id"), context.CancellationToken);

        return new Level
        {
            UserId = level.DriverId.ToString(),
            Level_ = level.Level,
            Points = level.RatingPoints,
            LevelUpThreshold = level.LevelUpThreshold,
            JobBoardEligible = level.JobBoardEligible,
        };
    }

    public override Task<Ack> ReportCancellation(CancellationEvent request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var subject = ParseId(request.UserId, "user_id");
        var rideId = ParseId(request.RideId, "ride_id");
        var role = ParseRole(request.Role);

        return RecordAsync(
            new ReputationFact(
                DedupeKey: Dedupe(IntakeKinds.Cancellation, request.EventId, rideId, subject),
                Kind: IntakeKinds.Cancellation,
                SubjectId: subject,
                SubjectRole: role,
                RideId: rideId,
                Source: IntakeSources.Grpc,
                ReasonCode: NullIfEmpty(request.ReasonCode),
                SystemInitiated: request.SystemInitiated),
            context);
    }

    public override Task<Ack> ReportNoShow(NoShowEvent request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var subject = ParseId(request.UserId, "user_id");
        var rideId = ParseId(request.RideId, "ride_id");

        return RecordAsync(
            new ReputationFact(
                DedupeKey: Dedupe(IntakeKinds.NoShow, request.EventId, rideId, subject),
                Kind: IntakeKinds.NoShow,
                SubjectId: subject,
                SubjectRole: ParseRole(request.Role),
                RideId: rideId,
                Source: IntakeSources.Grpc),
            context);
    }

    public override Task<Ack> ReportVehicle(VehicleReport request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var driverId = ParseId(request.DriverId, "driver_id");
        var reportId = ParseId(request.ReportId, "report_id");

        // Only a CONFIRMED report moves the tally (US-12.6). A PENDING one is recorded so that the
        // later CONFIRMED arriving under the same report id is not counted twice, and so that
        // "why is this counter 2?" has an answer that includes the report under review.
        var confirmed = request.Status == ReportStatus.Confirmed;

        return RecordAsync(
            new ReputationFact(
                DedupeKey: $"{IntakeKinds.Report}:{reportId}",
                Kind: IntakeKinds.Report,
                SubjectId: driverId,
                SubjectRole: SubjectRoles.Driver,
                RideId: ParseOptionalId(request.RideId),
                Source: IntakeSources.Grpc,
                Counted: confirmed,
                ReasonCode: NullIfEmpty(request.Reason)),
            context);
    }

    private async Task<Ack> RecordAsync(ReputationFact fact, ServerCallContext context)
    {
        var outcome = await reputation.RecordAsync(fact, context.CancellationToken);

        return new Ack
        {
            Counted = outcome.Counted,
            Duplicate = outcome.Duplicate,
            State = ToBlockState(outcome.Status.State),
        };
    }

    private static BlockStatus ToBlockStatus(ReputationStatus status) => new()
    {
        UserId = status.UserId.ToString(),
        State = ToBlockState(status.State),
        ExpiresAt = status.ExpiresAt is { } expires ? Timestamp.FromDateTimeOffset(expires) : null,
        Reason = status.Reason ?? BlockReasons.Clear,
        CancellationsContinuous = status.CancellationsContinuous,
        ReportsTotal = status.ReportsTotal,
        NoShows = status.NoShows,
        DispatchEligible = status.AllowsDispatch,
    };

    private static BlockState ToBlockState(string state) => state switch
    {
        BlockStates.Ok => BlockState.Ok,
        BlockStates.Warn => BlockState.Warn,
        BlockStates.BookingDisabled => BlockState.BookingDisabled,
        BlockStates.Delisted => BlockState.Delisted,

        // Unreachable through ck_block_states_state, and mapped rather than thrown because an
        // unknown state must never read as OK to a caller that is gating on it.
        _ => BlockState.Unspecified,
    };

    /// <summary>
    /// The dedupe key. A caller with an event id gets exactly-once on that; one without gets it on
    /// <c>(kind, ride, subject)</c>, which is the same fact by a different name.
    /// </summary>
    private static string Dedupe(string kind, string? eventId, Guid rideId, Guid subjectId) =>
        Ulids.TryParse(eventId, out var parsed) && parsed != Guid.Empty
            ? $"{IntakeSources.Grpc}:{parsed}"
            : $"{kind}:{rideId}:{subjectId}";

    private static string ParseRole(SubjectRole role) => role switch
    {
        SubjectRole.Passenger => SubjectRoles.Passenger,
        SubjectRole.Driver => SubjectRoles.Driver,
        _ => throw new RpcException(new Status(
            StatusCode.InvalidArgument, "role must be SUBJECT_ROLE_PASSENGER or SUBJECT_ROLE_DRIVER.")),
    };

    private static Guid ParseId(string? value, string field) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new RpcException(new Status(
                StatusCode.InvalidArgument, $"{field} is required and must be a ULID or a UUID."));

    private static Guid? ParseOptionalId(string? value) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
