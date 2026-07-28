using MageRide.Shared.Auth;

namespace MageRide.Iam.Rbac;

/// <summary>One feature area as a specific caller may use it.</summary>
/// <param name="Area">The URD §2.3 row.</param>
/// <param name="Grants">
/// Every capability the caller holds here, from any role. Carries
/// <see cref="PermissionGrant.OwnScope"/> when at least one of them is scope-limited.
/// </param>
/// <param name="ScopedGrants">
/// Exactly the capabilities that are available <em>only</em> within the caller's own records or
/// organisation — the ones <see cref="Qualifier"/> describes.
/// </param>
/// <param name="Symbol">
/// The result rendered back into legend form. Equal to the spec's own cell whenever one role is
/// doing all the work, which is the common case.
/// </param>
/// <param name="Qualifier">
/// The scope notes the scope-limited cells carry, deduplicated. <see langword="null"/> when
/// nothing is scope-limited.
/// </param>
public sealed record EffectivePermission(
    FeatureArea Area,
    PermissionGrant Grants,
    PermissionGrant ScopedGrants,
    string Symbol,
    string? Qualifier)
{
    public bool Satisfies(PermissionGrant needed) => needed != PermissionGrant.None && (Grants & needed) == needed;

    /// <summary>
    /// Whether the owning service must bound <paramref name="needed"/> to the caller's own records.
    /// </summary>
    /// <remarks>
    /// The question <see cref="PermissionGrant.OwnScope"/> exists to make askable. A caller who is
    /// both an Admin (👁 platform-wide) and a Fleet Owner (◐ own org) may read everything and
    /// write only their own organisation; a single "is this scoped" flag would answer that wrongly
    /// in one direction or the other, so the answer is per capability.
    /// </remarks>
    public bool RequiresOwnScope(PermissionGrant needed) => (ScopedGrants & needed) != PermissionGrant.None;
}

/// <summary>Everything the RBAC surface knows about one caller.</summary>
/// <param name="Roles">Every canonical role held. Permissions are their union (AL-06).</param>
/// <param name="Fleet">The org-scoped sub-role, when there is a fleet membership (AL-03).</param>
public sealed record EffectivePermissionSet(
    Guid UserId,
    IReadOnlyList<string> Roles,
    Domain.FleetMembership? Fleet,
    IReadOnlyList<EffectivePermission> Permissions)
{
    private readonly IReadOnlyDictionary<string, EffectivePermission> _byArea =
        Permissions.ToDictionary(static p => p.Area.Key, StringComparer.Ordinal);

    /// <summary>What this caller may do in <paramref name="area"/>. Never null — ➖ if nothing.</summary>
    public EffectivePermission For(FeatureArea area)
    {
        ArgumentNullException.ThrowIfNull(area);

        return _byArea.TryGetValue(area.Key, out var permission)
            ? permission
            : new EffectivePermission(
                area, PermissionGrant.None, PermissionGrant.None, PermissionCell.Symbols.None, null);
    }

    public bool Allows(FeatureArea area, PermissionGrant needed) => For(area).Satisfies(needed);
}

/// <summary>
/// Resolves a role set into the effective permissions of URD §2.3 (AL-06), narrowed by the
/// org-scoped fleet sub-role of URD §2.1 (AL-03).
/// </summary>
public interface IPolicyEvaluator
{
    EffectivePermissionSet Evaluate(Guid userId, IReadOnlyList<string> roles, Domain.FleetMembership? fleet);
}

/// <inheritdoc cref="IPolicyEvaluator"/>
/// <remarks>
/// <para>
/// <b>Union, not precedence.</b> URD §2.1: "A user may hold more than one role … Effective
/// permissions are the **union** of the user's roles, always bounded by the Feature Permission
/// Matrix." A driver who is also a fleet owner gets both columns; nothing is subtracted for
/// holding a second role, and the matrix is the only ceiling.
/// </para>
/// <para>
/// <b>The fleet sub-role narrows one column, not the answer.</b> URD §2.1 makes Owner / Manager /
/// Viewer "an org-scoped sub-model of the Fleet Owner role" — so the narrowing is applied to what
/// <c>fleet_owner</c> contributes and to nothing else. A Viewer who is also a Support CSR keeps
/// every CSR cell at full strength; narrowing the union instead would have the fleet sub-role
/// silently demote a platform role it has no business touching.
/// </para>
/// <para>
/// Pure and stateless — no I/O, no clock, no configuration. The role list comes from
/// <c>iam.user_roles</c> ∪ <c>iam.users.role</c> and the membership from
/// <c>iam.fleet_members</c>; both are read by <c>IUserRepository</c>.
/// </para>
/// </remarks>
public sealed class PolicyEvaluator : IPolicyEvaluator
{
    public EffectivePermissionSet Evaluate(Guid userId, IReadOnlyList<string> roles, Domain.FleetMembership? fleet)
    {
        ArgumentNullException.ThrowIfNull(roles);

        // Unknown role strings are dropped rather than trusted. A token minted before a role was
        // retired, or a hand-edited iam.user_roles row, must not reach the matrix as a key that
        // happens not to be there — that path denies, but silently, and the drop is the honest
        // version of the same outcome.
        var held = roles.Where(MageRideRoles.IsKnown).Distinct(StringComparer.Ordinal).ToArray();

        var permissions = new List<EffectivePermission>(FeatureAreas.All.Count);

        foreach (var area in FeatureAreas.All)
        {
            // Kept apart rather than OR-ed together, because OwnScope is a *restriction* and a
            // union of restrictions is not a restriction. A caller who holds the same capability
            // unscoped from one role and own-scoped from another holds it unscoped; folding the
            // flag in would tell the owning service to bound a read the caller may make platform
            // -wide, which is how an Admin who happens to own a fleet would end up seeing less
            // than an Admin.
            var unscoped = PermissionGrant.None;
            var scoped = PermissionGrant.None;
            var qualifiers = new List<string>();
            PermissionCell? soleSource = null;
            var sources = 0;

            foreach (var role in held)
            {
                var cell = PermissionMatrix.Cell(area, role);

                if (string.Equals(role, MageRideRoles.FleetOwner, StringComparison.Ordinal))
                {
                    cell = NarrowToFleetSubRole(area, cell, fleet?.FleetRole);
                }

                if (cell.Grants == PermissionGrant.None)
                {
                    continue;
                }

                sources++;
                soleSource = cell;

                var capabilities = cell.Grants & Capabilities;

                if (cell.Grants.HasFlag(PermissionGrant.OwnScope))
                {
                    scoped |= capabilities;

                    if (cell.Qualifier is { } qualifier && !qualifiers.Contains(qualifier, StringComparer.Ordinal))
                    {
                        qualifiers.Add(qualifier);
                    }
                }
                else
                {
                    unscoped |= capabilities;
                }
            }

            permissions.Add(Merge(area, unscoped, scoped, qualifiers, sources == 1 ? soleSource : null));
        }

        return new EffectivePermissionSet(userId, held, fleet, permissions);
    }

    /// <summary>The four verbs. <see cref="PermissionGrant.OwnScope"/> is not one of them.</summary>
    private const PermissionGrant Capabilities =
        PermissionGrant.Read | PermissionGrant.Write | PermissionGrant.Configure | PermissionGrant.Raise;

    /// <summary>
    /// Applies URD §2.1's fleet sub-model to a <c>fleet_owner</c> cell.
    /// </summary>
    /// <remarks>
    /// "Owner = full org control + billing; Manager = onboarding, assignment, scheduling,
    /// monitoring (**no billing**/owner changes); Viewer = **read-only** fleet map &amp; analytics."
    /// <para>
    /// A <c>fleet_owner</c> with no membership row keeps the plain column: the account holds the
    /// canonical role and the sub-model has nothing to say about it, which is the state a fleet
    /// owner is in between being granted the role and their organisation being created (AL-03).
    /// </para>
    /// </remarks>
    private static PermissionCell NarrowToFleetSubRole(FeatureArea area, PermissionCell cell, string? fleetRole) =>
        fleetRole switch
        {
            // Viewer is read-only across the whole sub-model, not only on the map.
            FleetRoles.Viewer => Restrict(cell, PermissionGrant.Read | PermissionGrant.OwnScope, "viewer"),

            // Manager loses billing and nothing else.
            FleetRoles.Manager when area == FeatureAreas.FleetBilling =>
                Restrict(cell, PermissionGrant.Read | PermissionGrant.OwnScope, "manager, no billing"),

            _ => cell,
        };

    private static PermissionCell Restrict(PermissionCell cell, PermissionGrant ceiling, string note)
    {
        var narrowed = cell.Grants & ceiling;

        if (narrowed == cell.Grants)
        {
            return cell;
        }

        var qualifier = cell.Qualifier is null ? note : $"{cell.Qualifier}, {note}";

        return narrowed == PermissionGrant.None
            ? PermissionCell.Denied
            : new PermissionCell(Render(narrowed, qualifier), narrowed, qualifier);
    }

    private static EffectivePermission Merge(
        FeatureArea area,
        PermissionGrant unscoped,
        PermissionGrant scoped,
        List<string> qualifiers,
        PermissionCell? soleSource)
    {
        // Only the capabilities that no role grants platform-wide are actually scope-limited.
        var scopedOnly = scoped & ~unscoped;
        var grants = unscoped | scoped | (scopedOnly == PermissionGrant.None
            ? PermissionGrant.None
            : PermissionGrant.OwnScope);

        if (grants == PermissionGrant.None)
        {
            return new EffectivePermission(area, grants, PermissionGrant.None, PermissionCell.Symbols.None, null);
        }

        var qualifier = scopedOnly != PermissionGrant.None && qualifiers.Count > 0
            ? string.Join(" · ", qualifiers)
            : null;

        // One role is doing all the work: hand back the spec's own cell, wording intact.
        if (soleSource is not null && soleSource.Grants == grants)
        {
            return new EffectivePermission(area, grants, scopedOnly, soleSource.Symbol, soleSource.Qualifier);
        }

        return new EffectivePermission(area, grants, scopedOnly, Render(grants, qualifier), qualifier);
    }

    /// <summary>Renders a grant set back into the URD §2.3 legend.</summary>
    private static string Render(PermissionGrant grants, string? qualifier)
    {
        var glyph = grants switch
        {
            PermissionGrant.None => PermissionCell.Symbols.None,
            _ when grants.HasFlag(PermissionGrant.OwnScope) => PermissionCell.Symbols.OwnScope,
            _ when grants.HasFlag(PermissionGrant.Write) => PermissionCell.Symbols.Full,
            _ when grants.HasFlag(PermissionGrant.Configure) => PermissionCell.Symbols.Configure,
            _ when grants.HasFlag(PermissionGrant.Read) => PermissionCell.Symbols.ReadOnly,
            _ => PermissionCell.Symbols.Raise,
        };

        return qualifier is null ? glyph : $"{glyph} {qualifier}";
    }
}
