using System.Net;
using MageRide.Fleet.Endpoints;
using MageRide.Fleet.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.Fleet.Tests.Integration;

/// <summary>
/// US-13.A5 — Owner / Manager / Viewer, and the definition-of-done claim that a Viewer cannot
/// mutate anything and a Manager cannot change the payout profile.
/// </summary>
[Collection<FleetCollection>]
public sealed class SubRoleTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_owner_provisions_a_manager_and_a_viewer_who_hold_the_canonical_role()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        var manager = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/members",
            new { email = "Ops.Manager@Ruhunu.LK", name = "Ops Manager", fleetRole = "manager" },
            fleet.OwnerBearer);

        var provisioned = await FleetHarness.OkAsync<FleetMemberResponse>(manager, "POST members");
        manager.Dispose();

        Assert.Equal("manager", provisioned.FleetRole);
        // Normalised, because iam.users.email is a UNIQUE sign-in credential (AL-07) and two
        // spellings of one address would be two accounts nobody can tell apart.
        Assert.Equal("ops.manager@ruhunu.lk", provisioned.Email);

        using var viewer = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/members",
            new { email = "desk@ruhunu.lk", fleetRole = "viewer" },
            fleet.OwnerBearer);

        Assert.Equal(HttpStatusCode.Created, viewer.StatusCode);

        // URD §2.1 makes the sub-roles "an org-scoped sub-model of the Fleet Owner role", and
        // C027's PolicyEvaluator narrows the fleet_owner column and only that one. A sub-user
        // without the canonical grant would be narrowed from an empty cell and hold nothing.
        await using var connection = await harness.OpenAsync();

        var grants = await Dapper.SqlMapper.QueryAsync<string>(
            connection,
            """
            SELECT u.email FROM iam.user_roles r
              JOIN iam.users u ON u.id = r.user_id
             WHERE r.role = 'fleet_owner' AND u.email IN ('ops.manager@ruhunu.lk','desk@ruhunu.lk');
            """);

        Assert.Equal(2, grants.Count());

        var members = await harness.GetAsync<FleetMembersResponse>(
            $"/v1/fleets/{fleet.FleetId}/members", fleet.OwnerBearer);

        // Owner first, then manager, then viewer — the list renders as an org chart.
        Assert.Equal(
            [FleetRoles.Owner, FleetRoles.Manager, FleetRoles.Viewer],
            members.Items.Select(item => item.FleetRole).ToArray());
    }

    [Fact]
    public async Task A_second_owner_cannot_be_provisioned_through_this_route()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        using var response = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/members",
            new { email = "coowner@ruhunu.lk", fleetRole = "owner" },
            fleet.OwnerBearer);

        var problem = await FleetHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("validation-failed", problem.Code);
    }

    [Fact]
    public async Task Provisioning_the_same_person_twice_is_a_conflict_not_a_silent_promotion()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        using var first = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/members",
            new { email = "desk@ruhunu.lk", fleetRole = "viewer" },
            fleet.OwnerBearer);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // A different sub-role and a fresh key, so this is a genuine second request rather than a
        // replay: promoting a Viewer to Manager is a decision, not a side effect of re-submitting.
        using var second = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/members",
            new { email = "desk@ruhunu.lk", fleetRole = "manager" },
            fleet.OwnerBearer);

        var problem = await FleetHarness.ProblemAsync(second);

        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("fleet-member-exists", problem.Code);
    }

    /// <summary>Definition of done: "a Viewer cannot mutate anything".</summary>
    [Fact]
    public async Task A_viewer_cannot_mutate_anything()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var (viewerId, viewerBearer) = await SeatAsync(harness, fleet, FleetRoles.Viewer);
        var vehicleId = await harness.AddVehicleAsync(fleet.FleetId, fleet.OwnerId);

        // Reading is what a Viewer is for, and it works.
        var read = await harness.GetAsync<FleetResponse>($"/v1/fleets/{fleet.FleetId}", viewerBearer);
        Assert.Equal(fleet.FleetId.ToString(), read.FleetId);

        using var members = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/members",
            new { email = "another@ruhunu.lk", fleetRole = "viewer" },
            viewerBearer);

        using var payout = await harness.PutAsync(
            $"/v1/fleets/{fleet.FleetId}/payout-profile",
            new { bank = "BOC", branch = "Nugegoda", accountNo = "1", accountHolderName = "Viewer" },
            viewerBearer);

        using var classification = await harness.PutAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{vehicleId}/classification",
            new { modeBBilling = "free" },
            viewerBearer);

        using var document = await harness.UploadPayoutDocumentAsync(
            fleet.FleetId, viewerBearer, "bank_statement", [1, 2, 3, 4]);

        foreach (var refused in new[] { members, payout, classification, document })
        {
            var problem = await FleetHarness.ProblemAsync(refused);

            Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
            Assert.Equal("fleet-role-insufficient", problem.Code);
        }

        Assert.NotEqual(Guid.Empty, viewerId);
    }

    /// <summary>Definition of done: "a Manager cannot change the payout profile".</summary>
    /// <remarks>
    /// US-13.A5: "Manager = onboarding/assignment/scheduling/monitoring (<b>no billing</b>/owner
    /// changes)", and C027's <c>PolicyEvaluator</c> narrows a Manager out of <c>fleet-billing</c>
    /// for the same reason. The account the organisation's money arrives in is the most owner-ish
    /// thing on the portal.
    /// </remarks>
    [Fact]
    public async Task A_manager_runs_the_fleet_but_cannot_touch_the_payout_profile()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var (_, managerBearer) = await SeatAsync(harness, fleet, FleetRoles.Manager);
        var vehicleId = await harness.AddVehicleAsync(fleet.FleetId, fleet.OwnerId);

        // The Manager's own job: Service payment is onboarding, not billing.
        var classified = await harness.PutAsync<FleetVehicleResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{vehicleId}/classification",
            new { modeBBilling = "free" },
            managerBearer);

        Assert.Equal("free", classified.ModeBBilling);

        using var read = await harness.GetAsync($"/v1/fleets/{fleet.FleetId}/payout-profile", managerBearer);
        using var write = await harness.PutAsync(
            $"/v1/fleets/{fleet.FleetId}/payout-profile",
            new { bank = "BOC", branch = "Nugegoda", accountNo = "9", accountHolderName = "Manager" },
            managerBearer);
        using var upload = await harness.UploadPayoutDocumentAsync(
            fleet.FleetId, managerBearer, "lankaqr_code", [9, 9, 9]);

        foreach (var refused in new[] { read, write, upload })
        {
            var problem = await FleetHarness.ProblemAsync(refused);

            Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
            Assert.Equal("fleet-role-insufficient", problem.Code);
        }
    }

    /// <summary>
    /// The token's <c>fleet_role</c> claim is not the authority — the membership row is.
    /// </summary>
    /// <remarks>
    /// A person may belong to several organisations and iam-svc puts the most privileged pair in
    /// the token (C027). An Owner of one fleet arriving at another fleet's path with that token
    /// must be refused, or the claim would be a privilege over every organisation on the platform.
    /// </remarks>
    [Fact]
    public async Task An_owner_of_one_organisation_is_a_stranger_at_another()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var mine = await harness.CreateFleetAsync();
        var theirs = await harness.CreateFleetAsync();

        // The token says "owner of `mine`" — genuinely true — and the path names `theirs`.
        using var response = await harness.GetAsync($"/v1/fleets/{theirs.FleetId}", mine.OwnerBearer);

        var problem = await FleetHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
        Assert.Equal("not-fleet-member", problem.Code);
    }

    /// <summary>
    /// A forged claim buys nothing: the seat is read from <c>iam.fleet_members</c> every time.
    /// </summary>
    [Fact]
    public async Task A_token_claiming_a_sub_role_the_person_does_not_hold_is_refused()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        var (viewerId, _) = await SeatAsync(harness, fleet, FleetRoles.Viewer);

        // The same person, with a token that says `owner` of the same organisation.
        var overclaimed = harness.Tokens.FleetMember(viewerId, fleet.FleetId, FleetRoles.Owner);

        using var response = await harness.PutAsync(
            $"/v1/fleets/{fleet.FleetId}/payout-profile",
            new { bank = "BOC", branch = "Nugegoda", accountNo = "1", accountHolderName = "Viewer" },
            overclaimed);

        var problem = await FleetHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
        Assert.Equal("fleet-role-insufficient", problem.Code);
    }

    [Fact]
    public async Task An_unknown_organisation_is_not_found_rather_than_forbidden()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var (userId, _) = await harness.CreateFleetOwnerAsync();
        var unknown = Guid.CreateVersion7();

        using var response = await harness.GetAsync(
            $"/v1/fleets/{unknown}", harness.Tokens.FleetMember(userId, unknown, FleetRoles.Owner));

        var problem = await FleetHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, problem.Status);
        Assert.Equal("fleet-not-found", problem.Code);
    }

    /// <summary>Provisions a sub-user through the real route and returns a bearer for them.</summary>
    private static async Task<(Guid UserId, string Bearer)> SeatAsync(
        FleetHarness harness, SeededFleet fleet, string fleetRole)
    {
        var email = $"{fleetRole}-{Guid.NewGuid():N}@ruhunu.lk";

        using var response = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/members",
            new { email, fleetRole },
            fleet.OwnerBearer);

        var member = await FleetHarness.OkAsync<FleetMemberResponse>(response, $"POST members ({fleetRole})");
        var userId = Guid.Parse(member.MemberId);

        return (userId, harness.Tokens.FleetMember(userId, fleet.FleetId, fleetRole));
    }
}
