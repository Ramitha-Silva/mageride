namespace MageRide.Reputation.Domain;

/// <summary>One row of <c>reputation.counters</c>.</summary>
/// <param name="WindowStartedAt">
/// Start of the current rolling window — the instant <see cref="ReportsTotal"/> and
/// <see cref="NoShows"/> were last cleared. Stored in <c>window_reset_at</c>; see migration 0804
/// for the reading. <see cref="CancellationsContinuous"/> is deliberately not window-scoped: D5'
/// §7.2 makes it a consecutive run, reset by any completed ride.
/// </param>
public sealed record CounterRow(
    Guid UserId,
    int CancellationsContinuous,
    int ReportsTotal,
    int NoShows,
    DateTimeOffset? WindowStartedAt,
    DateTimeOffset UpdatedAt)
{
    public static CounterRow Empty(Guid userId, DateTimeOffset now) =>
        new(userId, 0, 0, 0, now, now);
}

/// <summary>One row of <c>reputation.block_states</c>.</summary>
public sealed record BlockStateRow(
    Guid UserId,
    string State,
    DateTimeOffset? ExpiresAt,
    string Source,
    string? Reason,
    Guid? SetBy,
    DateTimeOffset UpdatedAt)
{
    public static BlockStateRow Clear(Guid userId, DateTimeOffset now) =>
        new(userId, BlockStates.Ok, null, BlockSources.Auto, BlockReasons.Clear, null, now);

    /// <summary>True while the row's time box has not passed. A row with no box always holds.</summary>
    public bool HoldsAt(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}

/// <summary>What a rule decided: a state, why, and how long it holds.</summary>
public readonly record struct BlockDecision(string State, string Reason, DateTimeOffset? ExpiresAt)
{
    public int Severity => BlockStates.Severity(State);

    public static BlockDecision Clear => new(BlockStates.Ok, BlockReasons.Clear, null);
}

/// <summary>The full verdict a caller gets back — the state plus the counters behind it.</summary>
public sealed record ReputationStatus(
    Guid UserId,
    string State,
    string? Reason,
    string Source,
    DateTimeOffset? ExpiresAt,
    int CancellationsContinuous,
    int ReportsTotal,
    int NoShows,
    DateTimeOffset? WindowStartedAt)
{
    public bool AllowsDispatch => BlockStates.AllowsDispatch(State);

    public static ReputationStatus From(BlockStateRow state, CounterRow counters) =>
        new(
            state.UserId,
            state.State,
            state.Reason,
            state.Source,
            state.ExpiresAt,
            counters.CancellationsContinuous,
            counters.ReportsTotal,
            counters.NoShows,
            counters.WindowStartedAt);

    /// <summary>What an unknown user looks like: nothing has ever happened, so nothing is wrong.</summary>
    public static ReputationStatus Clear(Guid userId) =>
        new(userId, BlockStates.Ok, BlockReasons.Clear, BlockSources.Auto, null, 0, 0, 0, null);
}

/// <summary>One row of <c>dispatch.driver_levels</c> (D5' §4.2, US-6A.6).</summary>
/// <remarks>
/// The table lives in the <c>dispatch</c> schema because that is where D4' §6 and
/// server_db_schema.md §6 print it, and reputation-svc is nonetheless its writer — D5' §4.2 gives
/// every level *change* to this service (3 reports → level−1 + temporary delisting; a no-show →
/// level−1) and D3' puts the appeal restore on reputation-svc's admin surface. Raised as a
/// micro-change-set in the C033 handoff: the table belongs in <c>reputation</c>.
/// </remarks>
public sealed record DriverLevelRow(Guid DriverId, int Level, int RatingPoints, int LevelUpThreshold)
{
    /// <summary>D5' §4.2: Level 1 loses Job Board / scheduled-ride access (US-6A.8).</summary>
    public bool JobBoardEligible => Level > 1;

    public static DriverLevelRow Default(Guid driverId, int threshold) => new(driverId, 3, 0, threshold);
}

/// <summary>A fact to count. One shape for both intake paths — gRPC and <c>ride.events</c>.</summary>
/// <param name="DedupeKey">
/// What makes counting exactly-once. <c>'{source}:{eventId}'</c> for a topic message,
/// <c>'{kind}:{rideId}:{subjectId}'</c> for a caller that minted no event id.
/// </param>
/// <param name="Counted">
/// <see langword="false"/> for a fact that is recorded and moves nothing — a non-CONFIRMED vehicle
/// report, or a pre-acceptance cancellation a caller sent anyway (D5' §7.2: those never count).
/// </param>
public sealed record ReputationFact(
    string DedupeKey,
    string Kind,
    Guid SubjectId,
    string SubjectRole,
    Guid? RideId,
    string Source,
    bool Counted = true,
    string? ReasonCode = null,
    bool SystemInitiated = false,
    string? Detail = null);

/// <summary>What an intake did.</summary>
/// <param name="Duplicate">
/// The fact had already been counted. Not an error: delivery is at-least-once (D6' §2.3) and a
/// retried RPC is expected.
/// </param>
public sealed record IntakeOutcome(ReputationStatus Status, bool Counted, bool Duplicate, int? Level);

/// <summary>One row of <c>reputation.fraud_flags</c>.</summary>
public sealed record FraudFlagRow(
    Guid Id,
    string Kind,
    Guid? SubjectId,
    string? SubjectType,
    Guid? RelatedId,
    string Status,
    string WindowKey,
    string? Detail,
    Guid? ResolvedBy,
    DateTimeOffset? ResolvedAt,
    string? ResolutionNote,
    DateTimeOffset Ts);

/// <summary>A signal the detector raised, before it is written.</summary>
public sealed record FraudSignal(
    string Kind,
    Guid SubjectId,
    string SubjectType,
    Guid? RelatedId,
    string WindowKey,
    string Summary,
    IReadOnlyDictionary<string, object?> Detail);
