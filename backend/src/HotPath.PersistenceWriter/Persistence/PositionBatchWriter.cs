using System.Diagnostics;
using MageRide.HotPath.PersistenceWriter.Configuration;
using MageRide.HotPath.PersistenceWriter.Sampling;
using MageRide.Shared.Observability;
using MageRide.Shared.Persistence;
using MageRide.Shared.Telemetry;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.HotPath.PersistenceWriter.Persistence;

/// <summary>One accepted sample, with the fleet this service resolved for it.</summary>
/// <param name="Sample">The normalised sample, exactly as C039 republished it.</param>
/// <param name="FleetId">
/// The owning fleet, resolved here when the publisher did not denormalise it
/// (<c>mqtt-topics.md</c> §6: "C040 must populate it").
/// </param>
public sealed record PositionRow(PositionSample Sample, Guid? FleetId);

/// <summary>What one flush did.</summary>
/// <param name="Rows">Rows offered to the database, after in-batch deduplication.</param>
/// <param name="Inserted">Rows the hypertable actually took — the rest were replays (T-05/R-17).</param>
/// <param name="Sampled">Rows written to <c>trips.position_samples</c> (ADD §9.2).</param>
/// <param name="DeadLettered">Rows Postgres refused on their own merits.</param>
/// <param name="Elapsed">Wall time of the transaction.</param>
public readonly record struct FlushOutcome(
    int Rows, int Inserted, int Sampled, int DeadLettered, TimeSpan Elapsed);

/// <summary>The durable write, one batch at a time.</summary>
public interface IPositionBatchWriter
{
    /// <summary>
    /// Writes one batch in a single transaction: the hypertable, then the 1/min operational
    /// downsample.
    /// </summary>
    /// <returns>What was written. Throws only when the batch should be retried.</returns>
    Task<FlushOutcome> WriteAsync(IReadOnlyList<PositionRow> batch, CancellationToken cancellationToken);
}

/// <summary>
/// ADD §9.5 item 5's write path: <c>COPY</c> into the hypertable, batched, idempotent.
/// </summary>
/// <remarks>
/// <para>
/// <b>COPY into a temp table, then <c>INSERT … ON CONFLICT DO NOTHING</c>.</b> The spec asks for
/// <c>COPY</c> (§9.5 item 5) and separately requires replay idempotency on the vehicle's sequence
/// (§9.5 item 1, T-05/R-17), and <c>COPY</c> has no conflict handling at all — a duplicate raises and
/// takes the whole batch with it. The two-step is what satisfies both: the binary import moves the
/// rows at <c>COPY</c> speed, and one set-based insert applies the unique index. A batch of a
/// thousand costs one extra sequential scan of a thousand rows, which is nothing beside the index
/// maintenance the insert does anyway.
/// </para>
/// <para>
/// <b>The temp table is created inside the transaction, <c>ON COMMIT DROP</c>.</b> A session-scoped
/// staging table created once per connection would be faster, and would break the moment this
/// service ran behind PgBouncer in transaction mode (ADD §9.3), where consecutive transactions are
/// not guaranteed the same backend. One <c>CREATE TEMP TABLE</c> per half-second is not a cost worth
/// a correctness footgun.
/// </para>
/// <para>
/// <b>One transaction, so a kill loses nothing.</b> The hypertable rows and the operational samples
/// commit together, and the caller commits Kafka offsets only after this returns. A process killed
/// at any point replays from the last committed offset; the unique indexes on both tables discard
/// what was already written, so the replay is a no-op rather than a duplicate.
/// </para>
/// <para>
/// <b>The conflict target is three columns, and that is not a choice.</b> TimescaleDB rejects a
/// unique index that omits a partitioning column, so the specs' <c>(vehicle_id, seq)</c> cannot
/// exist and <c>ux_positions_vehicle_seq</c> is <c>(vehicle_id, seq, sample_ts)</c> (C006 note (a)).
/// A re-sent buffered sample carries the GNSS timestamp it was captured with, so the tuple still
/// collides — which is the case T-05/R-17 exists for.
/// </para>
/// </remarks>
public sealed class PositionBatchWriter(
    INpgsqlConnectionFactory connectionFactory,
    IOperationalSampler sampler,
    IDeadLetterSink deadLetters,
    IOptions<PersistenceWriterOptions> options,
    ILogger<PositionBatchWriter> logger) : IPositionBatchWriter
{
    /// <summary>
    /// The staging table. <c>UNLOGGED</c> is implicit for a temp table and <c>ON COMMIT DROP</c>
    /// ties its lifetime to the batch.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> <c>LIKE telemetry.positions</c>: that would copy the four CHECK
    /// constraints, so a bad row would fail during the <c>COPY</c> — where the failure names a line
    /// number in a binary stream and nothing else. Failing on the <c>INSERT</c> instead lets the
    /// row-by-row isolation pass identify exactly which sample is poison and dead-letter it with the
    /// sample id attached.
    /// </remarks>
    private const string CreateStagingSql =
        """
        CREATE TEMP TABLE positions_staging (
          vehicle_id  UUID NOT NULL,
          sample_ts   TIMESTAMPTZ NOT NULL,
          received_ts TIMESTAMPTZ NOT NULL,
          seq         BIGINT NOT NULL,
          lat         DOUBLE PRECISION NOT NULL,
          lng         DOUBLE PRECISION NOT NULL,
          speed_mps   REAL,
          heading_deg SMALLINT,
          accuracy_m  REAL,
          hdop        REAL,
          sat_count   SMALLINT,
          source      SMALLINT NOT NULL,
          fleet_id    UUID,
          trip_id     UUID
        ) ON COMMIT DROP;
        """;

    private const string CopySql =
        """
        COPY positions_staging (vehicle_id, sample_ts, received_ts, seq, lat, lng, speed_mps,
                                heading_deg, accuracy_m, hdop, sat_count, source, fleet_id, trip_id)
        FROM STDIN (FORMAT BINARY)
        """;

    /// <summary>
    /// The one statement that applies the batch. <c>DISTINCT ON</c> collapses a duplicate that
    /// arrived inside this same batch — <c>ON CONFLICT</c> would handle it, but resolving it here
    /// keeps the row count the caller reports equal to the rows actually offered.
    /// </summary>
    private const string ApplySql =
        """
        INSERT INTO telemetry.positions (
            vehicle_id, sample_ts, received_ts, seq, lat, lng, speed_mps, heading_deg,
            accuracy_m, hdop, sat_count, source, fleet_id, trip_id)
        SELECT DISTINCT ON (vehicle_id, seq, sample_ts)
               vehicle_id, sample_ts, received_ts, seq, lat, lng, speed_mps, heading_deg,
               accuracy_m, hdop, sat_count, source, fleet_id, trip_id
          FROM positions_staging
         ORDER BY vehicle_id, seq, sample_ts, received_ts
        ON CONFLICT (vehicle_id, seq, sample_ts) DO NOTHING;
        """;

    /// <summary>
    /// The isolation pass, one row at a time. Only reached after a data error, and bounded by
    /// <see cref="PersistenceWriterOptions.BatchRows"/>.
    /// </summary>
    private const string InsertOneSql =
        """
        INSERT INTO telemetry.positions (
            vehicle_id, sample_ts, received_ts, seq, lat, lng, speed_mps, heading_deg,
            accuracy_m, hdop, sat_count, source, fleet_id, trip_id)
        VALUES (@vehicleId, @sampleTs, @receivedTs, @seq, @lat, @lng, @speedMps, @headingDeg,
                @accuracyM, @hdop, @satCount, @source, @fleetId, @tripId)
        ON CONFLICT (vehicle_id, seq, sample_ts) DO NOTHING;
        """;

    private readonly PersistenceWriterOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<FlushOutcome> WriteAsync(
        IReadOnlyList<PositionRow> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
        {
            return default;
        }

        using var activity = MageRideDiagnostics.ActivitySource.StartActivity(
            "persistence-writer.flush", ActivityKind.Client);
        activity?.SetTag("mageride.batch_rows", batch.Count);

        var clock = Stopwatch.StartNew();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var create = new NpgsqlCommand(CreateStagingSql, connection, transaction))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await CopyAsync(connection, batch, cancellationToken);

        int inserted;

        try
        {
            await using var apply = new NpgsqlCommand(ApplySql, connection, transaction);
            inserted = await apply.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (IsDataError(ex) && _options.DeadLetterEnabled)
        {
            // The batch contains at least one row this table will never accept. Retrying it whole
            // would fail identically forever and stall the partition every other vehicle shares, so
            // the rows are re-offered individually to find which ones. Bounded by BatchRows, and
            // only ever reached after C039's filter already let something through that Postgres
            // still refuses — which is a producer bug, not a device.
            logger.LogError(
                ex,
                "A batch of {Rows} rows was refused ({SqlState}); isolating the poison rows one by one",
                batch.Count, ex.SqlState);

            await transaction.RollbackAsync(cancellationToken);

            return await IsolateAsync(batch, clock, cancellationToken);
        }

        var sampled = _options.OperationalSamplingEnabled
            ? await sampler.WriteAsync(connection, transaction, batch, cancellationToken)
            : 0;

        await transaction.CommitAsync(cancellationToken);

        clock.Stop();
        Record(batch.Count, inserted, sampled, deadLettered: 0, clock.Elapsed);

        return new FlushOutcome(batch.Count, inserted, sampled, DeadLettered: 0, clock.Elapsed);
    }

    /// <summary>Streams the batch into the staging table as binary <c>COPY</c>.</summary>
    private static async Task CopyAsync(
        NpgsqlConnection connection, IReadOnlyList<PositionRow> batch, CancellationToken cancellationToken)
    {
        await using var writer = await connection.BeginBinaryImportAsync(CopySql, cancellationToken);

        foreach (var row in batch)
        {
            var sample = row.Sample;

            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(sample.VehicleId, NpgsqlDbType.Uuid, cancellationToken);
            await writer.WriteAsync(sample.SampleTs, NpgsqlDbType.TimestampTz, cancellationToken);

            // received_ts is NOT NULL DEFAULT now() in the DDL, and a COPY supplies no defaults, so
            // it has to be written. C039 stamps it on every sample it republishes; falling back to
            // the sample's own instant would silently record a replay lag of zero.
            await writer.WriteAsync(
                sample.ReceivedTs ?? sample.SampleTs, NpgsqlDbType.TimestampTz, cancellationToken);

            await writer.WriteAsync(sample.Seq, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(sample.Lat, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(sample.Lng, NpgsqlDbType.Double, cancellationToken);

            await WriteNullableAsync(writer, sample.SpeedMps, NpgsqlDbType.Real, cancellationToken);
            await WriteNullableAsync(writer, sample.HeadingDeg, NpgsqlDbType.Smallint, cancellationToken);
            await WriteNullableAsync(writer, sample.AccuracyM, NpgsqlDbType.Real, cancellationToken);
            await WriteNullableAsync(writer, sample.Hdop, NpgsqlDbType.Real, cancellationToken);
            await WriteNullableAsync(writer, sample.SatCount, NpgsqlDbType.Smallint, cancellationToken);

            await writer.WriteAsync((short)sample.Source, NpgsqlDbType.Smallint, cancellationToken);

            await WriteNullableAsync(writer, row.FleetId, NpgsqlDbType.Uuid, cancellationToken);
            await WriteNullableAsync(writer, sample.TripId, NpgsqlDbType.Uuid, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    /// <summary>
    /// Re-offers a refused batch row by row, dead-lettering the ones Postgres will never take.
    /// </summary>
    /// <remarks>
    /// Each row gets its own transaction, so one poison row cannot roll back its neighbours. The
    /// 1/min downsample is skipped for this batch: it is derived from the same rows, and a partial
    /// batch would write a minute bucket whose representative fix is a row that failed. The next
    /// batch's samples cover the same minute.
    /// </remarks>
    private async Task<FlushOutcome> IsolateAsync(
        IReadOnlyList<PositionRow> batch, Stopwatch clock, CancellationToken cancellationToken)
    {
        var inserted = 0;
        var poison = new List<(PositionRow Row, string Reason)>();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        foreach (var row in batch)
        {
            try
            {
                await using var command = new NpgsqlCommand(InsertOneSql, connection);
                Bind(command, row);

                inserted += await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException ex) when (IsDataError(ex))
            {
                poison.Add((row, $"{ex.SqlState}: {ex.MessageText}"));
            }
        }

        foreach (var (row, reason) in poison)
        {
            logger.LogError(
                "Dead-lettering vehicle {VehicleId} seq {Seq}: {Reason}",
                row.Sample.VehicleId, row.Sample.Seq, reason);

            await deadLetters.SendAsync(row.Sample, reason, cancellationToken);
        }

        clock.Stop();
        Record(batch.Count, inserted, sampled: 0, deadLettered: poison.Count, clock.Elapsed);

        return new FlushOutcome(batch.Count, inserted, 0, poison.Count, clock.Elapsed);
    }

    private static void Bind(NpgsqlCommand command, PositionRow row)
    {
        var sample = row.Sample;

        command.Parameters.AddWithValue("vehicleId", sample.VehicleId);
        command.Parameters.AddWithValue("sampleTs", sample.SampleTs);
        command.Parameters.AddWithValue("receivedTs", sample.ReceivedTs ?? sample.SampleTs);
        command.Parameters.AddWithValue("seq", sample.Seq);
        command.Parameters.AddWithValue("lat", sample.Lat);
        command.Parameters.AddWithValue("lng", sample.Lng);
        command.Parameters.AddWithValue("speedMps", (object?)sample.SpeedMps ?? DBNull.Value);
        command.Parameters.AddWithValue("headingDeg", (object?)(short?)sample.HeadingDeg ?? DBNull.Value);
        command.Parameters.AddWithValue("accuracyM", (object?)sample.AccuracyM ?? DBNull.Value);
        command.Parameters.AddWithValue("hdop", (object?)sample.Hdop ?? DBNull.Value);
        command.Parameters.AddWithValue("satCount", (object?)(short?)sample.SatCount ?? DBNull.Value);
        command.Parameters.AddWithValue("source", (short)sample.Source);
        command.Parameters.AddWithValue("fleetId", (object?)row.FleetId ?? DBNull.Value);
        command.Parameters.AddWithValue("tripId", (object?)sample.TripId ?? DBNull.Value);
    }

    /// <summary>
    /// Whether Postgres refused the row for what it <i>is</i>, rather than for anything transient.
    /// </summary>
    /// <remarks>
    /// SQLSTATE class 22 is data exception (a numeric out of range, a bad timestamp) and 23 is
    /// integrity constraint violation (a CHECK, a NOT NULL). Everything else — a dropped connection,
    /// a deadlock, a full disk, a hypertable chunk being compressed — is transient by definition and
    /// must be retried, because committing past it would silently lose telemetry.
    /// </remarks>
    internal static bool IsDataError(PostgresException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.SqlState is { Length: >= 2 } state
               && state.AsSpan(0, 2) is "22" or "23";
    }

    private static async Task WriteNullableAsync<T>(
        NpgsqlBinaryImporter writer, T? value, NpgsqlDbType type, CancellationToken cancellationToken)
        where T : struct
    {
        if (value is { } present)
        {
            await writer.WriteAsync(present, type, cancellationToken);
        }
        else
        {
            await writer.WriteNullAsync(cancellationToken);
        }
    }

    private static void Record(int rows, int inserted, int sampled, int deadLettered, TimeSpan elapsed)
    {
        MageRideDiagnostics.TelemetryRowsWritten.Add(inserted);

        if (rows > inserted)
        {
            // Rows the unique index refused. Expected and healthy — it is T-05/R-17 working — but a
            // sustained high ratio means a device is re-sending a backlog it has already delivered.
            MageRideDiagnostics.TelemetryRowsDeduped.Add(rows - inserted - deadLettered);
        }

        if (sampled > 0)
        {
            MageRideDiagnostics.OperationalSamplesWritten.Add(sampled);
        }

        if (deadLettered > 0)
        {
            MageRideDiagnostics.TelemetryRowsDeadLettered.Add(deadLettered);
        }

        MageRideDiagnostics.TelemetryFlushLatencyMs.Record(elapsed.TotalMilliseconds);
    }
}
