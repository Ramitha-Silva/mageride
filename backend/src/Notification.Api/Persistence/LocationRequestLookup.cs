using Dapper;
using MageRide.Shared.Persistence;

namespace MageRide.Notification.Persistence;

/// <summary>
/// Resolves the public <c>requestId</c> handle to the row a <c>pickup_confirm</c> token points at.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only reason this read exists is that 0606 keeps two identifiers.</b>
/// <c>rides.location_requests.request_id</c> is the public handle — what
/// <c>POST /v1/location-requests</c> answers with, what the SignalR group is named after, and what
/// travels on <c>location.request.issued</c> — while <c>id</c> is the surrogate that
/// <c>safety.trip_share_tokens.location_request_id</c> has its foreign key onto (0901). Minting
/// AL-45's token therefore needs the translation, and the event carries only one of the two.
/// </para>
/// <para>
/// Read-only, one column, keyed by a unique index — the same shape as ride-svc's own cross-context
/// read of <c>iam.users</c>. Raised in the C051 handoff as a candidate for
/// <c>location.request.issued</c> to carry both ids, which would delete this class.
/// </para>
/// </remarks>
public interface ILocationRequestLookup
{
    Task<Guid?> FindIdAsync(Guid requestId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILocationRequestLookup"/>
internal sealed class LocationRequestLookup(INpgsqlConnectionFactory connections) : ILocationRequestLookup
{
    private readonly INpgsqlConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    public async Task<Guid?> FindIdAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                "SELECT id FROM rides.location_requests WHERE request_id = @RequestId;",
                new { RequestId = requestId },
                cancellationToken: cancellationToken));
    }
}
