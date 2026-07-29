using MageRide.Reputation.Configuration;
using MageRide.Reputation.Domain;
using MageRide.Reputation.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Reputation.Counters;

/// <summary>
/// The one place a counter moves and the one place a block state is written.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every intake is one transaction</b>: the <c>reputation.intake_log</c> claim that makes it
/// exactly-once, the counter update, the block-state upsert, any level decrement, the audit row and
/// the <c>reputation.outbox</c> event. A counter that moved without the event that announced it —
/// or an event announcing a state that rolled back — is the phantom R-13 exists to prevent, and
/// here it would be a driver excluded from dispatch by a fact nobody can find.
/// </para>
/// <para>
/// <b>The rules decide, not the caller.</b> <see cref="ReputationRules"/> is a pure function of the
/// counters, the clock and the configured thresholds; this class opens the transaction, applies it
/// and publishes the consequence. A caller reports what happened and never what it should mean —
/// the same shape ride-svc's cancellation matrix has, and for the same reason: two callers with
/// slightly different ideas about what three cancels mean is how a rule stops being a rule.
/// </para>
/// <para>
/// <b>This service cancels no ride and suspends no driver</b> (the component's second fence). It
/// writes state and publishes it; dispatch-svc gates on it and admin acts on it.
/// </para>
/// </remarks>
public interface IReputationService
{
    /// <summary>The verdict for one user, from cache when warm and from Postgres otherwise.</summary>
    Task<ReputationStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The driver's level, defaulted to D5' §4.2's starting level when there is no row.</summary>
    Task<DriverLevelRow> GetLevelAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>Counts one fact. Idempotent on <see cref="ReputationFact.DedupeKey"/>.</summary>
    Task<IntakeOutcome> RecordAsync(ReputationFact fact, CancellationToken cancellationToken);

    /// <summary>An admin pins a state (deliverable: "manual state override with audit").</summary>
    Task<ReputationStatus> OverrideAsync(
        Guid userId, string state, string reason, DateTimeOffset? expiresAt, Guid actorId,
        CancellationToken cancellationToken);

    /// <summary>An appeal restored a level (US-6A.8, D3' <c>POST /v1/admin/drivers/{id}/level/restore</c>).</summary>
    Task<DriverLevelRow> RestoreLevelAsync(
        Guid driverId, int level, string reason, Guid actorId, CancellationToken cancellationToken);

    /// <summary>
    /// Settles block states whose time box has passed. Returns how many were settled.
    /// </summary>
    Task<int> SettleExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IReputationService"/>
public sealed class ReputationService(
    IUnitOfWorkFactory unitOfWorkFactory,
    INpgsqlConnectionFactory connections,
    ICounterRepository counters,
    IBlockStateRepository blockStates,
    IIntakeLogRepository intakeLog,
    IDriverLevelRepository levels,
    IAuditRepository audit,
    IOutboxWriter outbox,
    IBlockStatusCache cache,
    TimeProvider clock,
    IOptions<ReputationOptions> options,
    ILogger<ReputationService> logger) : IReputationService
{
    private readonly ReputationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<ReputationStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (await cache.TryGetAsync(userId, cancellationToken) is { } cached)
        {
            // A time-boxed state can lapse while it is cached. Applying the box on read means the
            // caller never sees a delisting that ended two minutes ago, without waiting for the
            // sweep — the sweep's job is to make it durable and to publish it, not to be correct
            // first.
            return Lapse(cached, clock.GetUtcNow());
        }

        var status = await ReadStatusAsync(userId, cancellationToken);
        await cache.SetAsync(status, cancellationToken);

        return status;
    }

    public async Task<DriverLevelRow> GetLevelAsync(Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // No row means nothing has happened to this driver yet, which D5' §4.2 says is Level 3 —
        // not "unknown". Materialising one on a read would make a query a write.
        return await levels.FindAsync(connection, null, driverId, cancellationToken)
            ?? DriverLevelRow.Default(driverId, _options.LevelUpThreshold);
    }

    public async Task<IntakeOutcome> RecordAsync(ReputationFact fact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);

        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var claimed = await intakeLog.TryClaimAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fact, now, cancellationToken);

        if (!claimed)
        {
            // Already counted. Roll the transaction back rather than committing a no-op and answer
            // with what is currently true — a redelivered event is not an error (D6' §2.3).
            await unitOfWork.RollbackAsync(cancellationToken);

            var current = await GetStatusAsync(fact.SubjectId, cancellationToken);
            var currentLevel = fact.SubjectRole == SubjectRoles.Driver
                ? (await GetLevelAsync(fact.SubjectId, cancellationToken)).Level
                : (int?)null;

            return new IntakeOutcome(current, Counted: false, Duplicate: true, Level: currentLevel);
        }

        // Block state before counters — the one lock order this service has; see
        // IBlockStateRepository.LockAsync for why the expiry sweep makes it mandatory.
        var existing = await blockStates.LockAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fact.SubjectId, cancellationToken);

        var before = await counters.LockAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fact.SubjectId, now, cancellationToken);
        var after = ReputationRules.Apply(before, fact, _options, now);

        await counters.SaveAsync(unitOfWork.Connection, unitOfWork.Transaction, after, cancellationToken);

        var decision = ReputationRules.Resolve(
            existing,
            ReputationRules.Derive(after, _options, now),
            ReputationRules.DeriveFromEvent(fact, _options, now),
            now);

        var manual = existing is { Source: BlockSources.Manual } && existing.HoldsAt(now);

        await blockStates.UpsertAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            fact.SubjectId,
            decision,
            // A manual pin keeps its provenance through a recompute; without this the first counted
            // fact after an override would quietly turn it back into an automatic state and the
            // next one would overwrite it.
            manual ? BlockSources.Manual : BlockSources.Auto,
            manual ? existing.SetBy : null,
            cancellationToken);

        var level = await ApplyLevelPenaltyAsync(unitOfWork, fact, after, now, cancellationToken);

        var status = ReputationStatus.From(
            new BlockStateRow(
                fact.SubjectId,
                decision.State,
                decision.ExpiresAt,
                manual ? BlockSources.Manual : BlockSources.Auto,
                decision.Reason,
                manual ? existing.SetBy : null,
                now),
            after);

        // BlockStateRepository.LockAsync materialises an OK row when there was none, so this is
        // always the state the subject was actually in — a first fact that changes nothing
        // publishes nothing.
        var previous = existing.State;

        if (previous != decision.State)
        {
            await PublishStateChangeAsync(
                unitOfWork, status, previous, fact.RideId, actorId: null, now, cancellationToken);
        }

        await unitOfWork.CommitAsync(cancellationToken);
        await cache.InvalidateAsync(fact.SubjectId, cancellationToken);

        if (previous != decision.State)
        {
            logger.LogInformation(
                "{Subject} moved {From} → {State} ({Reason}) after {Kind} on ride {RideId}",
                fact.SubjectId, previous, decision.State, decision.Reason, fact.Kind, fact.RideId);
        }

        return new IntakeOutcome(status, fact.Counted, Duplicate: false, Level: level);
    }

    public async Task<ReputationStatus> OverrideAsync(
        Guid userId, string state, string reason, DateTimeOffset? expiresAt, Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!BlockStates.IsKnown(state))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["state"] = [$"state must be one of {string.Join(", ", BlockStates.All)}."],
            });
        }

        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var existing = await blockStates.LockAsync(
            unitOfWork.Connection, unitOfWork.Transaction, userId, cancellationToken);

        // Setting OK returns the user to automatic control outright: an "OK until Tuesday" override
        // would be a block scheduled for Tuesday, which is not what an admin lifting a block means.
        // Everything else is pinned until it expires or another admin decides.
        var clearing = state == BlockStates.Ok;

        var counterRow = clearing
            ? await counters.LockAsync(unitOfWork.Connection, unitOfWork.Transaction, userId, now, cancellationToken)
            : await counters.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, userId, cancellationToken)
              ?? CounterRow.Empty(userId, now);

        if (clearing)
        {
            // **Reinstatement forgives the counters**, and this is the part that makes the route
            // worth having. AL-16 calls the outcome "access restored"; leaving the tallies at three
            // would restore it for exactly as long as it took to recompute — the very next counted
            // fact would derive the same block back, and the admin's decision would look like it
            // had silently failed. Clearing every counter is also the honest reading of an admin
            // saying this person is fine.
            counterRow = counterRow with
            {
                CancellationsContinuous = 0,
                ReportsTotal = 0,
                NoShows = 0,
                WindowStartedAt = now,
            };

            await counters.SaveAsync(unitOfWork.Connection, unitOfWork.Transaction, counterRow, cancellationToken);
        }

        var decision = clearing
            ? BlockDecision.Clear
            : new BlockDecision(state, BlockReasons.Manual, expiresAt);

        await blockStates.UpsertAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            userId,
            decision,
            clearing ? BlockSources.Auto : BlockSources.Manual,
            clearing ? null : actorId,
            cancellationToken);

        await audit.WriteAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            actorId,
            AuditRepository.BlockStateOverride,
            "reputation.block_state",
            userId,
            before: new { state = existing.State, source = existing.Source, expiresAt = existing.ExpiresAt },
            after: new { state = decision.State, source = clearing ? BlockSources.Auto : BlockSources.Manual, expiresAt = decision.ExpiresAt, reason },
            now,
            cancellationToken);

        var status = ReputationStatus.From(
            new BlockStateRow(
                userId,
                decision.State,
                decision.ExpiresAt,
                clearing ? BlockSources.Auto : BlockSources.Manual,
                clearing ? decision.Reason : BlockReasons.Manual,
                clearing ? null : actorId,
                now),
            counterRow);

        await PublishStateChangeAsync(
            unitOfWork, status, existing.State, rideId: null, actorId, now, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);
        await cache.InvalidateAsync(userId, cancellationToken);

        return status;
    }

    public async Task<DriverLevelRow> RestoreLevelAsync(
        Guid driverId, int level, string reason, Guid actorId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (level is < 1 or > 3)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["level"] = ["level must be between 1 and 3 (D5' §4.2)."],
            });
        }

        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var before = await levels.LockAsync(
            unitOfWork.Connection, unitOfWork.Transaction, driverId, _options.LevelUpThreshold, cancellationToken);

        await levels.SetLevelAsync(unitOfWork.Connection, unitOfWork.Transaction, driverId, level, cancellationToken);

        // Restoring to the level the driver already holds is a no-op that still records the
        // decision — the contract says so, and an appeal that was heard and refused is exactly the
        // thing an auditor later wants to find.
        await audit.WriteAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            actorId,
            AuditRepository.LevelRestore,
            "dispatch.driver_level",
            driverId,
            before: new { level = before.Level },
            after: new { level, reason },
            now,
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return before with { Level = level };
    }

    public async Task<int> SettleExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var expired = await blockStates.ClaimExpiredAsync(
            unitOfWork.Connection, unitOfWork.Transaction, now, _options.ExpiryBatchSize, cancellationToken);

        if (expired.Count == 0)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            return 0;
        }

        foreach (var row in expired)
        {
            var counterRow = await counters.LockAsync(
                unitOfWork.Connection, unitOfWork.Transaction, row.UserId, now, cancellationToken);

            // The strike has been served, so what caused it is forgiven — otherwise the recompute
            // below would find the same three reports and delist the driver again the instant the
            // box lapsed, which is a permanent ban wearing a time box's clothes.
            counterRow = row.Reason switch
            {
                BlockReasons.ReportsDelist => counterRow with { ReportsTotal = 0, WindowStartedAt = now },
                BlockReasons.CancellationsDisabled => counterRow with { CancellationsContinuous = 0 },
                _ => counterRow,
            };

            await counters.SaveAsync(unitOfWork.Connection, unitOfWork.Transaction, counterRow, cancellationToken);

            var decision = ReputationRules.Derive(counterRow, _options, now);

            await blockStates.UpsertAsync(
                unitOfWork.Connection, unitOfWork.Transaction, row.UserId, decision, BlockSources.Auto,
                setBy: null, cancellationToken);

            var status = ReputationStatus.From(
                new BlockStateRow(
                    row.UserId, decision.State, decision.ExpiresAt, BlockSources.Auto, decision.Reason, null, now),
                counterRow);

            if (decision.State != row.State)
            {
                await PublishStateChangeAsync(
                    unitOfWork, status, row.State, rideId: null, actorId: null, now, cancellationToken);
            }
        }

        await unitOfWork.CommitAsync(cancellationToken);

        foreach (var row in expired)
        {
            await cache.InvalidateAsync(row.UserId, cancellationToken);
        }

        return expired.Count;
    }

    // -------------------------------------------------------------------------------------

    private async Task<int?> ApplyLevelPenaltyAsync(
        IUnitOfWork unitOfWork, ReputationFact fact, CounterRow after, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (fact.SubjectRole != SubjectRoles.Driver)
        {
            return null;
        }

        var current = await levels.LockAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fact.SubjectId, _options.LevelUpThreshold,
            cancellationToken);

        var penalty = ReputationRules.LevelPenalty(fact, after, _options);

        if (penalty == 0)
        {
            return current.Level;
        }

        // Floor at 1. D5' §4.2 is explicit that Level 1 is "NOT a permanent ban" — it costs the Job
        // Board and scheduled rides (US-6A.8) and immediate Mode C keeps working, so there is
        // nothing below it to fall to.
        var next = Math.Max(1, current.Level - penalty);

        if (next == current.Level)
        {
            return current.Level;
        }

        await levels.SetLevelAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fact.SubjectId, next, cancellationToken);

        // No actor: the rule decided, not a person. audit.events.actor_id is nullable for exactly
        // this (migration 1305), and an appeal against an automatic decrement needs to be able to
        // find it.
        await audit.WriteAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            actorId: null,
            AuditRepository.LevelDecrement,
            "dispatch.driver_level",
            fact.SubjectId,
            before: new { level = current.Level },
            after: new { level = next, reason = fact.Kind, rideId = fact.RideId },
            now,
            cancellationToken);

        return next;
    }

    private Task PublishStateChangeAsync(
        IUnitOfWork unitOfWork,
        ReputationStatus status,
        string? previous,
        Guid? rideId,
        Guid? actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payload = new BlockStateChangedPayload(
            UserId: status.UserId,
            PreviousState: previous,
            State: status.State,
            Reason: status.Reason ?? BlockReasons.Clear,
            Source: status.Source,
            ExpiresAt: status.ExpiresAt,
            DispatchEligible: status.AllowsDispatch,
            CancellationsContinuous: status.CancellationsContinuous,
            ReportsTotal: status.ReportsTotal,
            NoShows: status.NoShows,
            RideId: rideId,
            ActorId: actorId);

        return outbox.WriteAsync(
            unitOfWork, ReputationEvents.BlockStateChanged(payload, Guid.NewGuid(), now), cancellationToken);
    }

    private async Task<ReputationStatus> ReadStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var state = await blockStates.FindAsync(connection, null, userId, cancellationToken);

        if (state is null)
        {
            // Nothing has ever happened to this user. OK, and deliberately not persisted: a
            // dispatch round that asks about a thousand drivers must not create a thousand rows.
            return ReputationStatus.Clear(userId);
        }

        var counterRow = await counters.FindAsync(connection, null, userId, cancellationToken)
                         ?? CounterRow.Empty(userId, state.UpdatedAt);

        return Lapse(ReputationStatus.From(state, counterRow), clock.GetUtcNow());
    }

    /// <summary>Applies a time box that has passed, without writing anything.</summary>
    private static ReputationStatus Lapse(ReputationStatus status, DateTimeOffset now) =>
        status.ExpiresAt is { } expires && expires <= now
            ? status with { State = BlockStates.Ok, Reason = BlockReasons.Clear, ExpiresAt = null }
            : status;
}
