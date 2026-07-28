using System.Net;
using Dapper;
using Npgsql;

namespace MageRide.Provisioning.Persistence;

/// <summary>
/// <c>prov.imei_sightings</c> — the T-08 anti-clone evidence trail (migration 0404).
/// </summary>
/// <remarks>
/// <b>This is what actually catches a clone.</b> A cloned tracker does not call
/// <c>POST /v1/trackers/bind</c>; it dials the adapter, which resolves it through
/// <c>GET /v1/internal/trackers/{imei}/validate</c>. Recording each presentation with the
/// credential serial that made it turns "two devices presenting the same IMEI within 24 h"
/// (D6' §4.3) into a query — two distinct serials inside the window — rather than a rule with no
/// evidence behind it.
/// </remarks>
public interface IImeiSightingRepository
{
    Task RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string imei,
        string? credentialSerial,
        string source,
        Guid? actorId,
        IPAddress? remoteAddress,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Credential serials, other than <paramref name="excludingSerial"/>, that have presented this
    /// IMEI since <paramref name="since"/>.
    /// </summary>
    Task<IReadOnlyList<string>> ListOtherSerialsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string imei,
        string? excludingSerial,
        DateTimeOffset since,
        CancellationToken cancellationToken);

    /// <summary>Drops sightings older than the anti-clone window; they can no longer prove anything.</summary>
    Task<int> PruneAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, DateTimeOffset before, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IImeiSightingRepository"/>
public sealed class ImeiSightingRepository : IImeiSightingRepository
{
    public async Task RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string imei,
        string? credentialSerial,
        string source,
        Guid? actorId,
        IPAddress? remoteAddress,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO prov.imei_sightings (imei, credential_serial, source, actor_id, remote_addr, seen_at)
            VALUES (@Imei, @CredentialSerial, @Source, @ActorId, @RemoteAddress::inet, @Now);
            """,
            new
            {
                Imei = imei,
                CredentialSerial = credentialSerial,
                Source = source,
                ActorId = actorId,
                // Sent as text and cast by Postgres: Dapper has no built-in map for IPAddress, and
                // a type handler for one column would be a process-global registration every other
                // service inherits.
                RemoteAddress = remoteAddress?.ToString(),
                Now = now,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<string>> ListOtherSerialsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string imei,
        string? excludingSerial,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Sightings with no serial are excluded rather than counted as a distinct device: an
        // adapter that does not report one would otherwise make every second connection look like
        // a clone, and a quarantine is not a thing to guess at.
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT DISTINCT credential_serial
              FROM prov.imei_sightings
             WHERE imei = @Imei
               AND seen_at >= @Since
               AND credential_serial IS NOT NULL
               AND (@Excluding::text IS NULL OR credential_serial <> @Excluding);
            """,
            new { Imei = imei, Since = since, Excluding = excludingSerial },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public Task<int> PruneAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, DateTimeOffset before, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM prov.imei_sightings WHERE seen_at < @Before;",
            new { Before = before },
            transaction,
            cancellationToken: cancellationToken));
    }
}
