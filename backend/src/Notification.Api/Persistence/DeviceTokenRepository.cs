using Dapper;
using MageRide.Notification.Push;
using MageRide.Shared.Persistence;

namespace MageRide.Notification.Persistence;

/// <summary><c>comms.notification_tokens</c> (migrations 1302 + 1308).</summary>
public interface IDeviceTokenRepository
{
    /// <summary>
    /// Registers or refreshes this install's token.
    /// </summary>
    /// <remarks>
    /// Two unique indexes decide the outcome and the order they are applied in matters.
    /// <c>ux_notif_tokens_token</c> moves a token that another account holds — FCM and APNs reissue
    /// one to whichever install now owns it, so the old row would otherwise keep receiving E-01
    /// offers for somebody else's account. <c>ux_notif_tokens_device</c> (1308) replaces the token
    /// this *install* previously held, which is the reinstall case: same handset, new token, and
    /// without it the dead handle survives.
    /// </remarks>
    Task UpsertAsync(
        Guid userId, string platform, string token, string? deviceId, CancellationToken cancellationToken);

    /// <summary>Every live handle for a user, freshest first.</summary>
    Task<IReadOnlyList<DeviceToken>> ListForUserAsync(
        Guid userId, DateTimeOffset staleBefore, CancellationToken cancellationToken);

    /// <summary>Drops a handle the provider says is dead.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDeviceTokenRepository"/>
internal sealed class DeviceTokenRepository(INpgsqlConnectionFactory connections) : IDeviceTokenRepository
{
    private readonly INpgsqlConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    public async Task UpsertAsync(
        Guid userId, string platform, string token, string? deviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        await using var connection = await _connections.OpenAsync(cancellationToken);

        // The device row is retired first, so an install that came back with a new token does not
        // leave its old one behind to collide with somebody else's later. Both statements are in one
        // implicit transaction only if they share one — they do not, and they do not need to: the
        // worst interleaving leaves a duplicate handle for one user, which the token index then
        // resolves on the next registration.
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    DELETE FROM comms.notification_tokens
                     WHERE user_id = @UserId AND device_id = @DeviceId AND token <> @Token;
                    """,
                    new { UserId = userId, DeviceId = deviceId, Token = token },
                    cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO comms.notification_tokens (user_id, platform, token, device_id, last_seen_at)
                VALUES (@UserId, @Platform, @Token, @DeviceId, now())
                ON CONFLICT (token) DO UPDATE
                   SET user_id = EXCLUDED.user_id,
                       platform = EXCLUDED.platform,
                       device_id = COALESCE(EXCLUDED.device_id, comms.notification_tokens.device_id),
                       last_seen_at = now();
                """,
                new { UserId = userId, Platform = platform, Token = token, DeviceId = deviceId },
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<DeviceToken>> ListForUserAsync(
        Guid userId, DateTimeOffset staleBefore, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<DeviceToken>(
            new CommandDefinition(
                """
                SELECT id, user_id, platform, token
                  FROM comms.notification_tokens
                 WHERE user_id = @UserId
                   AND last_seen_at >= @StaleBefore
                 ORDER BY last_seen_at DESC;
                """,
                new { UserId = userId, StaleBefore = staleBefore },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM comms.notification_tokens WHERE id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken));
    }
}
