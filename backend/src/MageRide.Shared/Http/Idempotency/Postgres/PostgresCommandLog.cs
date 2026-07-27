using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Shared.Http.Idempotency.Postgres;

/// <summary>
/// <see cref="ICommandLog"/> over a service's Postgres command-log table, implementing the
/// reserve-execute-record sequence from ADD §11.13 (R-14, R-18).
/// </summary>
/// <remarks>
/// <para>
/// The reservation is a single <c>INSERT … ON CONFLICT (idempotency_key) DO NOTHING</c>: the
/// unique index is what makes concurrent duplicates safe, not application-level locking.
/// </para>
/// <para>
/// <b>Table contract.</b> Beyond the D4' §5 columns this needs the response body stored
/// losslessly and the original content type recorded. With the defaults
/// (<see cref="CommandLogBodyStorage.Json"/> + <c>response_content_type</c>) the table is:
/// </para>
/// <code>
/// CREATE TABLE rides.command_log (
///   idempotency_key       TEXT PRIMARY KEY,
///   ride_id               UUID,
///   actor_type            TEXT NOT NULL,
///   actor_id              UUID,
///   command               TEXT NOT NULL,
///   request_hash          BYTEA NOT NULL,
///   response_status       SMALLINT,
///   response_body         JSON,          -- D4' says JSONB; see the C002 handoff note
///   response_content_type TEXT,          -- not in D4' yet
///   ts                    TIMESTAMPTZ NOT NULL DEFAULT now());
/// </code>
/// </remarks>
public sealed partial class PostgresCommandLog : ICommandLog
{
    private readonly INpgsqlConnectionFactory _connectionFactory;
    private readonly CommandLogOptions _options;
    private readonly ILogger<PostgresCommandLog> _logger;

    private readonly string _reserveSql;
    private readonly string _takeOverStaleSql;
    private readonly string _selectSql;
    private readonly string _completeSql;
    private readonly string _releaseSql;

    public PostgresCommandLog(
        INpgsqlConnectionFactory connectionFactory,
        IOptions<CommandLogOptions> options,
        ILogger<PostgresCommandLog> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var table = $"{Identifier(_options.Schema)}.{Identifier(_options.Table)}";
        var aggregateColumn = _options.AggregateIdColumn is null ? null : Identifier(_options.AggregateIdColumn);
        var contentTypeColumn = _options.ContentTypeColumn is null ? null : Identifier(_options.ContentTypeColumn);

        var insertColumns = new StringBuilder("idempotency_key, actor_type, actor_id, command, request_hash");
        var insertValues = new StringBuilder("@Key, @ActorType, @ActorId, @Command, @Hash");
        if (aggregateColumn is not null)
        {
            insertColumns.Append(", ").Append(aggregateColumn);
            insertValues.Append(", @AggregateId");
        }

        _reserveSql =
            $"""
             INSERT INTO {table} ({insertColumns}, ts)
             VALUES ({insertValues}, now())
             ON CONFLICT (idempotency_key) DO NOTHING;
             """;

        // Reclaims a reservation abandoned by a process that died mid-command. Scoped to rows
        // with no response, matching on the same request so a genuine reuse still reports
        // Mismatch rather than executing twice.
        _takeOverStaleSql =
            $"""
             UPDATE {table}
                SET ts = now()
              WHERE idempotency_key = @Key
                AND response_status IS NULL
                AND request_hash = @Hash
                AND ts < now() - @StaleAfter::interval;
             """;

        _selectSql =
            $"""
             SELECT request_hash, response_status, response_body{(contentTypeColumn is null ? string.Empty : $", {contentTypeColumn}")}
               FROM {table}
              WHERE idempotency_key = @Key;
             """;

        _completeSql =
            $"""
             UPDATE {table}
                SET response_status = @Status,
                    response_body = @Body{(contentTypeColumn is null ? string.Empty : $",\n                    {contentTypeColumn} = @ContentType")}
              WHERE idempotency_key = @Key;
             """;

        _releaseSql =
            $"""
             DELETE FROM {table}
              WHERE idempotency_key = @Key
                AND response_status IS NULL;
             """;
    }

    public async Task<CommandLogReservation> TryReserveAsync(CommandLogKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            _reserveSql,
            new
            {
                Key = key.IdempotencyKey,
                key.ActorType,
                key.ActorId,
                key.Command,
                Hash = key.RequestHash,
                AggregateId = key.AggregateId,
            },
            cancellationToken: cancellationToken));

        if (inserted == 1)
        {
            return CommandLogReservation.Reserved;
        }

        var existing = await ReadAsync(connection, key.IdempotencyKey, cancellationToken);
        if (existing is null)
        {
            // Raced with a Release between the failed insert and the read; the retry executes.
            return CommandLogReservation.Reserved;
        }

        if (!CryptographicEquals(existing.RequestHash, key.RequestHash))
        {
            return CommandLogReservation.Mismatch;
        }

        if (existing.ResponseStatus is not { } status)
        {
            var reclaimed = await connection.ExecuteAsync(new CommandDefinition(
                _takeOverStaleSql,
                new { Key = key.IdempotencyKey, Hash = key.RequestHash, StaleAfter = _options.StaleReservationAfter },
                cancellationToken: cancellationToken));

            if (reclaimed == 1)
            {
                _logger.LogWarning(
                    "Reclaimed a stale command-log reservation for Idempotency-Key {Key} ({Command})",
                    key.IdempotencyKey, key.Command);
                return CommandLogReservation.Reserved;
            }

            return CommandLogReservation.InProgress;
        }

        return CommandLogReservation.Replay(new CommandLogResponse(
            status,
            existing.Body ?? [],
            existing.ContentType ?? _options.DefaultContentType));
    }

    public async Task CompleteAsync(string idempotencyKey, CommandLogResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(response);

        if (!TryEncodeBody(response, out var body, out var dbType))
        {
            _logger.LogWarning(
                "Response for Idempotency-Key {Key} is not storable as {Storage}; releasing the reservation so a retry re-executes",
                idempotencyKey, _options.BodyStorage);
            await ReleaseAsync(idempotencyKey, cancellationToken);
            return;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(_completeSql, connection)
        {
            CommandTimeout = _connectionFactory.CommandTimeoutSeconds,
        };

        command.Parameters.AddWithValue("Key", idempotencyKey);
        command.Parameters.AddWithValue("Status", NpgsqlDbType.Smallint, (short)response.Status);
        command.Parameters.Add(new NpgsqlParameter("Body", dbType) { Value = body ?? (object)DBNull.Value });

        if (_options.ContentTypeColumn is not null)
        {
            command.Parameters.AddWithValue("ContentType", (object?)response.ContentType ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReleaseAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            _releaseSql, new { Key = idempotencyKey }, cancellationToken: cancellationToken));
    }

    private async Task<StoredCommand?> ReadAsync(NpgsqlConnection connection, string key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_selectSql, connection)
        {
            CommandTimeout = _connectionFactory.CommandTimeoutSeconds,
        };
        command.Parameters.AddWithValue("Key", key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var hash = (byte[])reader.GetValue(0);
        int? status = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetInt16(1);

        byte[]? body = null;
        if (!await reader.IsDBNullAsync(2, cancellationToken))
        {
            body = reader.GetValue(2) switch
            {
                byte[] bytes => bytes,
                string text => Encoding.UTF8.GetBytes(text),
                var other => throw new InvalidOperationException(
                    $"response_body came back as {other.GetType().Name}; expected bytea or a JSON text type."),
            };
        }

        string? contentType = null;
        if (_options.ContentTypeColumn is not null && !await reader.IsDBNullAsync(3, cancellationToken))
        {
            contentType = reader.GetString(3);
        }

        return new StoredCommand(hash, status, body, contentType);
    }

    private bool TryEncodeBody(CommandLogResponse response, out object? body, out NpgsqlDbType dbType)
    {
        if (_options.BodyStorage == CommandLogBodyStorage.Bytea)
        {
            dbType = NpgsqlDbType.Bytea;
            body = response.Body.Length == 0 ? null : response.Body;
            return true;
        }

        dbType = _options.BodyStorage == CommandLogBodyStorage.Json ? NpgsqlDbType.Json : NpgsqlDbType.Jsonb;

        if (response.Body.Length == 0)
        {
            body = null;
            return true;
        }

        var text = Encoding.UTF8.GetString(response.Body);
        if (!IsJson(text))
        {
            body = null;
            return false;
        }

        body = text;
        return true;
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool CryptographicEquals(byte[] left, byte[] right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);

    /// <summary>
    /// Quotes a configured identifier. The values come from configuration rather than a request,
    /// but a bad one would still be concatenated into SQL, so it is validated and quoted.
    /// </summary>
    private static string Identifier(string name)
    {
        if (!SafeIdentifier().IsMatch(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a valid unquoted Postgres identifier for the command-log table.", nameof(name));
        }

        return $"\"{name}\"";
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();

    private sealed record StoredCommand(byte[] RequestHash, int? ResponseStatus, byte[]? Body, string? ContentType);
}
