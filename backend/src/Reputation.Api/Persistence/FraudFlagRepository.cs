using Dapper;
using MageRide.Reputation.Domain;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Reputation.Persistence;

/// <summary>
/// <c>reputation.fraud_flags</c> — the E-07 review queue.
/// </summary>
public interface IFraudFlagRepository
{
    /// <summary>
    /// Raises a signal. Returns the new row, or <see langword="null"/> when this
    /// <c>(kind, subject, counterparty, window)</c> has already been flagged.
    /// </summary>
    /// <remarks>
    /// The uniqueness is <c>ux_fraud_flags_window</c>'s, not a prior read's: the detector runs on
    /// every replica and a check-then-insert would let two passes both find nothing and both write.
    /// This is what makes the component's DoD — "raises <c>fraud.suspected</c> exactly once per
    /// detection window" — a property of the database rather than of the scheduler.
    /// </remarks>
    Task<FraudFlagRow?> TryRaiseAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, FraudSignal signal, DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<FraudFlagRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid flagId, CancellationToken cancellationToken);

    /// <summary>One page of the queue, newest first. <paramref name="limit"/> + 1 rows are read to
    /// decide <c>hasMore</c> without a second query.</summary>
    Task<IReadOnlyList<FraudFlagRow>> ListAsync(
        NpgsqlConnection connection, string? kind, string? status, DateTimeOffset? before, int limit,
        CancellationToken cancellationToken);

    /// <summary>Dismisses or actions a flag. Returns the row as it now stands.</summary>
    Task<FraudFlagRow?> ResolveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid flagId,
        string status,
        Guid resolvedBy,
        string? note,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFraudFlagRepository"/>
public sealed class FraudFlagRepository : IFraudFlagRepository
{
    private const string Columns =
        """
        id AS Id,
        kind AS Kind,
        subject_id AS SubjectId,
        subject_type AS SubjectType,
        related_id AS RelatedId,
        status AS Status,
        window_key AS WindowKey,
        detail #>> '{summary}' AS Detail,
        resolved_by AS ResolvedBy,
        resolved_at AS ResolvedAt,
        resolution_note AS ResolutionNote,
        ts AS Ts
        """;

    public async Task<FraudFlagRow?> TryRaiseAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, FraudSignal signal, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(signal);

        var detail = FraudFlagDetail.Serialize(signal);

        await using var command = new NpgsqlCommand(
            $"""
             INSERT INTO reputation.fraud_flags
               (kind, subject_id, subject_type, related_id, status, window_key, detail, ts)
             VALUES ($1, $2, $3, $4, 'open', $5, $6, $7)
             ON CONFLICT (kind, subject_id, related_id, window_key) DO NOTHING
             RETURNING {Columns};
             """,
            connection,
            transaction);

        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = signal.Kind });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = signal.SubjectId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = signal.SubjectType });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = (object?)signal.RelatedId ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = signal.WindowKey });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = detail });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = now });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public Task<FraudFlagRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid flagId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<FraudFlagRow>(new CommandDefinition(
            $"SELECT {Columns} FROM reputation.fraud_flags WHERE id = @FlagId;",
            new { FlagId = flagId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<FraudFlagRow>> ListAsync(
        NpgsqlConnection connection, string? kind, string? status, DateTimeOffset? before, int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Keyset pagination on (ts, id): D3' §0's cursor is opaque and an OFFSET would drift as the
        // detector inserts underneath a paging admin.
        //
        // The ::text / ::timestamptz casts on the optional filters are not decoration: Postgres
        // cannot infer a parameter's type from `$1 IS NULL` alone and answers 42P08 for the whole
        // query, so an unfiltered list would fail rather than return everything.
        var rows = await connection.QueryAsync<FraudFlagRow>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM reputation.fraud_flags
              WHERE (@Kind::text IS NULL OR kind = @Kind::text)
                AND (@Status::text IS NULL OR status = @Status::text)
                AND (@Before::timestamptz IS NULL OR ts < @Before::timestamptz)
              ORDER BY ts DESC, id DESC
              LIMIT @Limit;
             """,
            new { Kind = kind, Status = status, Before = before, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<FraudFlagRow?> ResolveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid flagId,
        string status,
        Guid resolvedBy,
        string? note,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        // Guarded on the current status: re-resolving with the same verdict is a no-op that still
        // returns the row (the contract says so), and changing a resolved flag's verdict matches
        // nothing here and is answered 409 by the caller.
        return await connection.QuerySingleOrDefaultAsync<FraudFlagRow>(new CommandDefinition(
            $"""
             UPDATE reputation.fraud_flags
                SET status = @Status,
                    resolved_by = @ResolvedBy,
                    resolved_at = @Now,
                    resolution_note = @Note
              WHERE id = @FlagId
                AND status IN ('open', @Status)
             RETURNING {Columns};
             """,
            new { FlagId = flagId, Status = status, ResolvedBy = resolvedBy, Note = note, Now = now },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static FraudFlagRow Read(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetGuid(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetFieldValue<DateTimeOffset>(11));
}
