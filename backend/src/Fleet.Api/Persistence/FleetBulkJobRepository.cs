using Dapper;
using MageRide.Fleet.Domain;
using Npgsql;

namespace MageRide.Fleet.Persistence;

/// <summary>
/// <c>registry.fleet_bulk_jobs</c> and its rows — US-13.1's CSV import and the per-row error report.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unlike provisioning-svc's bulk (T-09), there is no worker.</b> A tracker row has a credential
/// to mint against a CA, which is slow and can fail per row, so 0405's rows are drained
/// afterwards; a vehicle row is an <c>INSERT</c>. Validating and importing in one transaction is
/// both simpler and stronger — the job, its rows and the vehicles they created commit together, so
/// a CSV never half-arrives and a poll can never observe a job that is still growing.
/// </para>
/// <para>
/// <b>The failed rows are stored, not just counted.</b> The error report is a projection of them,
/// which is what makes it survive a restart and re-render identically an hour later — the property
/// D3' asks for when it calls the link "downloadable".
/// </para>
/// </remarks>
public interface IFleetBulkJobRepository
{
    /// <summary>Opens a job. Fails on <c>ux_fleet_bulk_jobs_in_flight</c> when one is already running.</summary>
    Task<Guid> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid requestedBy,
        int totalRows,
        CancellationToken cancellationToken);

    Task AddRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        IReadOnlyCollection<BulkVehicleRow> rows,
        CancellationToken cancellationToken);

    /// <summary>Closes the job with its tallies. The status is COMPLETED even when rows failed.</summary>
    /// <remarks>
    /// FAILED is reserved for a job that could not be processed at all. "Nine of ten rows imported"
    /// is a completed job with an error report, not a failure — and a client that branched on the
    /// status would otherwise discard nine good vehicles.
    /// </remarks>
    Task FinishAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        string status,
        int succeededRows,
        int failedRows,
        CancellationToken cancellationToken);

    Task<BulkVehicleJob?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid jobId,
        CancellationToken cancellationToken);

    /// <summary>The job's failed rows, in CSV order — the error report's whole content.</summary>
    Task<IReadOnlyList<BulkVehicleRow>> ListFailedRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid jobId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetBulkJobRepository"/>
internal sealed class FleetBulkJobRepository : IFleetBulkJobRepository
{
    private const string JobColumns =
        "id, fleet_id, requested_by, status, total_rows, succeeded_rows, failed_rows, created_at, finished_at";

    private const string RowColumns = """
        row_number, registration_number, vehicle_type, mode, mode_b_billing,
        default_monthly_fare_minor, status, vehicle_id, error_code, error_detail
        """;

    public Task<Guid> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid requestedBy,
        int totalRows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO registry.fleet_bulk_jobs (fleet_id, requested_by, total_rows)
            VALUES (@FleetId, @RequestedBy, @TotalRows)
            RETURNING id;
            """,
            new { FleetId = fleetId, RequestedBy = requestedBy, TotalRows = totalRows },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task AddRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        IReadOnlyCollection<BulkVehicleRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO registry.fleet_bulk_job_rows
              (job_id, row_number, registration_number, vehicle_type, mode, mode_b_billing,
               default_monthly_fare_minor, status, vehicle_id, error_code, error_detail)
            VALUES (@JobId, @RowNumber, @RegistrationNumber, @VehicleType, @Mode, @ModeBBilling,
                    @DefaultMonthlyFareMinor, @Status, @VehicleId, @ErrorCode, @ErrorDetail);
            """,
            rows.Select(row => new
            {
                JobId = jobId,
                row.RowNumber,
                row.RegistrationNumber,
                row.VehicleType,
                row.Mode,
                row.ModeBBilling,
                row.DefaultMonthlyFareMinor,
                row.Status,
                row.VehicleId,
                row.ErrorCode,
                row.ErrorDetail,
            }).ToArray(),
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task FinishAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        string status,
        int succeededRows,
        int failedRows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE registry.fleet_bulk_jobs
               SET status = @Status,
                   succeeded_rows = @SucceededRows,
                   failed_rows = @FailedRows,
                   finished_at = now()
             WHERE id = @JobId;
            """,
            new
            {
                JobId = jobId,
                Status = status,
                SucceededRows = succeededRows,
                FailedRows = failedRows,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<BulkVehicleJob?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.QuerySingleOrDefaultAsync<BulkVehicleJob>(new CommandDefinition(
            $"""
             SELECT {JobColumns} FROM registry.fleet_bulk_jobs
              WHERE id = @JobId AND fleet_id = @FleetId;
             """,
            new { FleetId = fleetId, JobId = jobId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<BulkVehicleRow>> ListFailedRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Through the scoped view, so the report cannot be pointed at another org's job by editing
        // the id — the download link carries no bearer (see `IErrorReportLinks`), and this is the
        // second lock behind the signature.
        var rows = await connection.QueryAsync<BulkVehicleRow>(new CommandDefinition(
            $"""
             SELECT {RowColumns} FROM registry.fleet_bulk_job_rows_fleet
              WHERE job_id = @JobId AND fleet_id = @FleetId
                AND status = '{BulkRowStatuses.Failed}'
              ORDER BY row_number;
             """,
            new { FleetId = fleetId, JobId = jobId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}
