using Grpc.Core;
using MageRide.Reputation.Grpc;
using MageRide.Safety.Configuration;
using MageRide.Safety.Domain;
using Microsoft.Extensions.Options;
using ReputationClient = MageRide.Reputation.Grpc.Reputation.ReputationClient;

namespace MageRide.Safety.Clients;

/// <summary>Tells reputation-svc about a vehicle report (US-12.5, D-04).</summary>
/// <remarks>
/// <para>
/// <b>The report is filed here and counted there</b> — <c>reputation.v1.proto</c>'s own comment
/// draws the line: "safety-svc owns the confirmation decision and <c>safety.vehicle_reports</c>,
/// this service owns the tally". So this hop carries a decision that has already been made and is
/// already durable; it is not the transaction.
/// </para>
/// <para>
/// <b>A failed hop is loud and does not fail the caller.</b> The passenger's report is committed
/// before this runs, and answering 500 would invite a retry that files a second report for the same
/// complaint. reputation-svc dedupes on <c>report_id</c>, so a later replay counts once.
/// </para>
/// </remarks>
public interface IReputationReporter
{
    /// <summary>Reports one vehicle report at its current status. False when it did not get through.</summary>
    Task<bool> ReportAsync(
        Guid reportId,
        Guid? driverId,
        Guid vehicleId,
        Guid reporterId,
        Guid? rideId,
        string reason,
        string status,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IReputationReporter"/>
internal sealed class ReputationReporter(
    ReputationClient client,
    IOptions<SafetyOptions> options,
    ILogger<ReputationReporter> logger) : IReputationReporter
{
    /// <summary>The interim shared secret, until the mesh provides mTLS (C042). Lower-case: gRPC metadata keys are.</summary>
    public const string InternalKeyHeader = "x-mageride-internal-key";

    private readonly SafetyOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<bool> ReportAsync(
        Guid reportId,
        Guid? driverId,
        Guid vehicleId,
        Guid reporterId,
        Guid? rideId,
        string reason,
        string status,
        CancellationToken cancellationToken)
    {
        if (!_options.ReputationReportingEnabled)
        {
            return false;
        }

        if (driverId is not { } driver)
        {
            // `reputation.counters` is keyed by user, so a report that cannot name a driver has no
            // counter to move. It stays on the moderation queue, where a human can still act on it.
            logger.LogWarning(
                "Vehicle report {ReportId} names no driver, so reputation-svc has no counter to move (US-12.6).",
                reportId);

            return false;
        }

        var request = new VehicleReport
        {
            ReportId = reportId.ToString(),
            DriverId = driver.ToString(),
            VehicleId = vehicleId.ToString(),
            ReporterId = reporterId.ToString(),
            RideId = rideId?.ToString() ?? string.Empty,
            Reason = reason,
            Status = Map(status),
        };

        var metadata = new Metadata();

        if (!string.IsNullOrWhiteSpace(_options.ReputationInternalKey))
        {
            metadata.Add(InternalKeyHeader, _options.ReputationInternalKey);
        }

        try
        {
            var ack = await client.ReportVehicleAsync(
                request,
                metadata,
                deadline: DateTime.UtcNow.Add(_options.ReputationTimeout),
                cancellationToken: cancellationToken);

            logger.LogInformation(
                "Reported vehicle report {ReportId} ({Status}) to reputation-svc: counted={Counted} duplicate={Duplicate} state={State}",
                reportId, status, ack.Counted, ack.Duplicate, ack.State);

            return true;
        }
        catch (RpcException exception)
        {
            // Deliberately not rethrown: the report is already durable and a 500 here would make the
            // passenger file it again.
            logger.LogError(
                exception,
                "reputation-svc did not accept vehicle report {ReportId} ({Code}); the counter has not moved and "
                + "the three-strike delisting will be short by one until it is replayed.",
                reportId, exception.StatusCode);

            return false;
        }
    }

    private static ReportStatus Map(string status) => status switch
    {
        VehicleReportStatuses.Confirmed => ReportStatus.Confirmed,
        VehicleReportStatuses.Dismissed => ReportStatus.Dismissed,
        _ => ReportStatus.Pending,
    };
}
