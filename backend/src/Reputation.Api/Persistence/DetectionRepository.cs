using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.Reputation.Domain;
using MageRide.Shared.Http;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Reputation.Persistence;

/// <summary>Two accounts seen behind one device binding (<c>iam.devices.device_key</c>).</summary>
public sealed record DeviceCluster(string DeviceKey, Guid[] UserIds);

/// <summary>Several accounts seen on one address or autonomous system.</summary>
public sealed record NetworkCluster(string Key, string Scope, Guid[] UserIds, int Observations);

/// <summary>
/// The two E-07 inputs that are not this service's own ledger: device bindings and network
/// observations.
/// </summary>
/// <remarks>
/// <c>iam.devices</c> is <b>read and only ever read</b> — iam-svc (C020) owns it, and the same rule
/// ride-svc states about <c>registry.vehicles</c> applies: reputation-svc writes nothing outside
/// the <c>reputation</c> schema except <c>dispatch.driver_levels</c>, which D5' §4.2 makes its own
/// (see <see cref="DriverLevelRepository"/>) and <c>audit.events</c>, which is append-only and
/// shared by every service that takes an admin decision (D-35).
/// </remarks>
public interface IDetectionRepository
{
    /// <summary>Device keys carrying at least <paramref name="threshold"/> distinct accounts.</summary>
    Task<IReadOnlyList<DeviceCluster>> FindSharedDevicesAsync(
        NpgsqlConnection connection, int threshold, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Addresses carrying at least <paramref name="threshold"/> distinct accounts inside the
    /// window, then the same by ASN for the callers that resolved one.
    /// </summary>
    Task<IReadOnlyList<NetworkCluster>> FindNetworkClustersAsync(
        NpgsqlConnection connection, DateTimeOffset since, int threshold, int limit, CancellationToken cancellationToken);

    Task RecordObservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        Guid? rideId,
        IPAddress ip,
        int? asn,
        string? userAgent,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Drops observations past their PDPA retention (E-06). Returns rows removed.</summary>
    Task<int> PurgeObservationsAsync(
        NpgsqlConnection connection, DateTimeOffset before, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDetectionRepository"/>
public sealed class DetectionRepository : IDetectionRepository
{
    public async Task<IReadOnlyList<DeviceCluster>> FindSharedDevicesAsync(
        NpgsqlConnection connection, int threshold, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // ux_devices_user_key (0105) makes one row per (user, install), so a device_key appearing
        // under two user_ids is two accounts on one handset — which is exactly the "device-binding
        // cross-check" E-07 asks for, and is not otherwise visible to anybody.
        var rows = await connection.QueryAsync<(string DeviceKey, Guid[] UserIds)>(new CommandDefinition(
            """
            SELECT device_key AS DeviceKey, array_agg(DISTINCT user_id) AS UserIds
              FROM iam.devices
             WHERE device_key IS NOT NULL
             GROUP BY device_key
            HAVING count(DISTINCT user_id) >= @Threshold
             ORDER BY count(DISTINCT user_id) DESC
             LIMIT @Limit;
            """,
            new { Threshold = threshold, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows.Select(row => new DeviceCluster(row.DeviceKey, row.UserIds))];
    }

    public async Task<IReadOnlyList<NetworkCluster>> FindNetworkClustersAsync(
        NpgsqlConnection connection, DateTimeOffset since, int threshold, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Two scopes in one query. `ip` is the sharp signal — the same address is the same NAT or
        // the same handset — and `asn` is the loose one, which is why the caller applies a higher
        // threshold to it. Addresses without a resolved ASN fall back to the /24 (v4) or /48 (v6)
        // prefix, so a caller that could not look one up still contributes a clustering signal.
        var rows = await connection.QueryAsync<(string Key, string Scope, Guid[] UserIds, int Observations)>(
            new CommandDefinition(
                """
                SELECT host(ip) AS Key, 'ip' AS Scope,
                       array_agg(DISTINCT user_id) AS UserIds, count(*)::int AS Observations
                  FROM reputation.network_observations
                 WHERE observed_at >= @Since
                 GROUP BY ip
                HAVING count(DISTINCT user_id) >= @Threshold

                UNION ALL

                SELECT coalesce(asn::text, host(network(set_masklen(ip, CASE family(ip) WHEN 4 THEN 24 ELSE 48 END))))
                         AS Key,
                       CASE WHEN asn IS NULL THEN 'prefix' ELSE 'asn' END AS Scope,
                       array_agg(DISTINCT user_id) AS UserIds, count(*)::int AS Observations
                  FROM reputation.network_observations
                 WHERE observed_at >= @Since
                 GROUP BY 1, 2
                HAVING count(DISTINCT user_id) >= @Threshold

                 LIMIT @Limit;
                """,
                new { Since = since, Threshold = threshold, Limit = limit },
                cancellationToken: cancellationToken));

        return [.. rows.Select(row => new NetworkCluster(row.Key, row.Scope, row.UserIds, row.Observations))];
    }

    public async Task RecordObservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        Guid? rideId,
        IPAddress ip,
        int? asn,
        string? userAgent,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(ip);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO reputation.network_observations (user_id, ride_id, ip, asn, user_agent, observed_at)
            VALUES ($1, $2, $3, $4, $5, $6);
            """,
            connection,
            transaction);

        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = (object?)rideId ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Inet, Value = ip });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Integer,
            Value = (object?)asn ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = (object?)userAgent ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = now });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<int> PurgeObservationsAsync(
        NpgsqlConnection connection, DateTimeOffset before, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM reputation.network_observations WHERE observed_at < @Before;",
            new { Before = before },
            cancellationToken: cancellationToken));
    }
}

/// <summary>Renders a signal's evidence into the <c>detail</c> JSONB column.</summary>
/// <remarks>
/// <c>summary</c> is always present and is the sentence the admin queue shows; everything else is
/// the detector's own evidence, kept structured so a later heuristic can be evaluated against
/// flags that were raised before it existed. 0802's header says the column is open on purpose.
/// </remarks>
public static class FraudFlagDetail
{
    public static string Serialize(FraudSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var detail = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["summary"] = signal.Summary,
        };

        foreach (var (key, value) in signal.Detail)
        {
            detail[key] = value;
        }

        return JsonSerializer.Serialize(detail, MageRideJson.StorageOptions);
    }
}
