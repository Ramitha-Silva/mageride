using MageRide.Safety.Clients;
using MageRide.Safety.Configuration;
using MageRide.Safety.Domain;
using MageRide.Safety.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Safety.Reports;

/// <summary>What a moderator's decision produced.</summary>
/// <param name="ConfirmedTotal">CONFIRMED reports against the vehicle after this decision.</param>
/// <param name="Delisted">
/// True when this decision was the one that reached the threshold — US-12.6's "three confirmed
/// reports auto-delist", which is a consequence of the third confirmation rather than a separate
/// admin action.
/// </param>
public sealed record ResolvedReport(VehicleReport Report, int ConfirmedTotal, bool Delisted);

/// <summary>US-12.5, US-12.6 and US-12.10 — reports, their moderation, and passenger blocks.</summary>
public interface IReportService
{
    Task<VehicleReport> ReportVehicleAsync(
        Guid reporterId, Guid vehicleId, Guid? rideId, string reason, CancellationToken cancellationToken);

    Task<ResolvedReport> ResolveAsync(
        Guid reportId, string decision, Guid? resolvedBy, string? note, CancellationToken cancellationToken);

    Task<IReadOnlyList<VehicleReport>> QueueAsync(
        DateTimeOffset? before, int limit, CancellationToken cancellationToken);

    Task BlockAsync(Guid passengerId, Guid driverId, CancellationToken cancellationToken);

    Task UnblockAsync(Guid passengerId, Guid driverId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IReportService"/>
/// <remarks>
/// <para>
/// <b>The decision is this service's; the tally is reputation-svc's.</b>
/// <c>reputation.v1.proto</c>'s own comment draws that line, and it is why a confirmation here is
/// one transaction (the status, its evidence and the outbox row) followed by one gRPC hop that
/// cannot roll it back.
/// </para>
/// <para>
/// <b>The third confirmation and the count that makes it the third are one atomic fact.</b> Both
/// happen inside the same transaction: two moderators confirming the second and third report at the
/// same instant would otherwise both read two, and nothing would delist.
/// </para>
/// </remarks>
internal sealed class ReportService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IReportRepository reports,
    IDriverDirectory drivers,
    IOutboxWriter outbox,
    IReputationReporter reputation,
    IOptions<SafetyOptions> options,
    TimeProvider clock,
    ILogger<ReportService> logger) : IReportService
{
    private readonly SafetyOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<VehicleReport> ReportVehicleAsync(
        Guid reporterId, Guid vehicleId, Guid? rideId, string reason, CancellationToken cancellationToken)
    {
        if (!await drivers.VehicleExistsAsync(vehicleId, cancellationToken))
        {
            throw new MageRideException(MageRideErrors.VehicleNotFound, $"No vehicle {vehicleId}.");
        }

        // Resolved now and stored, because `reputation.counters` is keyed by *user* and the ride
        // that names the driver is terminal by the time anybody moderates the report. Re-deriving it
        // at confirmation time would answer differently once the vehicle changed hands (0905).
        var driverId = rideId is { } ride
            ? await drivers.FindRideDriverAsync(ride, cancellationToken)
            : null;

        VehicleReport report;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            report = await reports.CreateAsync(
                unitOfWork, reporterId, vehicleId, rideId, driverId, reason, cancellationToken);

            await outbox.WriteAsync(
                unitOfWork,
                SafetyEvents.VehicleReported(report.Id, vehicleId, driverId, reporterId, rideId, reason),
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        // After the commit: the report is durable, and a failed hop must not make the passenger file
        // it again. reputation-svc dedupes on report_id, so a later replay counts once.
        await reputation.ReportAsync(
            report.Id, driverId, vehicleId, reporterId, rideId, reason, VehicleReportStatuses.Pending, cancellationToken);

        logger.LogInformation(
            "Vehicle {VehicleId} reported by {ReporterId} on ride {RideId} (report {ReportId}).",
            vehicleId, reporterId, rideId, report.Id);

        return report;
    }

    public async Task<ResolvedReport> ResolveAsync(
        Guid reportId, string decision, Guid? resolvedBy, string? note, CancellationToken cancellationToken)
    {
        if (!VehicleReportStatuses.IsDecision(decision))
        {
            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["decision"] = ["decision must be CONFIRMED or DISMISSED."],
                },
                "A report is resolved by confirming or dismissing it.");
        }

        VehicleReport resolved;
        int confirmed;
        bool delisted;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            var moved = await reports.ResolveAsync(
                unitOfWork, reportId, decision, resolvedBy, note, clock.GetUtcNow(), cancellationToken);

            if (moved is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken);

                // Told apart by reading the row: a report that does not exist and one that another
                // moderator has already decided are different answers, and collapsing them would
                // make a race look like a typo.
                var existing = await reports.FindAsync(reportId, cancellationToken);

                throw existing is null
                    ? new MageRideException(MageRideErrors.NotFound, $"No report {reportId}.")
                    : new MageRideException(
                        MageRideErrors.Conflict, $"Report {reportId} was already resolved as {existing.Status}.");
            }

            resolved = moved;

            // Inside the same transaction as the status it counts — see the remarks.
            confirmed = await reports.CountConfirmedAsync(unitOfWork, resolved.VehicleId, cancellationToken);

            delisted = string.Equals(decision, VehicleReportStatuses.Confirmed, StringComparison.Ordinal)
                       && confirmed == _options.ReportDelistThreshold;

            await outbox.WriteAsync(
                unitOfWork,
                SafetyEvents.VehicleReportResolved(
                    resolved.Id, resolved.VehicleId, resolved.DriverId, decision, confirmed, delisted),
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        // The tally lives in reputation-svc; this is what tells it the report is now CONFIRMED and
        // lets D-04 move the driver's block state. The delisting itself is that service's.
        await reputation.ReportAsync(
            resolved.Id,
            resolved.DriverId,
            resolved.VehicleId,
            resolved.ReporterId,
            resolved.RideId,
            resolved.Reason,
            decision,
            cancellationToken);

        if (delisted)
        {
            logger.LogWarning(
                "Vehicle {VehicleId} reached {Threshold} confirmed reports and is delisted (US-12.6); report {ReportId} was the third.",
                resolved.VehicleId, _options.ReportDelistThreshold, resolved.Id);
        }

        return new ResolvedReport(resolved, confirmed, delisted);
    }

    public Task<IReadOnlyList<VehicleReport>> QueueAsync(
        DateTimeOffset? before, int limit, CancellationToken cancellationToken) =>
        reports.ListPendingAsync(before, Math.Clamp(limit, 1, _options.MaxPageSize), cancellationToken);

    public async Task BlockAsync(Guid passengerId, Guid driverId, CancellationToken cancellationToken)
    {
        if (passengerId == driverId)
        {
            // `ck_blocked_drivers_not_self` would refuse it anyway; this is the readable answer.
            // A self-block would silently shrink a driver's own candidate set if they also ride.
            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["driverId"] = ["You cannot block yourself."],
                },
                "A passenger cannot block their own account.");
        }

        if (await reports.BlockAsync(passengerId, driverId, cancellationToken))
        {
            // No event. dispatch-svc reads `safety.blocked_drivers` directly in its candidate query
            // (`CandidateRepository`, US-12.10), so the row *is* the mechanism — an event announcing
            // it would have no consumer that could act any sooner than the next dispatch round.
            logger.LogInformation("Passenger {PassengerId} blocked driver {DriverId}.", passengerId, driverId);
        }
    }

    public async Task UnblockAsync(Guid passengerId, Guid driverId, CancellationToken cancellationToken)
    {
        if (!await reports.UnblockAsync(passengerId, driverId, cancellationToken))
        {
            // 404, not 204: a client that thinks it cleared a block that was never there would show
            // the driver as available when nothing changed.
            throw new MageRideException(MageRideErrors.NotFound, "No such block.");
        }

        logger.LogInformation("Passenger {PassengerId} unblocked driver {DriverId}.", passengerId, driverId);
    }
}
