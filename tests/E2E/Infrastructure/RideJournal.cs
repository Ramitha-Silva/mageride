using System.Globalization;
using System.Text;
using Dapper;
using MageRide.TestKit;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// A ride's whole history, in the shape a person reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>C120's third definition-of-done item</b>: "a failure prints the ride's transition history for
/// diagnosis". A Mode C failure is almost never local — the ride is in the wrong state because a
/// consumer did not run, or a timer fired early, or an offer expired while a scenario was waiting —
/// and the four tables below are exactly the four questions somebody asks in that order: where has
/// this ride been, what did it tell the world, what is it still waiting for, and did dispatch-svc
/// ever offer it to anybody.
/// </para>
/// <para>
/// Every wait and every timer assertion in <see cref="ModeCFleet"/> appends this to its failure
/// message, so no scenario has to remember to. <see cref="AroundAsync"/> wraps a whole scenario body for
/// the failures that are ordinary <c>Assert</c> calls rather than waits.
/// </para>
/// </remarks>
internal sealed class RideJournal(PostgresFixture postgres)
{
    /// <summary>
    /// Runs <paramref name="scenario"/> and, if anything in it fails, re-throws with the histories
    /// of every ride it names appended.
    /// </summary>
    /// <remarks>
    /// The ride ids are collected by the scenario itself rather than discovered, because "every ride
    /// created since this test started" is not knowable in a suite that shares one database with
    /// every other suite in the run — and a diagnosis that printed a hundred strangers' rides would
    /// be no better than none.
    /// </remarks>
    public async Task AroundAsync(Func<List<Guid>, Task> scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var rides = new List<Guid>();

        try
        {
            await scenario(rides);
        }
        catch (Exception failure) when (rides.Count > 0 && !ContainsJournal(failure))
        {
            var report = new StringBuilder(failure.Message);

            foreach (var rideId in rides.Distinct())
            {
                report.Append(await DescribeAsync(rideId));
            }

            throw new RideScenarioException(report.ToString(), failure);
        }
    }

    /// <summary>One ride's transitions, events, live timers and offers, newest table last.</summary>
    public async Task<string> DescribeAsync(Guid rideId)
    {
        try
        {
            await using var connection = await postgres.OpenAsync();

            var report = new StringBuilder()
                .Append(Marker)
                .Append(CultureInfo.InvariantCulture, $" ride {rideId}\n");

            var ride = await connection.QuerySingleOrDefaultAsync<(string State, long Version, DateTimeOffset CreatedAt)>(
                "SELECT state, version, created_at FROM rides.rides WHERE id = @RideId;",
                new { RideId = rideId });

            if (ride.State is null)
            {
                return report.Append("  (no such ride)\n").ToString();
            }

            report.Append(CultureInfo.InvariantCulture, $"  now: {ride.State} v{ride.Version}, requested {ride.CreatedAt:HH:mm:ss.fff}\n");

            report.Append("  transitions (ADD Appendix B.2 invariant 4):\n");
            foreach (var (from, to, reason, actorType, at) in
                     await connection.QueryAsync<(string?, string, string?, string, DateTimeOffset)>(
                         """
                         SELECT from_state, to_state, reason_code, actor_type, ts
                           FROM rides.transitions WHERE ride_id = @RideId ORDER BY ts, id;
                         """,
                         new { RideId = rideId }))
            {
                report.Append(CultureInfo.InvariantCulture,
                    $"    {at:HH:mm:ss.fff}  {from ?? "-",-28} → {to,-28} {reason ?? string.Empty,-30} by {actorType}\n");
            }

            report.Append("  rides.outbox:\n");
            foreach (var (type, at, dispatched) in
                     await connection.QueryAsync<(string, DateTimeOffset, DateTimeOffset?)>(
                         """
                         SELECT event_type, created_at, dispatched_at
                           FROM rides.outbox WHERE aggregate_id = @RideId ORDER BY id;
                         """,
                         new { RideId = rideId }))
            {
                report.Append(CultureInfo.InvariantCulture,
                    $"    {at:HH:mm:ss.fff}  {type,-34} {(dispatched is null ? "UNDISPATCHED" : "published")}\n");
            }

            report.Append("  rides.timers (R-04):\n");
            foreach (var (kind, fireAt, firedAt) in
                     await connection.QueryAsync<(string, DateTimeOffset, DateTimeOffset?)>(
                         """
                         SELECT kind, fire_at, fired_at FROM rides.timers
                          WHERE ride_id = @RideId ORDER BY fire_at, id;
                         """,
                         new { RideId = rideId }))
            {
                report.Append(CultureInfo.InvariantCulture,
                    $"    {kind,-20} due {fireAt:HH:mm:ss.fff} {(firedAt is null ? "(live)" : $"fired {firedAt:HH:mm:ss.fff}")}\n");
            }

            report.Append("  dispatch.offers:\n");
            foreach (var (driverId, status, sentAt, expiresAt, respondedAt) in
                     await connection.QueryAsync<(Guid, string, DateTimeOffset, DateTimeOffset, DateTimeOffset?)>(
                         """
                         SELECT driver_id, status, sent_at, expires_at, responded_at
                           FROM dispatch.offers WHERE ride_id = @RideId ORDER BY sent_at;
                         """,
                         new { RideId = rideId }))
            {
                report.Append(CultureInfo.InvariantCulture,
                    $"    driver {driverId} {status,-9} sent {sentAt:HH:mm:ss.fff} expires {expiresAt:HH:mm:ss.fff}"
                    + $" {(respondedAt is null ? string.Empty : $"answered {respondedAt:HH:mm:ss.fff}")}\n");
            }

            report.Append("  dispatch.timers:\n");
            foreach (var (kind, fireAt, firedAt) in
                     await connection.QueryAsync<(string, DateTimeOffset, DateTimeOffset?)>(
                         """
                         SELECT kind, fire_at, fired_at FROM dispatch.timers
                          WHERE ride_id = @RideId ORDER BY fire_at, id;
                         """,
                         new { RideId = rideId }))
            {
                report.Append(CultureInfo.InvariantCulture,
                    $"    {kind,-20} due {fireAt:HH:mm:ss.fff} {(firedAt is null ? "(live)" : $"fired {firedAt:HH:mm:ss.fff}")}\n");
            }

            return report.ToString();
        }
        catch (Exception diagnosis) when (diagnosis is not OperationCanceledException)
        {
            // A diagnostic that throws would replace the real failure with its own, which is the
            // one outcome worse than no diagnostic at all.
            return $"\n{Marker} ride {rideId}: the history could not be read ({diagnosis.Message})\n";
        }
    }

    /// <summary>Marks a message as already carrying a history, so it is not appended twice.</summary>
    private const string Marker = "\n── ride history ──";

    private static bool ContainsJournal(Exception failure) =>
        failure.Message.Contains(Marker, StringComparison.Ordinal);
}

/// <summary>
/// A scenario failure with the ride's history attached.
/// </summary>
/// <remarks>
/// A distinct type rather than a rethrow of the original: xUnit prints the message of whatever it
/// catches, and re-throwing the assertion would lose everything appended to it.
/// </remarks>
internal sealed class RideScenarioException(string message, Exception inner) : Exception(message, inner);
