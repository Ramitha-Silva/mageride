using System.Net;
using MageRide.Fleet.Endpoints;
using MageRide.Fleet.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Fleet.Tests.Integration;

/// <summary>
/// US-13.A7 — the organisation, and the Verification-Officer gate in front of everything it does.
/// </summary>
[Collection<FleetCollection>]
public sealed class OrganisationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Registering_an_organisation_leaves_it_pending_and_seats_its_owner()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var (ownerId, bearer) = await harness.CreateFleetOwnerAsync();

        using var response = await harness.PostAsync(
            "/v1/fleets",
            new
            {
                name = "Ruhunu Express",
                registrationNo = "PV-102938",
                contactPhone = "+94771234567",
                contactEmail = "ops@ruhunu.lk",
                address = "42 Galle Road, Colombo 03",
            },
            bearer);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var fleet = await FleetHarness.OkAsync<FleetResponse>(response, "POST /v1/fleets");

        Assert.Equal("PENDING", fleet.Status);
        Assert.Equal("Ruhunu Express", fleet.Name);
        Assert.Equal("PV-102938", fleet.RegistrationNo);
        Assert.Equal("+94771234567", fleet.ContactPhone);

        // The seat, not the owner_id column. Every route on this service resolves the caller from
        // iam.fleet_members, so an organisation whose registrant has no row there is one nobody can
        // open — including the person who just created it.
        await using var connection = await harness.OpenAsync();

        var seat = await Dapper.SqlMapper.ExecuteScalarAsync<string?>(
            connection,
            "SELECT fleet_role FROM iam.fleet_members WHERE fleet_id = @FleetId AND user_id = @UserId;",
            new { FleetId = Guid.Parse(fleet.FleetId), UserId = ownerId });

        Assert.Equal(FleetRoles.Owner, seat);

        // And the registrant can immediately read it back with a token carrying the claim pair
        // iam-svc would mint on their next sign-in.
        var read = await harness.GetAsync<FleetResponse>(
            $"/v1/fleets/{fleet.FleetId}", harness.Tokens.FleetMember(ownerId, Guid.Parse(fleet.FleetId), FleetRoles.Owner));

        Assert.Equal(fleet.FleetId, read.FleetId);
        Assert.Equal("PENDING", read.Status);
    }

    [Fact]
    public async Task A_second_organisation_cannot_claim_a_live_business_registration()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        await harness.CreateFleetAsync(businessReg: "PV-DUPLICATE");

        var (_, bearer) = await harness.CreateFleetOwnerAsync();

        using var response = await harness.PostAsync(
            "/v1/fleets",
            new { name = "Impostor Transit", registrationNo = "pv-duplicate", contactPhone = "+94771234567" },
            bearer);

        var problem = await FleetHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("business-registration-exists", problem.Code);
    }

    [Fact]
    public async Task Registration_validates_the_fields_the_contract_constrains()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var (_, bearer) = await harness.CreateFleetOwnerAsync();

        // A landline, which _shared.yaml's PhoneE164 does not admit, and no business registration.
        using var response = await harness.PostAsync(
            "/v1/fleets", new { name = "Nameless", contactPhone = "+94112345678" }, bearer);

        var problem = await FleetHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("validation-failed", problem.Code);
        Assert.Contains("registrationNo", problem.Body, StringComparison.Ordinal);
        Assert.Contains("contactPhone", problem.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_passenger_cannot_register_an_organisation()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var passengerId = await harness.CreateUserAsync("passenger");

        using var response = await harness.PostAsync(
            "/v1/fleets",
            new { name = "Not A Fleet", registrationNo = "PV-000", contactPhone = "+94771234567" },
            harness.Tokens.Passenger(passengerId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A registration submitted twice under one key is one organisation (R-14).
    /// </summary>
    /// <remarks>
    /// The case that costs something: without replay the second submission reaches migration
    /// 0313's business-registration index and comes back 409, so a double-tapped Submit tells an
    /// operator their own application is a duplicate of itself.
    /// </remarks>
    [Fact]
    public async Task A_replayed_registration_creates_one_organisation()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var (_, bearer) = await harness.CreateFleetOwnerAsync();
        var key = Guid.NewGuid().ToString();

        var body = new
        {
            name = "Double Tap Transit",
            registrationNo = "PV-REPLAY",
            contactPhone = "+94771234567",
        };

        using var first = await harness.PostWithKeyAsync("/v1/fleets", body, bearer, key);
        using var second = await harness.PostWithKeyAsync("/v1/fleets", body, bearer, key);

        var one = await FleetHarness.OkAsync<FleetResponse>(first, "first POST /v1/fleets");
        var two = await FleetHarness.OkAsync<FleetResponse>(second, "replayed POST /v1/fleets");

        Assert.Equal(one.FleetId, two.FleetId);

        await using var connection = await harness.OpenAsync();

        var count = await Dapper.SqlMapper.ExecuteScalarAsync<int>(
            connection, "SELECT count(*)::int FROM registry.fleets WHERE business_reg = 'PV-REPLAY';");

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task An_officer_approves_the_organisation_and_a_rejection_carries_its_reason()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        // The queue admin-bff pages (AL-39), before any decision.
        var queue = await harness.InternalAsync<FleetQueueResponse>(
            HttpMethod.Get, "/v1/internal/fleets/queue");

        Assert.Contains(queue.Items, row => row.FleetId == fleet.FleetId.ToString() && row.Status == "PENDING");

        await harness.ApproveAsync(fleet.FleetId);

        var approved = await harness.GetAsync<FleetResponse>($"/v1/fleets/{fleet.FleetId}", fleet.OwnerBearer);
        Assert.Equal("APPROVED", approved.Status);
        Assert.Null(approved.RejectionReason);

        // A rejection on a second organisation, so the reason can be asserted without unwinding
        // the approval above.
        var other = await harness.CreateFleetAsync();
        var officerId = await harness.CreateUserAsync("verification_officer");

        var decision = await harness.InternalAsync<VerificationDecisionResponse>(
            HttpMethod.Post,
            $"/v1/internal/fleets/{other.FleetId}/reject",
            new { officerId = officerId.ToString(), reason = "The authorised-person ID does not match the business registration." });

        Assert.Equal("REJECTED", decision.Fleet.Status);
        Assert.Equal(
            "The authorised-person ID does not match the business registration.", decision.Fleet.RejectionReason);
    }

    [Fact]
    public async Task A_rejection_without_a_reason_is_refused()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        var officerId = await harness.CreateUserAsync("verification_officer");

        using var response = await harness.InternalAsync(
            HttpMethod.Post, $"/v1/internal/fleets/{fleet.FleetId}/reject", new { officerId = officerId.ToString() });

        var problem = await FleetHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("validation-failed", problem.Code);
    }

    [Fact]
    public async Task The_internal_plane_refuses_a_caller_without_the_key()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        using var noKey = await harness.InternalAsync(
            HttpMethod.Get, $"/v1/internal/fleets/{fleet.FleetId}", apiKey: null);
        using var wrongKey = await harness.InternalAsync(
            HttpMethod.Get, $"/v1/internal/fleets/{fleet.FleetId}", apiKey: "not-the-key");

        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
    }

    /// <summary>
    /// Without a key the family is not mapped at all.
    /// </summary>
    /// <remarks>
    /// The posture registry-svc, ride-svc and support-svc take: a deployment that forgets the
    /// secret gets no route rather than an open door. What is behind it is the write that decides
    /// where every Mode B rupee is sent.
    /// <para>
    /// Asserted on the endpoint data source rather than on a status code, because the kernel's
    /// deny-by-default fallback policy answers an unrouted path <c>401</c> before routing can
    /// answer <c>404</c> — so a status assertion would pass for the wrong reason on the day
    /// somebody mapped the routes without the filter.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Without_a_configured_key_the_internal_plane_does_not_exist()
    {
        await using var harness = await FleetHarness.StartAsync(postgres, withInternalPlane: false);

        var fleet = await harness.CreateFleetAsync();

        var mapped = harness.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint =>
                endpoint.RoutePattern.RawText?.StartsWith("/v1/internal/", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Empty(mapped);

        // And presenting the key the *other* harness uses does not reach anything either.
        using var response = await harness.InternalAsync(
            HttpMethod.Get, $"/v1/internal/fleets/{fleet.FleetId}", apiKey: FleetHarness.InternalApiKey);

        Assert.False(response.IsSuccessStatusCode);
    }
}
