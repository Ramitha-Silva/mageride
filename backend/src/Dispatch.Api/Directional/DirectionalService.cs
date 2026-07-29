using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Persistence;
using MageRide.Dispatch.Presence;
using MageRide.Dispatch.Redis;
using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using MageRide.Shared.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Dispatch.Directional;

/// <summary>The body of <c>POST /v1/standby/directional</c>, before validation.</summary>
public sealed record SetDirectionalCommand(Guid DriverId, StandbyPlace? Destination, string? Label);

/// <summary>
/// The body of <c>PUT /v1/admin/dispatch/directional-config</c>. Every member optional so a partial
/// PUT keeps whatever is live rather than resetting it to a default.
/// </summary>
public sealed record DirectionalConfigUpdate(
    int? ThetaMaxDeg,
    int? DetourMaxM,
    int? ProgressMinM,
    int? MaxUsesPerDay,
    int? MaxDurationSec,
    bool? ClearOnFirstTrip);

/// <summary>
/// Directional Travel's lifecycle: set, read, clear, expire and remind (DT-01, DT-03, DT-04, DT-08).
/// </summary>
public interface IDirectionalService
{
    /// <summary>DT-08's live state for the driver's own filter card.</summary>
    Task<DirectionalState> GetAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// DT-01: consumes one daily use, writes the filter, and arms its expiry and 10-minute reminder.
    /// </summary>
    Task<DirectionalState> SetAsync(SetDirectionalCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// The driver turned it off early (<c>DELETE</c>). <b>Still consumes the use</b> (US-6A.19); a
    /// driver with nothing active gets the contract's <c>404</c>.
    /// </summary>
    Task<DirectionalState> TurnOffAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// Clears whatever is active for one of DT-04's non-manual reasons — expiry, going offline, the
    /// R-15 last will, or <c>clear_on_first_trip</c>. A no-op when nothing is active.
    /// </summary>
    Task<bool> ClearAsync(Guid driverId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// The driver was matched to a ride. Clears the filter only if an admin has turned
    /// <c>clear_on_first_trip</c> on — it is off by default and no spec asks for it to be on.
    /// </summary>
    Task OnTripMatchedAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>Fires one due <c>directional_expiry</c> or <c>directional_reminder</c> timer.</summary>
    Task RunTimerAsync(DueDispatchTimer timer, CancellationToken cancellationToken);

    Task<DirectionalConfigRow> GetConfigAsync(CancellationToken cancellationToken);

    Task<DirectionalConfigRow> UpdateConfigAsync(
        DirectionalConfigUpdate update, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDirectionalService"/>
/// <remarks>
/// <para>
/// <b>An activation is one statement, and the statement is the limit.</b> DT-03's budget is
/// <c>COUNT(*) per (driver_id, used_date)</c> over the activation rows themselves, evaluated inside
/// the <c>INSERT</c> that would consume it — so two taps of Set arriving together cannot both spend
/// the driver's last use, and no counter anywhere has to be decremented when a filter is turned off.
/// The Redis counter is incremented afterwards as ADD §9.4's mirror and is never consulted.
/// </para>
/// <para>
/// <b>Clearing is idempotent and emits at most once.</b> Every one of DT-04's four paths funnels
/// through the same conditional <c>UPDATE … WHERE cleared_at IS NULL</c>: whichever gets there first
/// wins the row, and the ones behind it update nothing and emit nothing. This matters because at
/// least two of them race by construction — a driver who goes offline at the moment their filter
/// expires triggers both.
/// </para>
/// <para>
/// <b>Two timers, because <c>ux_dispatch_timers_driver_live</c> is per (driver, kind).</b> The
/// expiry is DT-04's source of truth; the reminder is US-10.14's 10-minute warning. Both are retired
/// the moment the filter clears, so a driver who turns theirs off two minutes in is not told nine
/// minutes later that it is about to end.
/// </para>
/// </remarks>
public sealed class DirectionalService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IDirectionalRepository directional,
    IPresenceRepository presence,
    IDispatchTimerRepository timers,
    IDirectionalCache cache,
    IOutboxWriter outbox,
    IOptions<DispatchOptions> options,
    TimeProvider timeProvider,
    ILogger<DirectionalService> logger) : IDirectionalService
{
    /// <summary>The contract's <c>label</c> ceiling (<c>dispatch.yaml</c>, <c>maxLength: 60</c>).</summary>
    private const int MaxLabelLength = 60;

    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<DirectionalState> GetAsync(Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var config = await directional.GetConfigAsync(connection, cancellationToken);
        var filter = await directional.FindActiveAsync(connection, null, driverId, cancellationToken);

        return await BuildStateAsync(connection, driverId, filter, config, cancellationToken);
    }

    public async Task<DirectionalState> SetAsync(SetDirectionalCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var destination = RequireDestination(command.Destination);
        var label = RequireLabel(command.Label);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        // 403 not-online (D3'). A filter belongs to a driver who is on standby: the predicate only
        // ever runs over candidates, and an offline driver is not one — so accepting the filter
        // would consume a daily use for a preference nothing could act on.
        var row = await presence.FindAsync(connection, null, command.DriverId, cancellationToken);

        if (row is null || row.State == PresenceStates.Offline)
        {
            throw new MageRideException(
                MageRideErrors.NotOnline,
                "Go on standby before setting a Directional Travel filter (POST /v1/standby/online).");
        }

        var config = await directional.GetConfigAsync(connection, cancellationToken);

        // A second filter would need a second activation row, which ux_directional_active refuses —
        // so the answer is a 409 rather than a unique-violation 500. Changing destination means
        // turning the current one off first, and that costs a use: US-6A.19's anti-gaming rule is
        // exactly what makes "just re-point it" not free.
        if (await directional.FindActiveAsync(connection, null, command.DriverId, cancellationToken) is { } live)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"A Directional Travel filter is already active until {live.ExpiresAt:O}. Turn it off first " +
                "(DELETE /v1/standby/directional) — note that doing so still consumes its daily use (US-6A.19).");
        }

        var now = timeProvider.GetUtcNow();
        var (usedDate, usedDateTzAt) = BusinessCalendar.Stamp(now);
        var expiresAt = now.Add(config.MaxDuration);

        DirectionalFilterRow filter;
        int usesRemaining;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            var activated = await directional.TryActivateAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                command.DriverId,
                destination,
                label,
                expiresAt,
                usedDate,
                usedDateTzAt,
                config.MaxUsesPerDay,
                cancellationToken);

            if (activated is null)
            {
                // The insert's own WHERE refused: today's budget is spent. US-6A.18's 409, and the
                // one case where nothing at all has been written.
                await unitOfWork.RollbackAsync(cancellationToken);

                throw new MageRideException(
                    MageRideErrors.DirectionalLimitReached,
                    $"Directional Travel is limited to {config.MaxUsesPerDay} activations per day " +
                    $"(Asia/Colombo). Today's are used; the next reset is at midnight local time.");
            }

            filter = activated;

            // Belt and braces against a timer left behind by a filter that was cleared without its
            // timers being retired — a crash between the two commits. ArmDriverTimerAsync is
            // DO NOTHING on the live-timer index, so an orphan would otherwise make this filter
            // inherit the previous one's fire time.
            foreach (var kind in DispatchTimerKinds.Directional)
            {
                await timers.RetireForDriverAsync(
                    unitOfWork.Connection, unitOfWork.Transaction, command.DriverId, kind, cancellationToken);
            }

            await timers.ArmDriverTimerAsync(
                unitOfWork.Connection, unitOfWork.Transaction, command.DriverId,
                DispatchTimerKinds.DirectionalExpiry, expiresAt, payload: null, cancellationToken);

            // US-10.14's reminder, and only when there is something to remind about: a filter whose
            // whole duration is shorter than the lead would otherwise be announced as expiring at
            // the moment it was set.
            var remindAt = expiresAt - _options.DirectionalReminderLead;

            if (remindAt > now)
            {
                await timers.ArmDriverTimerAsync(
                    unitOfWork.Connection, unitOfWork.Transaction, command.DriverId,
                    DispatchTimerKinds.DirectionalReminder, remindAt, payload: null, cancellationToken);
            }

            // Counted inside the same transaction as the insert, so the number the driver is handed
            // back is the one their activation just produced and not a racing replica's.
            usesRemaining = Math.Max(
                0,
                config.MaxUsesPerDay
                - await directional.CountUsesAsync(unitOfWork.Connection, command.DriverId, usedDate, cancellationToken));

            await unitOfWork.CommitAsync(cancellationToken);
        }

        // Redis after the commit, for the reason every other write in this service gives: a hint a
        // ROLLBACK cannot take back would describe a filter the database never accepted.
        await cache.SetAsync(filter, expiresAt - timeProvider.GetUtcNow(), cancellationToken);
        await cache.IncrementUsesAsync(command.DriverId, usedDate, cancellationToken);

        logger.LogInformation(
            "Driver {DriverId} set a Directional Travel filter toward {Destination} until {ExpiresAt:O}; " +
            "{UsesRemaining} of {MaxUses} activations left today (DT-01)",
            command.DriverId, destination, expiresAt, usesRemaining, config.MaxUsesPerDay);

        return new DirectionalState(
            filter, usesRemaining, config.MaxDurationSec, expiresAt - timeProvider.GetUtcNow());
    }

    public async Task<DirectionalState> TurnOffAsync(Guid driverId, CancellationToken cancellationToken)
    {
        var cleared = await ClearCoreAsync(driverId, DirectionalClearReasons.Manual, cancellationToken);

        if (cleared is null)
        {
            // The contract's 404 on DELETE. Deliberately not a 200: "there was nothing to turn off"
            // and "your filter is off" look the same to a driver but not to a client deciding
            // whether a use was just spent.
            throw new MageRideException(
                MageRideErrors.NotFound, "No Directional Travel filter is active for this driver.");
        }

        return cleared.State;
    }

    public async Task<bool> ClearAsync(Guid driverId, string reason, CancellationToken cancellationToken) =>
        await ClearCoreAsync(driverId, reason, cancellationToken) is not null;

    public async Task OnTripMatchedAsync(Guid driverId, CancellationToken cancellationToken)
    {
        DirectionalConfigRow config;

        await using (var connection = await connectionFactory.OpenAsync(cancellationToken))
        {
            config = await directional.GetConfigAsync(connection, cancellationToken);
        }

        if (!config.ClearOnFirstTrip)
        {
            // The default, and D5' §12 gives no rule that says otherwise: a driver who set a filter
            // for the drive home wants it for the whole drive home, not for the first fare of it.
            return;
        }

        if (await ClearAsync(driverId, DirectionalClearReasons.FirstMatchedTrip, cancellationToken))
        {
            logger.LogInformation(
                "Driver {DriverId}'s Directional Travel filter cleared on their first matched trip " +
                "(clear_on_first_trip is on)",
                driverId);
        }
    }

    public async Task RunTimerAsync(DueDispatchTimer timer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);

        if (timer.DriverId is not { } driverId)
        {
            await MarkFiredAsync(timer.Id, cancellationToken);
            return;
        }

        switch (timer.Kind)
        {
            case DispatchTimerKinds.DirectionalExpiry:
                if (await ClearAsync(driverId, DirectionalClearReasons.Expiry, cancellationToken))
                {
                    logger.LogInformation(
                        "Driver {DriverId}'s Directional Travel filter expired; they are back in the full " +
                        "eligible pool (DT-04)",
                        driverId);
                }
                else
                {
                    // Already cleared — they went offline, or turned it off, between the timer being
                    // armed and this sweep. Nothing to do and nothing to say.
                    await MarkFiredAsync(timer.Id, cancellationToken);
                }

                break;

            case DispatchTimerKinds.DirectionalReminder:
                await RemindAsync(timer, driverId, cancellationToken);
                break;

            default:
                await MarkFiredAsync(timer.Id, cancellationToken);
                break;
        }
    }

    public async Task<DirectionalConfigRow> GetConfigAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await directional.GetConfigAsync(connection, cancellationToken);
    }

    public async Task<DirectionalConfigRow> UpdateConfigAsync(
        DirectionalConfigUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var current = await directional.GetConfigAsync(connection, cancellationToken);

        var merged = new DirectionalConfigRow(
            ThetaMaxDeg: update.ThetaMaxDeg ?? current.ThetaMaxDeg,
            DetourMaxM: update.DetourMaxM ?? current.DetourMaxM,
            ProgressMinM: update.ProgressMinM ?? current.ProgressMinM,
            MaxUsesPerDay: update.MaxUsesPerDay ?? current.MaxUsesPerDay,
            MaxDurationSec: update.MaxDurationSec ?? current.MaxDurationSec,
            ClearOnFirstTrip: update.ClearOnFirstTrip ?? current.ClearOnFirstTrip);

        Validate(merged);

        var saved = await directional.UpdateConfigAsync(connection, null, merged, cancellationToken);

        logger.LogInformation(
            "Directional Travel configuration updated: θ≤{Theta}°, detour≤{Detour} m, progress>{Progress} m, " +
            "{Uses} uses/day, {Duration} s, clearOnFirstTrip={ClearOnFirstTrip}",
            saved.ThetaMaxDeg, saved.DetourMaxM, saved.ProgressMinM, saved.MaxUsesPerDay, saved.MaxDurationSec,
            saved.ClearOnFirstTrip);

        return saved;
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>What a clear produced: the row that was cleared and the driver's state after it.</summary>
    private sealed record ClearedFilter(DirectionalFilterRow Row, DirectionalState State);

    /// <summary>
    /// DT-04's one implementation. Returns <see langword="null"/> when nothing was active, which
    /// every caller but the manual <c>DELETE</c> treats as ordinary.
    /// </summary>
    private async Task<ClearedFilter?> ClearCoreAsync(
        Guid driverId, string reason, CancellationToken cancellationToken)
    {
        DirectionalFilterRow cleared;
        DirectionalConfigRow config;
        int usesRemaining;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            config = await directional.GetConfigAsync(unitOfWork.Connection, cancellationToken);

            var row = await directional.ClearAsync(
                unitOfWork.Connection, unitOfWork.Transaction, driverId, reason, cancellationToken);

            if (row is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken);
                return null;
            }

            cleared = row;

            foreach (var kind in DispatchTimerKinds.Directional)
            {
                await timers.RetireForDriverAsync(
                    unitOfWork.Connection, unitOfWork.Transaction, driverId, kind, cancellationToken);
            }

            usesRemaining = Math.Max(
                0,
                config.MaxUsesPerDay - await directional.CountUsesAsync(
                    unitOfWork.Connection, driverId, cleared.UsedDate, cancellationToken));

            // In the transaction, so a driver is never told they are back in the pool before the row
            // that took them out of it says so (D6' §2.4, R-13's rule applied to a smaller fact).
            await outbox.WriteAsync(
                unitOfWork,
                DispatchEvents.DirectionalCleared(cleared, reason, usesRemaining, timeProvider.GetUtcNow()),
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        await cache.ClearAsync(driverId, cancellationToken);

        logger.LogInformation(
            "Driver {DriverId}'s Directional Travel filter {FilterId} cleared ({Reason}); {UsesRemaining} " +
            "activations left today — a turn-off does not give the use back (US-6A.19)",
            driverId, cleared.Id, reason, usesRemaining);

        return new ClearedFilter(
            cleared,
            new DirectionalState(null, usesRemaining, config.MaxDurationSec, TimeSpan.Zero));
    }

    private async Task RemindAsync(DueDispatchTimer timer, Guid driverId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var filter = await directional.FindActiveAsync(
            unitOfWork.Connection, unitOfWork.Transaction, driverId, cancellationToken);

        // Consumed either way: a reminder for a filter that has already gone is not rescheduled, it
        // is dropped. The row stays as the audit that the reminder was due.
        await timers.MarkFiredAsync(unitOfWork.Connection, unitOfWork.Transaction, timer.Id, cancellationToken);

        if (filter is null)
        {
            await unitOfWork.CommitAsync(cancellationToken);
            return;
        }

        var remaining = filter.ExpiresAt - timeProvider.GetUtcNow();

        await outbox.WriteAsync(
            unitOfWork,
            DispatchEvents.DirectionalExpiring(filter, remaining, timeProvider.GetUtcNow()),
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Driver {DriverId}'s Directional Travel filter expires in {Remaining}; DIRECTIONAL_EXPIRING handed " +
            "to notification-svc (DT-08, US-10.14)",
            driverId, remaining);
    }

    private async Task MarkFiredAsync(Guid timerId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await timers.MarkFiredAsync(connection, null, timerId, cancellationToken);
    }

    private async Task<DirectionalState> BuildStateAsync(
        NpgsqlConnection connection,
        Guid driverId,
        DirectionalFilterRow? filter,
        DirectionalConfigRow config,
        CancellationToken cancellationToken)
    {
        // Today's budget, not the cleared filter's: a driver reading the card at 00:05 has a fresh
        // two activations even though the filter they turned off at 23:50 was counted yesterday.
        var today = BusinessCalendar.Today(timeProvider);
        var used = await directional.CountUsesAsync(connection, driverId, today, cancellationToken);

        var remaining = filter is null
            ? TimeSpan.Zero
            : filter.ExpiresAt - timeProvider.GetUtcNow();

        return new DirectionalState(
            filter,
            Math.Max(0, config.MaxUsesPerDay - used),
            config.MaxDurationSec,
            remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }

    private static void Validate(DirectionalConfigRow config)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        // The bounds are dispatch.yaml's DirectionalConfig schema, enforced here because the
        // contract's own validation stops at the gateway and this row drives a hot-path predicate:
        // a θ_max of 400° would silently match every ride and a max_duration of one second would
        // burn a driver's daily budget on nothing.
        if (config.ThetaMaxDeg is < 0 or > 180)
        {
            errors["thetaMaxDeg"] = ["thetaMaxDeg must be between 0 and 180 degrees."];
        }

        if (config.DetourMaxM < 0)
        {
            errors["detourMaxM"] = ["detourMaxM must not be negative."];
        }

        if (config.ProgressMinM < 0)
        {
            errors["progressMinM"] = ["progressMinM must not be negative."];
        }

        if (config.MaxUsesPerDay is < 0 or > short.MaxValue)
        {
            errors["maxUsesPerDay"] = ["maxUsesPerDay must be between 0 and 32767."];
        }

        if (config.MaxDurationSec < 60)
        {
            errors["maxDurationSec"] = ["maxDurationSec must be at least 60 seconds."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }
    }

    private static GeoPoint RequireDestination(StandbyPlace? place)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (place?.Lat is not { } lat || double.IsNaN(lat) || lat is < -90 or > 90)
        {
            errors["destination.lat"] = ["destination.lat is required and must be between -90 and 90."];
        }

        if (place?.Lng is not { } lng || double.IsNaN(lng) || lng is < -180 or > 180)
        {
            errors["destination.lng"] = ["destination.lng is required and must be between -180 and 180."];
        }

        return errors.Count == 0
            ? new GeoPoint(place!.Lat!.Value, place.Lng!.Value)
            : throw new MageRideValidationException(errors);
    }

    private static string? RequireLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var trimmed = label.Trim();

        return trimmed.Length <= MaxLabelLength
            ? trimmed
            : throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["label"] = [$"label must be at most {MaxLabelLength} characters."],
            });
    }
}
