using Dapper;
using MageRide.HotPath.PersistenceWriter.Configuration;
using MageRide.HotPath.PersistenceWriter.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.HotPath.PersistenceWriter.Sampling;

/// <summary>ADD §9.2's "1/min sampled" operational history.</summary>
public interface IOperationalSampler
{
    /// <summary>
    /// Writes at most one row per (session, minute) for the Mode A/B vehicles in the batch.
    /// </summary>
    /// <returns>Rows inserted. Zero is the ordinary answer for a batch of Mode C traffic.</returns>
    Task<int> WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyList<PositionRow> batch,
        CancellationToken cancellationToken);
}

/// <summary>
/// The operational downsample — <c>trips.position_samples</c>, one row a minute (ADD §9.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Raw high-frequency positions go to <c>telemetry.positions</c> only</b> — this component's
/// first fence, and §21 and ADD §9.2 both state it. What lands here is one representative fix per
/// session per minute: the operational schema's twelve-month history (§9.2's retention), sized for
/// "show me this journey" rather than for telematics.
/// </para>
/// <para>
/// <b>The row's <c>sample_ts</c> is the minute boundary, not the fix's instant, and that is what
/// makes the write idempotent.</b> D6' §2.3 delivery is at-least-once, so this code will see the
/// same minute again after any rebalance, restart or replay. Truncating to the bucket turns
/// "have I already written this minute?" into a unique-index question
/// (<c>ux_possample_session_minute</c>, migration 0506) that needs no per-vehicle memory, survives a
/// crash, and gives two replicas the same answer. The alternative — an in-process "last written
/// minute per vehicle" — resets exactly when the platform is least stable.
/// </para>
/// <para>
/// <b>The first fix of a minute wins, not the last.</b> Either is defensible; the first is chosen
/// because it is decidable on arrival, so a batch that straddles a minute boundary does not have to
/// hold a bucket open waiting for a better candidate. A minute of driving is under a kilometre and
/// the operational series is not the record of where the vehicle was — <c>telemetry.positions</c> is.
/// </para>
/// <para>
/// <b>A sample from before its session started is dropped.</b> A vehicle that was publishing before
/// the driver pressed Start Journey has fixes that belong to no journey, and attributing them would
/// put the depot in the trip's polyline.
/// </para>
/// </remarks>
public sealed class OperationalSampler(
    IVehicleContextResolver context,
    IOptions<PersistenceWriterOptions> options,
    ILogger<OperationalSampler> logger) : IOperationalSampler
{
    /// <summary>
    /// One statement for the whole batch. <c>ON CONFLICT DO NOTHING</c> against
    /// <c>ux_possample_session_minute</c> is the idempotency; the <c>unnest</c> keeps it to one round
    /// trip rather than one per bucket.
    /// </summary>
    private const string InsertSql =
        """
        INSERT INTO trips.position_samples (session_id, vehicle_id, geo, speed_mps, heading_deg, sample_ts)
        SELECT s.session_id, s.vehicle_id,
               ST_SetSRID(ST_MakePoint(s.lng, s.lat), 4326)::geography,
               s.speed_mps, s.heading_deg, s.sample_ts
          FROM unnest(@SessionIds, @VehicleIds, @Lats, @Lngs, @SpeedMps, @HeadingDeg, @SampleTs)
               AS s(session_id, vehicle_id, lat, lng, speed_mps, heading_deg, sample_ts)
        ON CONFLICT (session_id, sample_ts) DO NOTHING;
        """;

    private readonly PersistenceWriterOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<int> WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyList<PositionRow> batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
        {
            return 0;
        }

        var vehicles = batch.Select(static row => row.Sample.VehicleId).Distinct().ToArray();

        var sessions = await context.ResolveSessionsAsync(
            connection, transaction, vehicles, cancellationToken);

        if (sessions.Count == 0)
        {
            // No Mode A/B journey in this batch. The ordinary case — telemetry.normalized carries
            // every Mode C vehicle on the platform and none of them has a tracking session (R-01).
            return 0;
        }

        var buckets = Bucket(batch, sessions);

        return buckets.Count == 0
            ? 0
            : await InsertAsync(connection, transaction, buckets, cancellationToken);
    }

    /// <summary>Collapses the batch to one fix per (session, minute) — the first of each.</summary>
    internal List<SampleRow> Bucket(
        IReadOnlyList<PositionRow> batch, IReadOnlyDictionary<Guid, ActiveSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(sessions);

        var chosen = new Dictionary<(Guid Session, DateTimeOffset Bucket), SampleRow>();

        foreach (var row in batch)
        {
            var sample = row.Sample;

            if (!sessions.TryGetValue(sample.VehicleId, out var session))
            {
                continue;
            }

            if (sample.SampleTs < session.StartedAt)
            {
                // Published before the driver pressed Start Journey — a vehicle idling at the depot,
                // or a tracker that never stopped from the last journey. The fix is real telemetry
                // and is in the hypertable; attributing it to this journey would put the depot in the
                // trip's polyline and in its distance.
                continue;
            }

            var sessionId = session.SessionId;
            var bucket = Truncate(sample.SampleTs);
            var key = (sessionId, bucket);

            // First wins. A later fix in the same minute is discarded rather than compared, which is
            // what keeps this decidable on arrival — see the class remarks.
            if (chosen.ContainsKey(key))
            {
                continue;
            }

            chosen[key] = new SampleRow(
                sessionId, sample.VehicleId, sample.Lat, sample.Lng,
                sample.SpeedMps, sample.HeadingDeg, bucket);
        }

        return [.. chosen.Values];
    }

    /// <summary>
    /// The minute (or <see cref="PersistenceWriterOptions.SamplePeriod"/>) a fix belongs to.
    /// </summary>
    /// <remarks>
    /// Truncated toward the epoch rather than toward the session start, so two replicas and a
    /// re-delivered batch all agree on the bucket without sharing any state. A UTC boundary, not an
    /// Asia/Colombo one: D-38's business-date rule is about days a payout is settled on, and a
    /// minute bucket is not a business date — Sri Lanka's +05:30 offset would in any case leave
    /// minute boundaries where UTC puts them.
    /// </remarks>
    internal DateTimeOffset Truncate(DateTimeOffset instant)
    {
        var period = _options.SamplePeriod.Ticks;
        var ticks = instant.UtcTicks / period * period;

        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private async Task<int> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        List<SampleRow> rows,
        CancellationToken cancellationToken)
    {
        var inserted = await connection.ExecuteAsync(
            new CommandDefinition(
                InsertSql,
                new
                {
                    SessionIds = rows.ConvertAll(static r => r.SessionId),
                    VehicleIds = rows.ConvertAll(static r => r.VehicleId),
                    Lats = rows.ConvertAll(static r => r.Lat),
                    Lngs = rows.ConvertAll(static r => r.Lng),

                    // REAL in the DDL, so the arrays are float, not double: Npgsql maps a
                    // List<double?> to float8[] and unnest would then hand ST_MakePoint's sibling
                    // columns a type the insert has to cast per row.
                    SpeedMps = rows.ConvertAll(static r => (float?)r.SpeedMps),
                    HeadingDeg = rows.ConvertAll(static r => (short?)r.HeadingDeg),
                    SampleTs = rows.ConvertAll(static r => r.SampleTs),
                },
                transaction,
                cancellationToken: cancellationToken));

        if (inserted < rows.Count)
        {
            // The minutes already stored. Expected on any redelivery — it is the index doing its
            // job — and the count is what says how much of a batch was a replay.
            logger.LogDebug(
                "{Inserted} of {Offered} operational samples were new; the rest were minutes already written",
                inserted, rows.Count);
        }

        return inserted;
    }

    /// <summary>One <c>trips.position_samples</c> row, before it is written.</summary>
    internal sealed record SampleRow(
        Guid SessionId,
        Guid VehicleId,
        double Lat,
        double Lng,
        double? SpeedMps,
        int? HeadingDeg,
        DateTimeOffset SampleTs);
}
