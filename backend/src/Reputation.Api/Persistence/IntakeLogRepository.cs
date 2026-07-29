using Dapper;
using MageRide.Reputation.Domain;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Reputation.Persistence;

/// <summary>
/// <c>reputation.intake_log</c> — the ledger that makes counting exactly-once.
/// </summary>
/// <remarks>
/// D6' §2.3 makes topic delivery at-least-once and a gRPC retry has the same shape, so the same
/// cancellation can arrive twice. Counting it twice would booking-disable a passenger in two rides
/// rather than three (D5' §7.2). The primary key settles it: the insert is the claim, and a caller
/// that inserted nothing knows the fact was already counted without a prior read.
/// </remarks>
public interface IIntakeLogRepository
{
    /// <summary>
    /// Records the fact. Returns <see langword="false"/> when it was already recorded, in which
    /// case nothing else in the intake may run.
    /// </summary>
    Task<bool> TryClaimAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ReputationFact fact, DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Completed rides for a <c>(passenger, driver)</c> pair inside a window — the E-07 input.</summary>
    Task<IReadOnlyList<PairCount>> CountPairsAsync(
        NpgsqlConnection connection, DateTimeOffset since, int threshold, int limit, CancellationToken cancellationToken);
}

/// <summary>One <c>(passenger, driver)</c> pair and how often it completed a ride in the window.</summary>
public sealed record PairCount(Guid PassengerId, Guid DriverId, int Rides, DateTimeOffset FirstAt, DateTimeOffset LastAt);

/// <inheritdoc cref="IIntakeLogRepository"/>
public sealed class IntakeLogRepository : IIntakeLogRepository
{
    public async Task<bool> TryClaimAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ReputationFact fact, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(fact);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO reputation.intake_log
              (dedupe_key, kind, subject_id, subject_role, ride_id, source, detail, ts)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (dedupe_key) DO NOTHING;
            """,
            connection,
            transaction);

        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = fact.DedupeKey });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = fact.Kind });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = fact.SubjectId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = fact.SubjectRole });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = (object?)fact.RideId ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = fact.Source });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = (object?)fact.Detail ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = now });

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<PairCount>> CountPairsAsync(
        NpgsqlConnection connection, DateTimeOffset since, int threshold, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The pair frequency is read out of this service's own ledger rather than out of
        // rides.rides. Both would answer, and the ledger is the honest source: it is what
        // reputation-svc was actually told, it survives a ride row being erased under PDPA (E-06),
        // and it keeps the detector inside this bounded context — the fence for this component.
        // Every completion carries both sides (see RideEventHandler), so a pair is a self-join on
        // ride_id between the two roles.
        var rows = await connection.QueryAsync<PairCount>(new CommandDefinition(
            """
            SELECT p.subject_id AS PassengerId,
                   d.subject_id AS DriverId,
                   count(*)::int AS Rides,
                   min(p.ts) AS FirstAt,
                   max(p.ts) AS LastAt
              FROM reputation.intake_log p
              JOIN reputation.intake_log d
                ON d.ride_id = p.ride_id
               AND d.kind = 'completion'
               AND d.subject_role = 'driver'
             WHERE p.kind = 'completion'
               AND p.subject_role = 'passenger'
               AND p.ride_id IS NOT NULL
               AND p.ts >= @Since
               AND d.ts >= @Since
             GROUP BY p.subject_id, d.subject_id
            HAVING count(*) >= @Threshold
             ORDER BY count(*) DESC
             LIMIT @Limit;
            """,
            new { Since = since, Threshold = threshold, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}
