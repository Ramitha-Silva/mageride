using Dapper;
using MageRide.Shared.Persistence;

namespace MageRide.PublicBff.Persistence;

/// <summary>One row of <c>safety.trip_share_tokens</c> (migration 0901).</summary>
public sealed record ShareToken(
    string Token,
    Guid? TripId,
    string Scope,
    Guid? LocationRequestId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastAccessAt,
    int AccessCount,
    DateTimeOffset CreatedAt)
{
    public bool IsLiveAt(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

/// <summary>
/// <c>safety.trip_share_tokens</c>, as the surface the token opens sees it.
/// </summary>
/// <remarks>
/// <b>Three verbs, and this service is entitled to all three.</b> The row is minted by
/// notification-svc (C051) and revoked on trip end by safety-svc (C052); what is left is the
/// redemption — read it, meter it (AL-44) and, for <c>pickup_confirm</c>, burn it (BR-29.1). The
/// burn belongs here because it happens when the token is <em>used</em>, and this is the only
/// component a `pickup_confirm` token is ever presented to.
/// </remarks>
public interface IShareTokenRepository
{
    /// <summary>The token is its own primary key — every lookup is by the value in the URL.</summary>
    Task<ShareToken?> FindAsync(string token, CancellationToken cancellationToken);

    /// <summary>
    /// Records one redemption (AL-44's <c>last_access_at</c> / <c>access_count</c>).
    /// </summary>
    /// <remarks>
    /// One <c>UPDATE … SET access_count = access_count + 1</c> rather than read-modify-write: a
    /// shared link is unauthenticated, the count is the only forensic trail there is, and two
    /// concurrent readers must not lose one between them.
    /// </remarks>
    Task MeterAsync(string token, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Burns a token, returning whether this call was the one that burned it.
    /// </summary>
    /// <remarks>
    /// Guarded on <c>revoked_at IS NULL</c>, so a double-tapped Share button burns once and the
    /// second press does not rewrite when the first happened — the timestamp is evidence.
    /// </remarks>
    Task<bool> BurnAsync(string token, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IShareTokenRepository"/>
internal sealed class ShareTokenRepository(INpgsqlConnectionFactory connections) : IShareTokenRepository
{
    private readonly INpgsqlConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private const string Columns =
        "token, trip_id, scope, location_request_id, expires_at, revoked_at, last_access_at, access_count, created_at";

    public async Task<ShareToken?> FindAsync(string token, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<ShareToken>(
            new CommandDefinition(
                $"SELECT {Columns} FROM safety.trip_share_tokens WHERE token = @Token;",
                new { Token = token },
                cancellationToken: cancellationToken));
    }

    public async Task MeterAsync(string token, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE safety.trip_share_tokens
                   SET access_count = access_count + 1, last_access_at = @At
                 WHERE token = @Token;
                """,
                new { Token = token, At = at },
                cancellationToken: cancellationToken));
    }

    public async Task<bool> BurnAsync(string token, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE safety.trip_share_tokens
                   SET revoked_at = @At
                 WHERE token = @Token AND revoked_at IS NULL;
                """,
                new { Token = token, At = at },
                cancellationToken: cancellationToken)) == 1;
    }
}
