using System.Text.RegularExpressions;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Shared.Messaging;

/// <summary>
/// Appends events to a service's outbox table inside the caller's transaction (D6' §2.4, R-13).
/// </summary>
public interface IOutboxWriter
{
    /// <summary>
    /// Writes one event on <paramref name="unitOfWork"/>'s transaction and signals the dispatcher.
    /// The signal is a <c>pg_notify</c> in the same transaction, so it fires on COMMIT and never
    /// for a rolled-back change.
    /// </summary>
    /// <returns>The generated <c>id</c> of the outbox row.</returns>
    Task<long> WriteAsync(IUnitOfWork unitOfWork, OutboxRecord record, CancellationToken cancellationToken = default);

    /// <summary>Writes several events on one transaction with a single notify.</summary>
    Task<IReadOnlyList<long>> WriteAsync(
        IUnitOfWork unitOfWork, IReadOnlyCollection<OutboxRecord> records, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IOutboxWriter"/>
public sealed partial class OutboxWriter : IOutboxWriter
{
    private readonly OutboxOptions _options;
    private readonly string _insertSql;

    public OutboxWriter(IOptions<OutboxOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        var table = $"{Identifier(_options.Schema)}.{Identifier(_options.Table)}";
        _insertSql =
            $"""
             INSERT INTO {table} (aggregate_id, event_type, payload, created_at)
             VALUES ($1, $2, $3, now())
             RETURNING id;
             """;
    }

    public async Task<long> WriteAsync(IUnitOfWork unitOfWork, OutboxRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var ids = await WriteAsync(unitOfWork, [record], cancellationToken);
        return ids[0];
    }

    public async Task<IReadOnlyList<long>> WriteAsync(
        IUnitOfWork unitOfWork, IReadOnlyCollection<OutboxRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return [];
        }

        var ids = new List<long>(records.Count);

        foreach (var record in records)
        {
            await using var command = new NpgsqlCommand(_insertSql, unitOfWork.Connection, unitOfWork.Transaction);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = record.AggregateId });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = record.EventType });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = record.Payload });

            var id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            ids.Add(id);
        }

        await NotifyAsync(unitOfWork, ids[^1], cancellationToken);
        return ids;
    }

    /// <summary>
    /// Queues the dispatcher wake-up. <c>NOTIFY</c> issued inside a transaction is delivered by
    /// Postgres at COMMIT — that ordering is what E-09 relies on and what keeps R-13 true.
    /// </summary>
    private async Task NotifyAsync(IUnitOfWork unitOfWork, long highestId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_notify($1, $2);", unitOfWork.Connection, unitOfWork.Transaction);

        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = _options.Channel });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = highestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string Identifier(string name)
    {
        if (!SafeIdentifier().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not a valid unquoted Postgres identifier.", nameof(name));
        }

        return $"\"{name}\"";
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();
}
