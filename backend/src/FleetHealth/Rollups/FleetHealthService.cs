using Dapper;
using MageRide.FleetHealth.Configuration;
using MageRide.FleetHealth.Domain;
using MageRide.FleetHealth.Persistence;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.FleetHealth.Rollups;

/// <summary>Everything <c>GET /v1/fleets/{fleetId}/health</c> answers with (US-3.13, US-3.16).</summary>
public sealed record FleetHealthSnapshot(
    Guid FleetId,
    TrackerStateCounts Counts,
    HealthThresholds Thresholds,
    FleetWindowRollup Window,
    double ThresholdPct,
    FleetHealthAlert? Alert,
    IReadOnlyList<DeviceHealthRow> Items,
    bool ItemsTruncated,
    DateTimeOffset AsOf);

/// <summary>The read model behind the Fleet Portal's tracker-health dashboard.</summary>
public interface IFleetHealthService
{
    /// <summary>Reads one fleet's health, scoped in the database to that fleet.</summary>
    Task<FleetHealthSnapshot> ReadAsync(Guid fleetId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetHealthService"/>
/// <remarks>
/// <para>
/// <b>The scoping is a session GUC and a security-barrier view, not a <c>WHERE</c> clause this code
/// could forget.</b> ADD §9.5 item 8 asks for row-level security on <c>fleet_id</c> "without
/// application-side filtering risk" and ADD §7.7.7 names this service as one of the two that apply it.
/// So the transaction sets <c>app.fleet_id</c> and every read goes through a <c>*_fleet</c> view whose
/// predicate is <c>telemetry.current_fleet_id()</c> — which returns NULL when the GUC is unset and
/// therefore matches no row. A bug that dropped the <c>set_config</c> would produce an empty dashboard,
/// never another organisation's devices.
/// </para>
/// <para>
/// <b>One transaction for all four reads.</b> Not for isolation's sake — every row is a rollup and a
/// concurrent flush changing one is fine — but because <c>SET LOCAL</c> is transaction-scoped, and
/// because it is the only way the counts, the items and the window are guaranteed to describe the same
/// instant. A dashboard whose totals did not add up to its own list would be reported as a bug for
/// years.
/// </para>
/// <para>
/// <b><c>ReadCommitted</c>, and <c>SET LOCAL</c> rather than <c>SET</c>.</b> ADD §9.3 puts this behind
/// PgBouncer in transaction mode, where consecutive statements are not promised the same backend and a
/// session-scoped <c>SET</c> would leak one caller's fleet onto the next caller's connection. That is
/// the whole reason the third argument to <c>set_config</c> is <see langword="true"/>.
/// </para>
/// <para>
/// <b><c>items</c> is capped and says so.</b> The contract has no pagination — D3' gives the operation a
/// flat array — so the cap is <c>Health:MaxItems</c> and the response carries <c>itemsTruncated</c>. The
/// counts are computed by a <c>GROUP BY</c> over the whole fleet and are never affected by it, so a
/// truncated list cannot read as a smaller fleet.
/// </para>
/// </remarks>
public sealed class FleetHealthService(
    IDeviceHealthRepository devices,
    IFleetRollupRepository rollups,
    IUnitOfWorkFactory unitOfWorkFactory,
    IOptions<FleetHealthOptions> options,
    TimeProvider clock) : IFleetHealthService
{
    /// <summary>
    /// Sets the fleet scope for the rest of the transaction. The third argument is
    /// <c>is_local = true</c>, which is what confines it to this transaction.
    /// </summary>
    private const string ScopeSql = "SELECT set_config('app.fleet_id', @FleetId, true);";

    private readonly FleetHealthOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<FleetHealthSnapshot> ReadAsync(Guid fleetId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var thresholds = new HealthThresholds(_options.StaleAfter, _options.OfflineAfter);
        var bucket = TimeBuckets.LastClosedStart(now, _options.Window);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            ScopeSql,
            new { FleetId = fleetId.ToString() },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        var counts = await devices.ReadFleetCountsAsync(
            unitOfWork.Connection, unitOfWork.Transaction, thresholds, now, cancellationToken);

        // One more than the cap, so a full page and a truncated one are distinguishable without a
        // second count.
        var rows = await devices.ReadFleetDevicesAsync(
            unitOfWork.Connection, unitOfWork.Transaction, thresholds, now, _options.MaxItems + 1, cancellationToken);

        var window = await rollups.ReadFleetWindowAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, bucket, bucket + _options.Window, cancellationToken);

        var alert = await rollups.ReadLatestAlertAsync(
            unitOfWork.Connection, unitOfWork.Transaction, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        var truncated = rows.Count > _options.MaxItems;
        var items = truncated ? rows.Take(_options.MaxItems).ToArray() : rows;

        return new FleetHealthSnapshot(
            fleetId, counts, thresholds, window, _options.OfflinePct, alert, items, truncated, now);
    }
}
