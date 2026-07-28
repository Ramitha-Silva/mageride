using Dapper;
using MageRide.Provisioning.Domain;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Provisioning.Persistence;

/// <summary><c>prov.bulk_jobs</c> and <c>prov.bulk_job_rows</c> — T-09 (migration 0405).</summary>
public interface IBulkJobRepository
{
    /// <summary>
    /// Creates the job and every parsed row in one statement pair.
    /// </summary>
    /// <remarks>
    /// The caller's transaction spans both, which is the "validates atomically … without partial
    /// commits" half of the DoD: a CSV either becomes a job with all of its rows or becomes
    /// nothing. Returns <see langword="null"/> when the fleet already has a job in flight —
    /// <c>ux_bulk_jobs_in_flight</c> settles that race rather than a prior SELECT.
    /// </remarks>
    Task<BulkJob?> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid requestedBy,
        string credentialType,
        IReadOnlyList<BulkRowInput> rows,
        CancellationToken cancellationToken);

    Task<BulkJob?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        Guid jobId,
        CancellationToken cancellationToken);

    /// <summary>The oldest job with rows left to mint, claimed for this worker.</summary>
    Task<BulkJob?> ClaimNextProcessingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken);

    /// <summary>Claims pending rows of one job. <c>FOR UPDATE SKIP LOCKED</c>, so replicas share a job.</summary>
    Task<IReadOnlyList<BulkRow>> ClaimPendingRowsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid jobId, int limit, CancellationToken cancellationToken);

    Task CompleteRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        int rowNumber,
        string status,
        Guid? bindingId,
        string? errorCode,
        string? errorDetail,
        CancellationToken cancellationToken);

    /// <summary>
    /// Recounts the job from its rows and finishes it when nothing is pending.
    /// </summary>
    /// <remarks>
    /// Derived rather than incremented: a counter bumped per row drifts the moment a worker dies
    /// between binding a row and recording it, and the number the Admin Portal polls would then be
    /// permanently wrong with nothing to reconcile it against.
    /// </remarks>
    Task<BulkJob> RecountAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Every row of a job, in CSV order — the per-row error report.</summary>
    Task<IReadOnlyList<BulkRow>> ListRowsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid jobId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBulkJobRepository"/>
public sealed class BulkJobRepository : IBulkJobRepository
{
    private const string JobColumns =
        "id, fleet_id, requested_by, status, total_rows, succeeded_rows, failed_rows, credential_type, " +
        "created_at, finished_at";

    private const string RowColumns =
        "job_id, row_number, imei, registration_number, vehicle_id, status, error_code, error_detail, binding_id";

    public async Task<BulkJob?> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid requestedBy,
        string credentialType,
        IReadOnlyList<BulkRowInput> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(rows);

        // ON CONFLICT on the partial unique index: a second submission while one is in flight
        // returns no row, which the service turns into D3''s 429 bulk-in-progress.
        var job = await connection.QuerySingleOrDefaultAsync<BulkJob>(new CommandDefinition(
            $"""
             INSERT INTO prov.bulk_jobs (fleet_id, requested_by, status, total_rows, credential_type)
             VALUES (@FleetId, @RequestedBy, '{BulkJobStatuses.Processing}', @TotalRows, @CredentialType)
             ON CONFLICT (fleet_id) WHERE status = '{BulkJobStatuses.Processing}' DO NOTHING
             RETURNING {JobColumns};
             """,
            new { FleetId = fleetId, RequestedBy = requestedBy, TotalRows = rows.Count, CredentialType = credentialType },
            transaction,
            cancellationToken: cancellationToken));

        if (job is null)
        {
            return null;
        }

        await InsertRowsAsync(connection, transaction, job.Id, rows, cancellationToken);

        return job;
    }

    public Task<BulkJob?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Scoped by fleet as well as by id: a job id from another fleet is "not found" here rather
        // than a row somebody else's operator can read the plates out of.
        return connection.QuerySingleOrDefaultAsync<BulkJob>(new CommandDefinition(
            $"SELECT {JobColumns} FROM prov.bulk_jobs WHERE id = @JobId AND fleet_id = @FleetId;",
            new { JobId = jobId, FleetId = fleetId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<BulkJob?> ClaimNextProcessingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QueryFirstOrDefaultAsync<BulkJob>(new CommandDefinition(
            $"""
             SELECT {JobColumns}
               FROM prov.bulk_jobs
              WHERE status = '{BulkJobStatuses.Processing}'
              ORDER BY created_at
              LIMIT 1
                FOR UPDATE SKIP LOCKED;
             """,
            transaction: transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<BulkRow>> ClaimPendingRowsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid jobId, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<BulkRow>(new CommandDefinition(
            $"""
             SELECT {RowColumns}
               FROM prov.bulk_job_rows
              WHERE job_id = @JobId AND status = '{BulkRowStatuses.Pending}'
              ORDER BY row_number
              LIMIT @Limit
                FOR UPDATE SKIP LOCKED;
             """,
            new { JobId = jobId, Limit = limit },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task CompleteRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        int rowNumber,
        string status,
        Guid? bindingId,
        string? errorCode,
        string? errorDetail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE prov.bulk_job_rows
               SET status = @Status, binding_id = @BindingId, error_code = @ErrorCode, error_detail = @ErrorDetail
             WHERE job_id = @JobId AND row_number = @RowNumber;
            """,
            new
            {
                JobId = jobId,
                RowNumber = rowNumber,
                Status = status,
                BindingId = bindingId,
                ErrorCode = errorCode,
                ErrorDetail = errorDetail,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<BulkJob> RecountAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleAsync<BulkJob>(new CommandDefinition(
            $"""
             WITH tally AS (
               SELECT count(*) FILTER (WHERE status = '{BulkRowStatuses.Bound}')   AS bound,
                      count(*) FILTER (WHERE status = '{BulkRowStatuses.Failed}')  AS failed,
                      count(*) FILTER (WHERE status = '{BulkRowStatuses.Pending}') AS pending
                 FROM prov.bulk_job_rows
                WHERE job_id = @JobId)
             UPDATE prov.bulk_jobs j
                SET succeeded_rows = tally.bound,
                    failed_rows    = tally.failed,
                    status         = CASE WHEN tally.pending = 0
                                          THEN '{BulkJobStatuses.Completed}'
                                          ELSE '{BulkJobStatuses.Processing}' END,
                    finished_at    = CASE WHEN tally.pending = 0 THEN @Now ELSE NULL END
               FROM tally
              WHERE j.id = @JobId
             RETURNING j.id, j.fleet_id, j.requested_by, j.status, j.total_rows, j.succeeded_rows,
                       j.failed_rows, j.credential_type, j.created_at, j.finished_at;
             """,
            new { JobId = jobId, Now = now },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<BulkRow>> ListRowsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid jobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<BulkRow>(new CommandDefinition(
            $"SELECT {RowColumns} FROM prov.bulk_job_rows WHERE job_id = @JobId ORDER BY row_number;",
            new { JobId = jobId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <summary>
    /// Writes every row with one binary COPY.
    /// </summary>
    /// <remarks>
    /// 5,000 parameterised INSERTs is 5,000 round trips inside the NFR-43 budget, and Npgsql's
    /// COPY is the one path that does not pay them. The rows are already validated by the time
    /// they get here, so nothing is lost by giving up per-row error reporting on the write itself.
    /// </remarks>
    private static async Task InsertRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        IReadOnlyList<BulkRowInput> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY prov.bulk_job_rows (job_id, row_number, imei, registration_number, vehicle_id, status) " +
            "FROM STDIN (FORMAT BINARY)",
            cancellationToken);

        foreach (var row in rows)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(jobId, NpgsqlDbType.Uuid, cancellationToken);
            await writer.WriteAsync(row.RowNumber, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(row.Imei, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(row.RegistrationNumber, NpgsqlDbType.Text, cancellationToken);

            if (row.VehicleId is { } vehicleId)
            {
                await writer.WriteAsync(vehicleId, NpgsqlDbType.Uuid, cancellationToken);
            }
            else
            {
                await writer.WriteNullAsync(cancellationToken);
            }

            await writer.WriteAsync(BulkRowStatuses.Pending, NpgsqlDbType.Text, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);

        // The transaction is the caller's; COPY takes part in it, so a rollback above discards
        // these rows with the job.
        _ = transaction;
    }
}
