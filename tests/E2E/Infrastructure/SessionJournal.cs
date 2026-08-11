using System.Globalization;
using System.Text;
using Dapper;
using MageRide.Shared.Caching;
using MageRide.TestKit;
using StackExchange.Redis;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// A vehicle's whole Mode A/B history, in the shape a person reads.
/// </summary>
/// <remarks>
/// <para>
/// The Mode A/B counterpart of <see cref="RideJournal"/>, and it exists for the same reason: a
/// failure here is almost never local. A session is still ACTIVE because a fix never crossed the
/// hot path; it ended for the wrong reason because two sweeps raced; a tracker's frame was refused
/// four services upstream. The six sections below are the questions somebody asks in that order —
/// which journeys has this vehicle had, what did each one log, what did it tell the world, did any
/// telemetry land, is there a tracker bound at all, and has anybody been granted a view of it.
/// </para>
/// <para>
/// Every wait in <see cref="ModeAbFleet"/> appends this to its failure message, so no scenario has
/// to remember to; <see cref="AroundAsync"/> wraps a whole scenario body for the failures that are
/// ordinary <c>Assert</c> calls.
/// </para>
/// </remarks>
internal sealed class SessionJournal(PostgresFixture postgres, Func<IDatabase>? cache = null)
{
    /// <summary>Marks a message as already carrying a history, so it is not appended twice.</summary>
    private const string Marker = "\n── vehicle history ──";

    /// <summary>
    /// Runs <paramref name="scenario"/> and, if anything in it fails, re-throws with the history of
    /// every vehicle it named appended.
    /// </summary>
    /// <remarks>
    /// The vehicle ids are collected by the scenario rather than discovered: "every vehicle created
    /// since this test started" is not knowable in a suite sharing one database, and a diagnosis
    /// printing a hundred strangers' buses is no better than none.
    /// </remarks>
    public async Task AroundAsync(Func<List<Guid>, Task> scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var vehicles = new List<Guid>();

        try
        {
            await scenario(vehicles);
        }
        catch (Exception failure) when (vehicles.Count > 0 && !failure.Message.Contains(Marker, StringComparison.Ordinal))
        {
            var report = new StringBuilder(failure.Message);

            foreach (var vehicleId in vehicles.Distinct())
            {
                report.Append(await DescribeAsync(vehicleId));
            }

            throw new SessionScenarioException(report.ToString(), failure);
        }
    }

    /// <summary>One vehicle's sessions, domain log, outbox, telemetry, binding and grants.</summary>
    public async Task<string> DescribeAsync(Guid vehicleId)
    {
        try
        {
            await using var connection = await postgres.OpenAsync();

            var report = new StringBuilder()
                .Append(Marker)
                .Append(CultureInfo.InvariantCulture, $" vehicle {vehicleId}\n");

            var vehicle = await connection.QuerySingleOrDefaultAsync<(string Mode, string Status, string Plate)>(
                "SELECT mode, status, registration_number FROM registry.vehicles WHERE id = @VehicleId;",
                new { VehicleId = vehicleId });

            if (vehicle.Mode is null)
            {
                return report.Append("  (no such vehicle)\n").ToString();
            }

            report.Append(CultureInfo.InvariantCulture,
                $"  registry: Mode {vehicle.Mode}, {vehicle.Status}, plate {vehicle.Plate}\n");

            report.Append("  trips.sessions (D-03):\n");

            var sessions = (await connection.QueryAsync<(
                Guid Id, string State, string? EndReason, string StartedBy, string? EndedBy, bool Fence,
                DateTimeOffset StartedAt, DateTimeOffset? EndedAt, DateTimeOffset? Movement, DateTimeOffset? Offline)>(
                """
                SELECT id, state, end_reason, started_by, ended_by, auto_end_at_destination,
                       started_at, ended_at, last_movement_at, offline_since
                  FROM trips.sessions WHERE vehicle_id = @VehicleId ORDER BY started_at, id;
                """,
                new { VehicleId = vehicleId })).ToList();

            foreach (var session in sessions)
            {
                report.Append(CultureInfo.InvariantCulture,
                    $"    {session.StartedAt:HH:mm:ss.fff}  {session.Id}  {session.State,-9}"
                    + $" by {session.StartedBy,-9} → {session.EndReason ?? "-",-22}"
                    + $" {(session.EndedBy is null ? string.Empty : $"by {session.EndedBy} ")}"
                    + $"{(session.EndedAt is null ? string.Empty : $"at {session.EndedAt:HH:mm:ss.fff} ")}"
                    + $"{(session.Fence ? "[fence armed] " : string.Empty)}"
                    + $"moved {session.Movement?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "never"}"
                    + $"{(session.Offline is null ? string.Empty : $" offline since {session.Offline:HH:mm:ss.fff}")}\n");
            }

            if (sessions.Count > 0)
            {
                report.Append("  trips.events (0502 — the domain log):\n");

                foreach (var (sessionId, kind, at) in
                         await connection.QueryAsync<(Guid, string, DateTimeOffset)>(
                             """
                             SELECT e.session_id, e.kind, e.ts FROM trips.events e
                               JOIN trips.sessions s ON s.id = e.session_id
                              WHERE s.vehicle_id = @VehicleId ORDER BY e.ts, e.id;
                             """,
                             new { VehicleId = vehicleId }))
                {
                    report.Append(CultureInfo.InvariantCulture, $"    {at:HH:mm:ss.fff}  {kind,-22} {sessionId}\n");
                }
            }

            report.Append("  trips.outbox (trip.events):\n");
            foreach (var (type, at, dispatched) in
                     await connection.QueryAsync<(string, DateTimeOffset, DateTimeOffset?)>(
                         """
                         SELECT event_type, created_at, dispatched_at
                           FROM trips.outbox WHERE aggregate_id = @VehicleId ORDER BY id;
                         """,
                         new { VehicleId = vehicleId }))
            {
                report.Append(CultureInfo.InvariantCulture,
                    $"    {at:HH:mm:ss.fff}  {type,-22} {(dispatched is null ? "UNDISPATCHED" : "published")}\n");
            }

            var telemetry = await connection.QuerySingleOrDefaultAsync<(int Rows, DateTimeOffset? Newest)>(
                "SELECT count(*)::int, max(sample_ts) FROM telemetry.positions WHERE vehicle_id = @VehicleId;",
                new { VehicleId = vehicleId });

            report.Append(CultureInfo.InvariantCulture,
                $"  telemetry.positions: {telemetry.Rows} row(s)"
                + $"{(telemetry.Newest is null ? " — nothing reached Timescale" : $", newest {telemetry.Newest:HH:mm:ss.fff}")}\n");

            await AppendProcessorVerdictAsync(report, vehicleId);

            report.Append("  prov.tracker_bindings (T-03):\n");
            foreach (var (imei, state, boundAt) in
                     await connection.QueryAsync<(string, string, DateTimeOffset)>(
                         """
                         SELECT imei, state, created_at FROM prov.tracker_bindings
                          WHERE vehicle_id = @VehicleId ORDER BY created_at;
                         """,
                         new { VehicleId = vehicleId }))
            {
                report.Append(CultureInfo.InvariantCulture, $"    {imei}  {state,-12} bound {boundAt:HH:mm:ss.fff}\n");
            }

            report.Append("  subscription.grants (Epic 23):\n");
            foreach (var (passengerId, status, unsubscribedAt, deletedAt) in
                     await connection.QueryAsync<(Guid, string, DateTimeOffset?, DateTimeOffset?)>(
                         """
                         SELECT passenger_id, status, unsubscribed_at, deleted_at
                           FROM subscription.grants WHERE vehicle_id = @VehicleId ORDER BY granted_at;
                         """,
                         new { VehicleId = vehicleId }))
            {
                report.Append(CultureInfo.InvariantCulture,
                    $"    passenger {passengerId} {status,-14}"
                    + $"{(unsubscribedAt is null ? string.Empty : $" left {unsubscribedAt:HH:mm:ss.fff}")}"
                    + $"{(deletedAt is null ? string.Empty : $" deleted {deletedAt:HH:mm:ss.fff}")}\n");
            }

            return report.ToString();
        }
        catch (Exception diagnosis) when (diagnosis is not OperationCanceledException)
        {
            // A diagnostic that throws would replace the real failure with its own, which is the one
            // outcome worse than no diagnostic at all.
            return $"\n{Marker} vehicle {vehicleId}: the history could not be read ({diagnosis.Message})\n";
        }
    }

    /// <summary>
    /// position-processor-svc's own verdict on this vehicle, read out of Redis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row count above says whether a fix reached Timescale. It cannot say <em>why</em> one did
    /// not, and the two answers need opposite fixes: a sample position-processor-svc REFUSED (a
    /// D-18 gate, the T-05 replay watermark) is a scenario driving the platform wrongly, while a
    /// sample it ACCEPTED that has no row is persistence-writer-svc, its consumer group, or the
    /// database. `veh:meta` is written only for an accepted sample and `veh:seq` only advances for
    /// one, so the pair splits the pipeline exactly at position-processor-svc.
    /// </para>
    /// <para>
    /// Added while chasing the intermittent ModeB telemetry timeout of 2026-08-10, which reproduced
    /// in CI twice and never once on the build host — 6/6 alone and 136/136 in the full suite pinned
    /// to two CPUs. Without this the failure says only "no row arrived", which is equally true of
    /// every cause.
    /// </para>
    /// <para>
    /// `sampleTs` is an ISO-8601 instant, and `seq` is <b>not</b> independent of it: tcp-adapter sets
    /// `seq = CapturedAt.ToUnixTimeMilliseconds()` (`Ingest/TrackerSamples.cs`) and all four protocols
    /// stamp to the whole second, so seq always ends in `000` and carries second resolution. Both are
    /// printed raw because that relationship is the useful thing to see — a watermark equal to the
    /// awaited fix means the T-05 replay check, not a plausibility gate, is what refused it.
    /// </para>
    /// </remarks>
    private async Task AppendProcessorVerdictAsync(StringBuilder report, Guid vehicleId)
    {
        if (cache is null)
        {
            return;
        }

        try
        {
            var db = cache();
            var meta = await db.HashGetAllAsync(RedisKeys.VehicleMeta(vehicleId));
            var seq = await db.StringGetAsync(RedisKeys.VehicleSeq(vehicleId));

            report.Append("  position-processor verdict (Redis):\n");

            if (meta.Length == 0)
            {
                report.Append(
                    "    veh:meta absent — position-processor-svc accepted NO sample for this vehicle,\n"
                    + "      or the 10-minute VehicleMetaTtl has expired since it did. If a fix was sent,\n"
                    + "      it was refused or never arrived: the gap is UPSTREAM of persistence-writer.\n");
            }
            else
            {
                var fields = meta.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
                var stamp = fields.GetValueOrDefault("sampleTs");
                var when = DateTimeOffset.TryParse(
                    stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    : stamp ?? "-";

                report.Append(CultureInfo.InvariantCulture,
                    $"    last ACCEPTED sample {when}, seq {fields.GetValueOrDefault("seq") ?? "-"}, "
                    + $"at {fields.GetValueOrDefault("lat") ?? "-"},{fields.GetValueOrDefault("lng") ?? "-"}\n");
                report.Append(
                    "      If this matches the fix the wait was after, position-processor-svc ACCEPTED it\n"
                    + "      and the missing row is persistence-writer-svc's — its consumer group, its\n"
                    + "      batch, or Postgres. If it is older, the fix was refused or never arrived.\n");
            }

            report.Append(CultureInfo.InvariantCulture,
                $"    veh:seq watermark: {(seq.HasValue ? seq.ToString() : "absent")}\n");
        }
        catch (Exception diagnosis) when (diagnosis is not OperationCanceledException)
        {
            report.Append(CultureInfo.InvariantCulture,
                $"  position-processor verdict: unreadable ({diagnosis.Message})\n");
        }
    }
}

/// <summary>
/// A scenario failure with the vehicle's history attached.
/// </summary>
/// <remarks>
/// A distinct type rather than a rethrow of the original: xUnit prints the message of whatever it
/// catches, and re-throwing the assertion would lose everything appended to it.
/// </remarks>
internal sealed class SessionScenarioException(string message, Exception inner) : Exception(message, inner);
