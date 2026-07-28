using MageRide.Iam.Domain;
using MageRide.Iam.Persistence;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;

namespace MageRide.Iam.Rbac;

/// <summary>Everything <c>/v1/admin/rbac/users/{userId}</c> answers with.</summary>
public sealed record UserRoleGrants(
    Guid UserId,
    string PrimaryRole,
    IReadOnlyList<RoleGrant> Grants,
    IReadOnlyList<string> Roles,
    FleetMembership? Fleet,
    EffectivePermissionSet Permissions);

/// <summary>
/// The Super-Admin role-provisioning surface of URD §2.3's "User &amp; role management (RBAC)" row.
/// </summary>
/// <remarks>
/// The permission half of that row's wording — "define permissions" — is
/// <see cref="PermissionMatrix"/> and is not writable from anywhere; see the argument there.
/// What is writable is the grant, which is the "assign roles" half.
/// </remarks>
public interface IRoleAdminService
{
    Task<IReadOnlyList<RoleCatalogEntry>> CatalogAsync(CancellationToken cancellationToken);

    Task<UserRoleGrants> GetAsync(Guid userId, CancellationToken cancellationToken);

    Task<UserRoleGrants> GrantAsync(Guid actorId, Guid userId, string? role, CancellationToken cancellationToken);

    Task<UserRoleGrants> RevokeAsync(Guid actorId, Guid userId, string? role, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRoleAdminService"/>
public sealed class RoleAdminService(
    IUnitOfWorkFactory unitOfWorkFactory,
    INpgsqlConnectionFactory connections,
    IRoleGrantRepository grants,
    IUserRepository users,
    IProfileRepository profiles,
    IPolicyEvaluator policies) : IRoleAdminService
{
    public async Task<IReadOnlyList<RoleCatalogEntry>> CatalogAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await grants.CatalogAsync(connection, null, cancellationToken);
    }

    public async Task<UserRoleGrants> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await ReadAsync(connection, null, userId, cancellationToken);
    }

    public async Task<UserRoleGrants> GrantAsync(
        Guid actorId, Guid userId, string? role, CancellationToken cancellationToken)
    {
        var canonical = RequireRole(role);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        _ = await profiles.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, userId, cancellationToken)
            ?? throw NotFound(userId);

        await grants.GrantAsync(
            unitOfWork.Connection, unitOfWork.Transaction, userId, canonical, actorId, cancellationToken);

        var result = await ReadAsync(unitOfWork.Connection, unitOfWork.Transaction, userId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        // The new role reaches the account's own token at its next refresh, not now — C026's
        // rotation re-reads the principal (SessionService.RotateAsync). Revoking a live session
        // here would sign out an admin who was granted an *extra* role.
        return result;
    }

    public async Task<UserRoleGrants> RevokeAsync(
        Guid actorId, Guid userId, string? role, CancellationToken cancellationToken)
    {
        var canonical = RequireRole(role);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var profile = await profiles.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, userId, cancellationToken)
                      ?? throw NotFound(userId);

        // The union in IUserRepository.RolesAsync includes iam.users.role, so deleting the grant
        // row for a primary role changes nothing an evaluator can see. Answering 200 to a request
        // that did nothing is worse than refusing it: the console would show the role gone and
        // every service would keep honouring it.
        if (string.Equals(profile.Role, canonical, StringComparison.Ordinal))
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            throw new MageRideException(
                MageRideErrors.Conflict,
                $"'{canonical}' is this account's primary role and cannot be revoked as a grant. Change the " +
                "primary role first.");
        }

        // AL-06 makes Super Admin the only principal who can grant super_admin. A Super Admin who
        // revokes their own is not locked out of an account — they are locked out of the ability
        // to give it back, and so is everybody else if they were the last one.
        if (actorId == userId && string.Equals(canonical, MageRideRoles.SuperAdmin, StringComparison.Ordinal))
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            throw new MageRideException(
                MageRideErrors.Conflict,
                "A Super Admin cannot revoke their own super_admin role; ask another Super Admin (AL-06).");
        }

        if (!await grants.RevokeAsync(
                unitOfWork.Connection, unitOfWork.Transaction, userId, canonical, cancellationToken))
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            throw new MageRideException(MageRideErrors.NotFound, $"'{canonical}' is not granted to this account.");
        }

        var result = await ReadAsync(unitOfWork.Connection, unitOfWork.Transaction, userId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return result;
    }

    private async Task<UserRoleGrants> ReadAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction? transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var profile = await profiles.FindAsync(connection, transaction, userId, cancellationToken)
                      ?? throw NotFound(userId);

        var granted = await grants.GrantsAsync(connection, transaction, userId, cancellationToken);
        var principal = await users.PrincipalAsync(connection, transaction, userId, cancellationToken);

        return new UserRoleGrants(
            userId,
            profile.Role,
            granted,
            principal.Roles,
            principal.Fleet,
            policies.Evaluate(userId, principal.Roles, principal.Fleet));
    }

    private static string RequireRole(string? role)
    {
        if (!MageRideRoles.IsKnown(role))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["role"] = [$"role must be one of the nine canonical roles (AL-06): {string.Join(", ", MageRideRoles.All.Order(StringComparer.Ordinal))}."],
            });
        }

        return role!;
    }

    private static MageRideException NotFound(Guid userId) =>
        new(MageRideErrors.NotFound, $"No account '{userId}'.");
}
