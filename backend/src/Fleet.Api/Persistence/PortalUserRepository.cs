using Dapper;
using MageRide.Shared.Auth;
using Npgsql;

namespace MageRide.Fleet.Persistence;

/// <summary>The account a provisioned sub-user signs in with, and whether this call created it.</summary>
public sealed record PortalUser(Guid Id, string Email, bool WasCreated);

/// <summary>
/// The <c>iam.users</c> / <c>iam.user_roles</c> half of provisioning a sub-user (US-13.A5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a sub-user holds the canonical <c>fleet_owner</c> role.</b> URD §2.1 makes Owner /
/// Manager / Viewer "an org-scoped sub-model of the Fleet Owner role", and C027's
/// <c>PolicyEvaluator</c> implements exactly that: the sub-role narrows what the
/// <c>fleet_owner</c> column contributes and nothing else. A Viewer who held no canonical role
/// would be narrowed from an empty cell and end up with no permissions at all — the deny-by-default
/// matrix has no other column for them.
/// </para>
/// <para>
/// <b>No credential is set here.</b> AL-07 gives the Fleet Portal Email+Password, Google and Apple,
/// and all three are iam-svc's (C026): <c>iam.user_credentials</c> and
/// <c>iam.federated_identities</c> are written by the sign-in and link flows, not by an invitation.
/// A person provisioned here exists and can be granted a seat; how they prove who they are is the
/// other service's question. What is <b>missing</b> is the invitation itself — there is no
/// "you have been added to X" template in <c>content.notification_templates</c> and no fleet-org
/// notification anywhere in the seed (migration 1904), so the owner has to tell them out of band.
/// Raised in the C058 handoff.
/// </para>
/// </remarks>
public interface IPortalUserRepository
{
    /// <summary>
    /// Finds the account for <paramref name="email"/> or creates one, and makes sure it holds the
    /// canonical <c>fleet_owner</c> grant the sub-model narrows.
    /// </summary>
    Task<PortalUser> EnsureFleetPortalUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string email,
        string? name,
        Guid grantedBy,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPortalUserRepository"/>
internal sealed class PortalUserRepository : IPortalUserRepository
{
    public async Task<PortalUser> EnsureFleetPortalUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string email,
        string? name,
        Guid grantedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var normalised = email.Trim().ToLowerInvariant();

        // `iam.users.email` is UNIQUE, so the insert is the lookup: ON CONFLICT DO NOTHING and
        // RETURNING gives no row when the account already exists, which is what tells the two
        // cases apart without a read-then-write race.
        //
        // `role` is set to fleet_owner only on creation. An existing driver or passenger keeps
        // their primary role — AL-06 makes effective permissions the *union* of iam.user_roles, so
        // the grant below is what matters, and overwriting `users.role` would demote somebody's
        // account because they were handed a Viewer seat.
        var created = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            INSERT INTO iam.users (email, role, first_name)
            VALUES (@Email, 'fleet_owner', @Name)
            ON CONFLICT (email) DO NOTHING
            RETURNING id;
            """,
            new { Email = normalised, Name = name },
            transaction,
            cancellationToken: cancellationToken));

        var userId = created ?? await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT id FROM iam.users WHERE email = @Email;",
            new { Email = normalised },
            transaction,
            cancellationToken: cancellationToken));

        // The union grant. Idempotent, and `granted_by` records the Fleet Owner who did it —
        // US-13.A5's exemption from "internal roles are provisioned only by Super Admin" is
        // exactly the fact worth being able to read back later.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO iam.user_roles (user_id, role, granted_by)
            VALUES (@UserId, @Role, @GrantedBy)
            ON CONFLICT (user_id, role) DO NOTHING;
            """,
            new { UserId = userId, Role = MageRideRoles.FleetOwner, GrantedBy = grantedBy },
            transaction,
            cancellationToken: cancellationToken));

        return new PortalUser(userId, normalised, created is not null);
    }
}
