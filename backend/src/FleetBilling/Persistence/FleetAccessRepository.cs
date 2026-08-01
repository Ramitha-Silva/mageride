using Dapper;
using MageRide.FleetBilling.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.FleetBilling.Persistence;

/// <summary>
/// <c>registry.fleets</c> and <c>iam.fleet_members</c>, read-only — what decides whether a caller
/// may see an organisation's money.
/// </summary>
/// <remarks>
/// <b>Both tables belong to fleet-svc (C058) and are never written here.</b> The alternative is a
/// synchronous hop to fleet-svc on every request of a billing screen, which is the same trade
/// subscription-svc's <c>ModeBRegistryRepository</c>, wallet-svc's <c>IsDriverAsync</c> and
/// registry-svc's own cross-context reads already make in both directions.
/// </remarks>
internal interface IFleetAccessRepository
{
    /// <summary>The organisation named in the path, or <see langword="null"/>.</summary>
    Task<FleetOrganisation?> FindAsync(Guid fleetId, CancellationToken cancellationToken);

    /// <summary>
    /// The caller's sub-role in <em>this</em> organisation, or <see langword="null"/> when they hold
    /// none.
    /// </summary>
    Task<string?> RoleForAsync(Guid fleetId, Guid userId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetAccessRepository"/>
internal sealed class FleetAccessRepository(INpgsqlConnectionFactory connections) : IFleetAccessRepository
{
    public async Task<FleetOrganisation?> FindAsync(Guid fleetId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<FleetOrganisation>(
            new CommandDefinition(
                """
                SELECT id, name, status, owner_id
                  FROM registry.fleets
                 WHERE id = @FleetId;
                """,
                new { FleetId = fleetId },
                cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// The organisation's registrant is treated as an Owner whether or not C058 also wrote them a
    /// membership row. It always does — the fleet and the owner's seat commit together — but the
    /// person who registered an organisation being locked out of its billing by a missing join row
    /// is not a failure mode worth preserving.
    /// </remarks>
    public async Task<string?> RoleForAsync(Guid fleetId, Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                """
                SELECT CASE
                         WHEN EXISTS (SELECT 1 FROM registry.fleets f
                                       WHERE f.id = @FleetId AND f.owner_id = @UserId)
                           THEN 'owner'
                         ELSE (SELECT m.fleet_role FROM iam.fleet_members m
                                WHERE m.fleet_id = @FleetId AND m.user_id = @UserId)
                       END;
                """,
                new { FleetId = fleetId, UserId = userId },
                cancellationToken: cancellationToken));
    }
}
