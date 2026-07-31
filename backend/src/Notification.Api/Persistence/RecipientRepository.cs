using System.Text;
using System.Text.Json;
using Dapper;
using MageRide.Notification.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.Notification.Persistence;

/// <summary>
/// Who to send to, and in which language.
/// </summary>
/// <remarks>
/// <para>
/// <b>This service reads <c>iam.users</c> directly, and writes exactly one of its columns.</b> The
/// read is the same read-only cross-context read ride-svc's <c>DriverSummaryRepository</c> and
/// iam-svc's own bootstrap make, and for the same reason: rendering one ride offer for twelve
/// candidate drivers cannot be twelve HTTP calls to iam-svc on a fifteen-second clock, and a
/// notification pipeline that stops when iam-svc redeploys is a pipeline that drops offers.
/// </para>
/// <para>
/// <b>The write is <c>notif_prefs</c> and nothing else</b>, and it is deliberate rather than
/// convenient. D3' puts <c>PUT /v1/notify/preferences</c> on this service and iam-svc's own
/// CLAUDE.md says the route "writes the same column"; the alternative — a
/// <c>comms.notification_preferences</c> table of this service's own — would mean
/// <c>GET /v1/users/me</c> reporting switches that gate nothing, which is worse than a shared
/// column with one documented writer on each side. Both services apply the same safety-critical
/// exclusion list, which is what stops the two disagreeing about whether a mute took effect.
/// </para>
/// </remarks>
public interface IRecipientRepository
{
    /// <summary>Resolves one account. <see langword="null"/> when there is no such user.</summary>
    Task<NotificationRecipient?> FindAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Resolves many in one round trip — the offer fan-out and the broadcast both need it.</summary>
    Task<IReadOnlyDictionary<Guid, NotificationRecipient>> FindManyAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>
    /// AL-21 and AL-45's branch input: is this number an account? Answered from
    /// <c>iam.users.phone</c>, which is E.164 and unique.
    /// </summary>
    Task<NotificationRecipient?> FindByPhoneAsync(string phoneE164, CancellationToken cancellationToken);

    /// <summary>Merges switches into <c>iam.users.notif_prefs</c> and returns what is now in force.</summary>
    Task<IReadOnlyDictionary<string, bool>> UpdatePreferencesAsync(
        Guid userId, IReadOnlyDictionary<string, bool> preferences, CancellationToken cancellationToken);

    /// <summary>
    /// US-14.8's audience, resolved to user ids. <c>role</c> is matched against the union of
    /// <c>iam.user_roles</c> (AL-06), so a driver who also books rides is in the driver audience.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListAudienceAsync(string? role, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRecipientRepository"/>
internal sealed class RecipientRepository(INpgsqlConnectionFactory connections) : IRecipientRepository
{
    private readonly INpgsqlConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    public async Task<NotificationRecipient?> FindAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(
            new CommandDefinition(
                """
                SELECT id, phone, language, notif_prefs::text AS notif_prefs
                  FROM iam.users WHERE id = @UserId;
                """,
                new { UserId = userId },
                cancellationToken: cancellationToken));

        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyDictionary<Guid, NotificationRecipient>> FindManyAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, NotificationRecipient>();
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<UserRow>(
            new CommandDefinition(
                """
                SELECT id, phone, language, notif_prefs::text AS notif_prefs
                  FROM iam.users WHERE id = ANY(@UserIds);
                """,
                new { UserIds = userIds.ToArray() },
                cancellationToken: cancellationToken));

        return rows.ToDictionary(static row => row.Id, Map);
    }

    public async Task<NotificationRecipient?> FindByPhoneAsync(
        string phoneE164, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneE164);

        await using var connection = await _connections.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(
            new CommandDefinition(
                """
                SELECT id, phone, language, notif_prefs::text AS notif_prefs
                  FROM iam.users WHERE phone = @Phone;
                """,
                new { Phone = phoneE164 },
                cancellationToken: cancellationToken));

        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyDictionary<string, bool>> UpdatePreferencesAsync(
        Guid userId, IReadOnlyDictionary<string, bool> preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        await using var connection = await _connections.OpenAsync(cancellationToken);

        // `||` merges rather than replaces: the body is a set of switches the user just touched,
        // not the whole document, so a client that sends one key must not silently clear the rest.
        var updated = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                """
                UPDATE iam.users
                   SET notif_prefs = notif_prefs || @Patch::jsonb
                 WHERE id = @UserId
                 RETURNING notif_prefs::text;
                """,
                new { UserId = userId, Patch = Preferences.Write(preferences) },
                cancellationToken: cancellationToken));

        return Preferences.Read(updated);
    }

    public async Task<IReadOnlyList<Guid>> ListAudienceAsync(
        string? role, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        // A blocked account is not an audience member: US-14.8's banner is for people who can use
        // the platform, and the push would be the only thing they still received from it.
        var rows = await connection.QueryAsync<Guid>(
            new CommandDefinition(
                """
                SELECT u.id
                  FROM iam.users u
                 WHERE u.is_blocked = false
                   AND (@Role::text IS NULL
                        OR u.role = @Role
                        OR EXISTS (SELECT 1 FROM iam.user_roles r
                                    WHERE r.user_id = u.id AND r.role = @Role))
                 ORDER BY u.created_at
                 LIMIT @Limit;
                """,
                new { Role = role, Limit = limit },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    private static NotificationRecipient Map(UserRow row) =>
        new(row.Id, row.Phone, Languages.Normalise(row.Language), Preferences.Read(row.NotifPrefs));

    private sealed record UserRow(Guid Id, string? Phone, string? Language, string? NotifPrefs);
}

/// <summary>
/// <c>iam.users.notif_prefs</c>, read and written with its keys exactly as they are.
/// </summary>
/// <remarks>
/// <b>The keys are data, not property names.</b> <c>MageRideJson</c> sets
/// <c>DictionaryKeyPolicy = CamelCase</c>, which would write <c>SCHEDULED_REMINDER</c> back as
/// <c>sCHEDULED_REMINDER</c> once, silently, and the mute the user set would stop matching the
/// notification it was for. iam-svc solves it with a converter
/// (<c>LiteralKeyDictionaryConverter</c>, C027, whose own remarks predict this service needing the
/// same treatment); this side writes the document by hand, which needs no serializer options to be
/// configured correctly by a future caller.
/// </remarks>
internal static class Preferences
{
    public static IReadOnlyDictionary<string, bool> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return NotificationRecipient.NoPreferences;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return NotificationRecipient.NoPreferences;
            }

            var values = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    values[property.Name] = property.Value.GetBoolean();
                }
            }

            return values;
        }
        catch (JsonException)
        {
            // The column is NOT NULL DEFAULT '{}' and two services write it through a boolean map,
            // so this is a belt to the braces. The failure it guards is every notification to one
            // person turning into a 500.
            return NotificationRecipient.NoPreferences;
        }
    }

    public static string Write(IReadOnlyDictionary<string, bool> preferences)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var (key, value) in preferences)
            {
                // WriteBoolean takes the name verbatim — there is no naming policy on a
                // Utf8JsonWriter, which is the point of writing the document by hand.
                writer.WriteBoolean(key, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
