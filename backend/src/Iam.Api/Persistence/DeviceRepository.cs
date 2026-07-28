using Dapper;
using Npgsql;

namespace MageRide.Iam.Persistence;

/// <summary><c>iam.devices</c> — the per-install row a session binds to (AL-08, D-30).</summary>
public interface IDeviceRepository
{
    /// <summary>
    /// The device row for <paramref name="deviceKey"/> on this account, created on first sight.
    /// </summary>
    Task<Guid> EnsureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string deviceKey,
        string platform,
        string? fcmToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// The client <c>deviceId</c> a device row was created for — the <c>device_id</c> claim a
    /// rotated access token has to carry forward.
    /// </summary>
    Task<string?> FindKeyAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid deviceRowId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDeviceRepository"/>
public sealed class DeviceRepository : IDeviceRepository
{
    public Task<Guid> EnsureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string deviceKey,
        string platform,
        string? fcmToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Upsert on the 0105 partial unique index. COALESCE keeps a previously registered push
        // token when this sign-in did not carry one — losing it would silently stop notifications.
        return connection.QuerySingleAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO iam.devices (user_id, device_key, platform, fcm_apns_token)
            VALUES (@UserId, @DeviceKey, @Platform, @FcmToken)
            ON CONFLICT (user_id, device_key) WHERE device_key IS NOT NULL
            DO UPDATE SET platform = EXCLUDED.platform,
                          fcm_apns_token = COALESCE(EXCLUDED.fcm_apns_token, iam.devices.fcm_apns_token)
            RETURNING id;
            """,
            new { UserId = userId, DeviceKey = deviceKey, Platform = platform, FcmToken = fcmToken },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<string?> FindKeyAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid deviceRowId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT device_key FROM iam.devices WHERE id = @DeviceRowId;",
            new { DeviceRowId = deviceRowId },
            transaction,
            cancellationToken: cancellationToken));
    }
}
