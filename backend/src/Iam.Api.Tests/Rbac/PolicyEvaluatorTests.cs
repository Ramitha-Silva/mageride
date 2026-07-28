using MageRide.Iam.Domain;
using MageRide.Iam.Rbac;
using MageRide.Shared.Auth;

namespace MageRide.Iam.Tests.Rbac;

/// <summary>
/// DoD: "a user holding two roles gets the union of permissions" (URD §2.1, AL-06), plus the
/// org-scoped fleet sub-model of URD §2.1 (AL-03) and the deny-by-default fence.
/// </summary>
public sealed class PolicyEvaluatorTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly PolicyEvaluator Evaluator = new();

    [Fact]
    public void No_role_grants_nothing_anywhere()
    {
        var effective = Evaluator.Evaluate(User, [], null);

        Assert.All(effective.Permissions, permission => Assert.Equal(PermissionGrant.None, permission.Grants));
        Assert.All(FeatureAreas.All, area => Assert.False(effective.Allows(area, PermissionGrant.Read)));
    }

    [Fact]
    public void Two_roles_give_the_union_of_both_columns()
    {
        var driver = Evaluator.Evaluate(User, [MageRideRoles.Driver], null);
        var finance = Evaluator.Evaluate(User, [MageRideRoles.FinanceOfficer], null);
        var both = Evaluator.Evaluate(User, [MageRideRoles.Driver, MageRideRoles.FinanceOfficer], null);

        foreach (var area in FeatureAreas.All)
        {
            Assert.Equal(
                driver.For(area).Grants | finance.For(area).Grants,
                both.For(area).Grants);
        }
    }

    /// <summary>
    /// The union is strictly additive: no <em>capability</em> a role grants is taken away by
    /// holding a second.
    /// </summary>
    /// <remarks>
    /// Compared over the four verbs only. Losing <see cref="PermissionGrant.OwnScope"/> is the
    /// opposite of losing a grant — it means a second role authorises the same thing without the
    /// scope limit, which is a widening (see <c>Scope_is_tracked_per_capability_not_per_area</c>).
    /// </remarks>
    [Fact]
    public void Holding_a_second_role_never_removes_a_capability()
    {
        const PermissionGrant Capabilities =
            PermissionGrant.Read | PermissionGrant.Write | PermissionGrant.Configure | PermissionGrant.Raise;

        foreach (var first in MageRideRoles.All)
        {
            var alone = Evaluator.Evaluate(User, [first], null);

            foreach (var second in MageRideRoles.All.Where(r => !string.Equals(r, first, StringComparison.Ordinal)))
            {
                var together = Evaluator.Evaluate(User, [first, second], null);

                foreach (var area in FeatureAreas.All)
                {
                    var lost = alone.For(area).Grants & ~together.For(area).Grants & Capabilities;

                    Assert.True(
                        lost == PermissionGrant.None,
                        $"{first} lost {FeatureAreas.Describe(lost)} on {area.Key} by also holding {second}.");
                }
            }
        }
    }

    /// <summary>
    /// The companion to the rule above: a second role may only ever <em>lift</em> a scope limit,
    /// never impose one.
    /// </summary>
    [Fact]
    public void Holding_a_second_role_never_imposes_a_scope_limit()
    {
        foreach (var first in MageRideRoles.All)
        {
            var alone = Evaluator.Evaluate(User, [first], null);

            foreach (var second in MageRideRoles.All.Where(r => !string.Equals(r, first, StringComparison.Ordinal)))
            {
                var together = Evaluator.Evaluate(User, [first, second], null);

                foreach (var area in FeatureAreas.All)
                {
                    var newlyScoped = together.For(area).ScopedGrants & ~alone.For(area).ScopedGrants
                                      & alone.For(area).Grants;

                    Assert.True(
                        newlyScoped == PermissionGrant.None,
                        $"{first}'s {FeatureAreas.Describe(newlyScoped)} on {area.Key} became own-scoped by " +
                        $"also holding {second}.");
                }
            }
        }
    }

    /// <summary>
    /// The named example from URD §2.1 — "a Driver who is also a Fleet Owner" — spelled out rather
    /// than left to the algebraic test above.
    /// </summary>
    [Fact]
    public void A_driver_who_is_also_a_fleet_owner_gets_both()
    {
        var fleet = new FleetMembership(Guid.NewGuid(), FleetRoles.Owner);
        var effective = Evaluator.Evaluate(User, [MageRideRoles.Driver, MageRideRoles.FleetOwner], fleet);

        // From the driver column: ◐ own on the driver app.
        Assert.True(effective.Allows(FeatureAreas.DriverApp, PermissionGrant.Write));
        Assert.True(effective.For(FeatureAreas.DriverApp).Grants.HasFlag(PermissionGrant.OwnScope));

        // From the fleet_owner column: ◐ own org on fleet billing, which a driver alone has ➖ on.
        Assert.True(effective.Allows(FeatureAreas.FleetBilling, PermissionGrant.Write));
        Assert.Equal(
            PermissionGrant.None,
            Evaluator.Evaluate(User, [MageRideRoles.Driver], null).For(FeatureAreas.FleetBilling).Grants);

        // And nothing from a column they hold neither of.
        Assert.Equal(PermissionGrant.None, effective.For(FeatureAreas.RoleManagement).Grants);
    }

    [Fact]
    public void A_fleet_manager_keeps_operations_and_loses_billing()
    {
        var fleet = new FleetMembership(Guid.NewGuid(), FleetRoles.Manager);
        var effective = Evaluator.Evaluate(User, [MageRideRoles.FleetOwner], fleet);

        // URD §2.1: "Manager = onboarding, assignment, scheduling, monitoring (no billing…)".
        Assert.True(effective.Allows(FeatureAreas.FleetOperations, PermissionGrant.Write));
        Assert.True(effective.Allows(FeatureAreas.FleetMonitoring, PermissionGrant.Write));
        Assert.True(effective.Allows(FeatureAreas.FleetBilling, PermissionGrant.Read));
        Assert.False(effective.Allows(FeatureAreas.FleetBilling, PermissionGrant.Write));
    }

    [Fact]
    public void A_fleet_viewer_is_read_only_everywhere_the_fleet_role_reaches()
    {
        var fleet = new FleetMembership(Guid.NewGuid(), FleetRoles.Viewer);
        var effective = Evaluator.Evaluate(User, [MageRideRoles.FleetOwner], fleet);

        // URD §2.1: "Viewer = read-only fleet map & analytics".
        Assert.True(effective.Allows(FeatureAreas.FleetMonitoring, PermissionGrant.Read));

        foreach (var area in FeatureAreas.All)
        {
            var grants = effective.For(area).Grants;

            Assert.False(
                grants.HasFlag(PermissionGrant.Write) || grants.HasFlag(PermissionGrant.Configure),
                $"A fleet viewer holds {FeatureAreas.Describe(grants)} on {area.Key}.");
        }
    }

    /// <summary>
    /// The narrowing applies to the <c>fleet_owner</c> column and to nothing else — a Viewer who is
    /// also a Support CSR keeps every CSR cell at full strength.
    /// </summary>
    [Fact]
    public void The_fleet_sub_role_never_narrows_another_role()
    {
        var fleet = new FleetMembership(Guid.NewGuid(), FleetRoles.Viewer);

        var csrAlone = Evaluator.Evaluate(User, [MageRideRoles.SupportCsr], null);
        var both = Evaluator.Evaluate(User, [MageRideRoles.SupportCsr, MageRideRoles.FleetOwner], fleet);

        foreach (var area in FeatureAreas.All)
        {
            var lost = csrAlone.For(area).Grants & ~both.For(area).Grants;

            Assert.True(
                lost == PermissionGrant.None,
                $"A fleet viewer sub-role removed {FeatureAreas.Describe(lost)} from the CSR column on {area.Key}.");
        }

        // Support is ✅ for a CSR and ◐ own org for a fleet owner; the CSR's unscoped write wins.
        Assert.True(both.Allows(FeatureAreas.Support, PermissionGrant.Write));
    }

    [Fact]
    public void A_fleet_owner_without_a_membership_keeps_the_plain_column()
    {
        // Granted the role, organisation not created yet (AL-03).
        var effective = Evaluator.Evaluate(User, [MageRideRoles.FleetOwner], null);

        Assert.True(effective.Allows(FeatureAreas.FleetOperations, PermissionGrant.Write));
        Assert.True(effective.Allows(FeatureAreas.FleetBilling, PermissionGrant.Write));
    }

    [Fact]
    public void An_unknown_role_is_dropped_rather_than_trusted()
    {
        var effective = Evaluator.Evaluate(User, ["reseller", MageRideRoles.Passenger], null);

        Assert.Equal([MageRideRoles.Passenger], effective.Roles);
        Assert.True(effective.Allows(FeatureAreas.Passenger, PermissionGrant.Write));
    }

    [Fact]
    public void An_unmapped_feature_area_is_denied_to_a_super_admin()
    {
        var effective = Evaluator.Evaluate(User, [MageRideRoles.SuperAdmin], null);
        var invented = new FeatureArea("wallet-teleportation", "Not a URD §2.3 row");

        Assert.Equal(PermissionGrant.None, effective.For(invented).Grants);
        Assert.False(effective.Allows(invented, PermissionGrant.Read));
    }

    [Fact]
    public void Asking_for_nothing_is_never_an_allow()
    {
        var effective = Evaluator.Evaluate(User, [MageRideRoles.SuperAdmin], null);

        // A requirement of PermissionGrant.None would otherwise be satisfied by any cell,
        // including ➖ — an endpoint that forgot to name a capability must not be open.
        Assert.False(effective.Allows(FeatureAreas.RoleManagement, PermissionGrant.None));
        Assert.False(effective.Allows(FeatureAreas.Passenger, PermissionGrant.None));
    }

    [Fact]
    public void A_single_source_cell_keeps_the_spec_wording()
    {
        var effective = Evaluator.Evaluate(User, [MageRideRoles.FinanceOfficer], null);
        var refunds = effective.For(FeatureAreas.Refunds);

        Assert.Equal("✅ approve/execute", refunds.Symbol);
        Assert.Equal("approve/execute", refunds.Qualifier);
    }

    [Fact]
    public void An_unscoped_grant_supersedes_a_scoped_one()
    {
        // Support: CSR is ✅ (platform-wide) and fleet_owner is ◐ own org. Reporting "own org"
        // alongside an unscoped write would describe a narrower authority than the caller has.
        var fleet = new FleetMembership(Guid.NewGuid(), FleetRoles.Owner);
        var effective = Evaluator.Evaluate(User, [MageRideRoles.SupportCsr, MageRideRoles.FleetOwner], fleet);
        var support = effective.For(FeatureAreas.Support);

        Assert.True(support.Grants.HasFlag(PermissionGrant.Write));
        Assert.False(support.Grants.HasFlag(PermissionGrant.OwnScope));
        Assert.Equal(PermissionGrant.None, support.ScopedGrants);
        Assert.False(support.RequiresOwnScope(PermissionGrant.Write));
        Assert.Null(support.Qualifier);
    }

    /// <summary>
    /// The mixed case scope has to be tracked per capability for: Fleet operations is 👁 for an
    /// Admin and ◐ own org for a Fleet Owner, so somebody holding both reads every fleet and
    /// writes only their own.
    /// </summary>
    [Fact]
    public void Scope_is_tracked_per_capability_not_per_area()
    {
        var fleet = new FleetMembership(Guid.NewGuid(), FleetRoles.Owner);
        var effective = Evaluator.Evaluate(User, [MageRideRoles.Admin, MageRideRoles.FleetOwner], fleet);
        var operations = effective.For(FeatureAreas.FleetOperations);

        Assert.True(operations.Satisfies(PermissionGrant.Read));
        Assert.True(operations.Satisfies(PermissionGrant.Write));

        // The read came from the Admin column and needs no bounding; the write did not.
        Assert.False(operations.RequiresOwnScope(PermissionGrant.Read));
        Assert.True(operations.RequiresOwnScope(PermissionGrant.Write));
        Assert.Equal("own org", operations.Qualifier);
    }

    [Fact]
    public void A_lone_own_scope_grant_still_reports_its_scope()
    {
        var fleet = new FleetMembership(Guid.NewGuid(), FleetRoles.Owner);
        var operations = Evaluator.Evaluate(User, [MageRideRoles.FleetOwner], fleet).For(FeatureAreas.FleetOperations);

        Assert.True(operations.Grants.HasFlag(PermissionGrant.OwnScope));
        Assert.True(operations.RequiresOwnScope(PermissionGrant.Read));
        Assert.True(operations.RequiresOwnScope(PermissionGrant.Write));
        Assert.Equal("◐ own org", operations.Symbol);
    }
}
