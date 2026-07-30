using Dapper;
using MageRide.Content.Domain;
using MageRide.Shared.Http;
using MageRide.Shared.Persistence;
using System.Text.Json;

namespace MageRide.Content.Persistence;

/// <summary>
/// Who an announcement is for — the decidable half of <c>content.broadcasts.audience</c>.
/// </summary>
/// <remarks>
/// <para>
/// C005's column comment gives the example <c>{"role":"driver","city":"colombo"}</c> and says
/// notification-svc interprets it. This service serves the <b>in-app banner</b> (US-14.8), where the
/// only facts about the caller are the ones in the bearer: <c>role</c> and <c>app</c>. There is no
/// city claim — <c>iam.users.operating_city_code</c> is a column in another bounded context — so a
/// <c>city</c> predicate could only be ignored here, and a banner shown to the whole island because a
/// predicate was quietly dropped is worse than one that could not be published.
/// </para>
/// <para>
/// So the publish path <b>refuses</b> a selector containing anything but these two, and both are
/// evaluated on every read. Named in the C045 handoff: city targeting needs the city on the token or
/// a user-row read, and neither exists.
/// </para>
/// </remarks>
internal sealed record BroadcastAudience(string? Role, string? App)
{
    /// <summary>Nobody is excluded.</summary>
    public static readonly BroadcastAudience Everyone = new(null, null);

    /// <summary>Whether this selector has anything to check.</summary>
    public bool IsEveryone => Role is null && App is null;

    /// <summary>Whether a caller holding <paramref name="roles"/> in <paramref name="app"/> matches.</summary>
    /// <remarks>
    /// The role test is over the caller's whole role set, not their primary role: AL-06 makes
    /// effective permissions the union of every role held, and a driver who is also a passenger must
    /// see the driver announcement.
    /// </remarks>
    public bool Matches(IReadOnlyCollection<string> roles, string? app)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (Role is not null && !roles.Contains(Role, StringComparer.Ordinal))
        {
            return false;
        }

        return App is null || string.Equals(App, app, StringComparison.Ordinal);
    }
}

/// <summary>One announcement, as stored.</summary>
internal sealed record BroadcastRow(
    Guid Id,
    TrilingualText Message,
    BroadcastAudience Audience,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt)
{
    /// <summary>Whether the window covers <paramref name="at"/>.</summary>
    /// <remarks>
    /// Start is inclusive and end is exclusive, so a banner scheduled for 09:00 is up at 09:00 and a
    /// banner ending at 17:00 is down at 17:00. The alternative — both inclusive — leaves two
    /// broadcasts scheduled back to back both showing for one instant.
    /// </remarks>
    public bool IsLiveAt(DateTimeOffset at) => StartsAt <= at && (EndsAt is null || EndsAt > at);
}

/// <summary>
/// <c>content.broadcasts</c> — the US-14.8 in-app announcement banner.
/// </summary>
internal interface IBroadcastRepository
{
    /// <summary>
    /// Every broadcast whose window overlaps <c>[asOf, horizon]</c>, newest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately <b>not</b> filtered to "live now": the result is cached for
    /// <c>Content:CacheTtl</c> and a window filter applied at load time would hold back a broadcast
    /// scheduled to start inside the TTL for up to that long. So the load reaches one TTL into the
    /// future and the exact window is applied per request.
    /// </para>
    /// <para>
    /// <b>It does not reach further than that, and that is what makes <paramref name="limit"/>
    /// safe.</b> Rows come back newest-scheduled first, so without a horizon a batch of announcements
    /// scheduled for next month would fill the limit and push today's live banner out of the answer.
    /// A row beyond the horizon is loaded by the reload that happens before it starts.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<BroadcastRow>> ReadLiveAsync(
        DateTimeOffset asOf, DateTimeOffset horizon, int limit, CancellationToken cancellationToken);

    /// <summary>Publishes one announcement.</summary>
    Task<BroadcastRow> InsertAsync(
        TrilingualText message,
        BroadcastAudience audience,
        DateTimeOffset startsAt,
        DateTimeOffset? endsAt,
        Guid author,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBroadcastRepository"/>
internal sealed class BroadcastRepository(INpgsqlConnectionFactory connections) : IBroadcastRepository
{
    public async Task<IReadOnlyList<BroadcastRow>> ReadLiveAsync(
        DateTimeOffset asOf, DateTimeOffset horizon, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<(
            Guid Id, string Message, string? Audience, DateTimeOffset StartsAt, DateTimeOffset? EndsAt)>(
            new CommandDefinition(
                """
                SELECT id,
                       message_by_lang::text AS message,
                       audience::text        AS audience,
                       coalesce(scheduled_at, created_at) AS starts_at,
                       ends_at
                  FROM content.broadcasts
                 WHERE (ends_at IS NULL OR ends_at > @AsOf)
                   AND coalesce(scheduled_at, created_at) <= @Horizon
                 ORDER BY coalesce(scheduled_at, created_at) DESC, id
                 LIMIT @Limit;
                """,
                new { AsOf = asOf, Horizon = horizon, Limit = limit },
                cancellationToken: cancellationToken));

        return
        [
            .. rows.Select(row => new BroadcastRow(
                row.Id,
                TrilingualText.FromStored(ReferenceDataRepository.Parse(row.Message), $"broadcast {row.Id} message"),
                ParseAudience(row.Audience),
                row.StartsAt,
                row.EndsAt)),
        ];
    }

    public async Task<BroadcastRow> InsertAsync(
        TrilingualText message,
        BroadcastAudience audience,
        DateTimeOffset startsAt,
        DateTimeOffset? endsAt,
        Guid author,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(audience);

        await using var connection = await connections.OpenAsync(cancellationToken);

        // scheduled_at is written even for "now", so `coalesce(scheduled_at, created_at)` on the read
        // is the same instant the response reported rather than whatever the row's created_at ended
        // up being.
        var id = await connection.ExecuteScalarAsync<Guid>(
            new CommandDefinition(
                """
                INSERT INTO content.broadcasts
                      (message_by_lang, audience, scheduled_at, ends_at, created_by)
                VALUES (@Message::jsonb, @Audience::jsonb, @StartsAt, @EndsAt, @Author)
                RETURNING id;
                """,
                new
                {
                    Message = JsonSerializer.Serialize(message.Values, MageRideJson.StorageOptions),
                    Audience = audience.IsEveryone
                        ? null
                        : JsonSerializer.Serialize(ToStored(audience), MageRideJson.StorageOptions),
                    StartsAt = startsAt,
                    EndsAt = endsAt,
                    Author = author,
                },
                cancellationToken: cancellationToken));

        return new BroadcastRow(id, message, audience, startsAt, endsAt);
    }

    /// <summary>The wire and column shape: C005's singular keys, and only the two that are decidable.</summary>
    private static Dictionary<string, string> ToStored(BroadcastAudience audience)
    {
        var stored = new Dictionary<string, string>(StringComparer.Ordinal);

        if (audience.Role is not null)
        {
            stored["role"] = audience.Role;
        }

        if (audience.App is not null)
        {
            stored["app"] = audience.App;
        }

        return stored;
    }

    /// <remarks>
    /// A stored selector carrying a key this service does not know is <b>not</b> ignored: the
    /// broadcast is dropped from the answer by <see cref="BroadcastAudience.Matches"/> never being
    /// consulted for it — <see cref="ParseAudience"/> cannot express "unknown", so the unknown key is
    /// mapped onto an impossible role instead. Publishing such a row is refused by this service, so
    /// the only way one exists is a direct write; showing it to everybody would be the wrong
    /// direction of failure for a targeted announcement.
    /// </remarks>
    private static BroadcastAudience ParseAudience(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return BroadcastAudience.Everyone;
        }

        var stored = JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
                     ?? new Dictionary<string, string?>(StringComparer.Ordinal);

        var known = stored.Keys.All(static key => key is "role" or "app");

        if (!known)
        {
            return new BroadcastAudience(UndecidableRole, null);
        }

        stored.TryGetValue("role", out var role);
        stored.TryGetValue("app", out var app);

        return new BroadcastAudience(
            string.IsNullOrWhiteSpace(role) ? null : role,
            string.IsNullOrWhiteSpace(app) ? null : app);
    }

    /// <summary>A role no token can hold, so a selector this service cannot evaluate matches nobody.</summary>
    /// <remarks>
    /// The leading <c>!</c> is what makes that true: the nine canonical roles are lower-case letters
    /// and underscores (AL-06), so no value <c>MageRideRoles</c> admits can collide with this one.
    /// </remarks>
    internal const string UndecidableRole = "!undecidable";
}
