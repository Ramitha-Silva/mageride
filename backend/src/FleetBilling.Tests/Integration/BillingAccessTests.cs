using System.Net;
using MageRide.FleetBilling.Authorization;
using MageRide.FleetBilling.Endpoints;
using MageRide.FleetBilling.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using Microsoft.AspNetCore.Routing;

namespace MageRide.FleetBilling.Tests.Integration;

/// <summary>
/// Who may see an organisation's money (US-13.A5, US-13.A7).
/// </summary>
[Collection<FleetBillingCollection>]
public sealed class BillingAccessTests(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    /// <summary>
    /// US-13.A5, verbatim: "Manager = onboarding/assignment/scheduling/monitoring (<b>no
    /// billing</b>/owner changes)". Reads included — there is no billing read a Manager is entitled
    /// to, which is why this service gates its GETs where fleet-svc leaves the map and the analytics
    /// open.
    /// </summary>
    [Theory]
    [InlineData("manager")]
    [InlineData("viewer")]
    public async Task Only_the_owner_reaches_billing(string fleetRole)
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        var (_, bearer) = await harness.Seed.AddMemberAsync(fleet.Id, fleetRole);

        foreach (var path in new[]
        {
            $"/v1/fleets/{fleet.Id}/billing",
            $"/v1/fleets/{fleet.Id}/wallet",
        })
        {
            using var response = await harness.GetAsync(path, bearer);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            var (code, _) = await FleetBillingHarness.ProblemAsync(response);
            Assert.Equal("fleet-role-insufficient", code);
        }

        using var topup = await harness.PostAsync(
            $"/v1/fleets/{fleet.Id}/wallet/topup", new { amountMinor = 100_000, method = "lankaqr" }, bearer);

        Assert.Equal(HttpStatusCode.Forbidden, topup.StatusCode);
    }

    /// <summary>
    /// The token's <c>fleet_role</c> claim is never the authority. iam-svc puts the caller's most
    /// privileged membership in it (C027), so an Owner of one organisation arrives at another
    /// carrying <c>fleet_role=owner</c> — and is refused on the membership row.
    /// </summary>
    [Fact]
    public async Task An_owner_of_another_organisation_is_not_a_member_of_this_one()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var mine = await harness.Seed.CreateFleetAsync();
        var theirs = await harness.Seed.CreateFleetAsync();

        // `theirs.Bearer` carries fleet_role=owner and fleet_id=theirs.
        using var response = await harness.GetAsync($"/v1/fleets/{mine.Id}/billing", theirs.Bearer);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var (code, _) = await FleetBillingHarness.ProblemAsync(response);
        Assert.Equal("not-fleet-member", code);
    }

    /// <summary>A fleet id is a UUID nobody guesses, so "no such organisation" leaks nothing.</summary>
    [Fact]
    public async Task An_unknown_organisation_is_a_404()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();

        using var response = await harness.GetAsync($"/v1/fleets/{Guid.NewGuid()}/billing", fleet.Bearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var (code, _) = await FleetBillingHarness.ProblemAsync(response);
        Assert.Equal("fleet-not-found", code);
    }

    /// <summary>
    /// US-13.A7. A PENDING organisation has no approved vehicles, so it has no charges and no
    /// invoice — every route here would answer an empty page, and an empty page is a worse answer
    /// than "your organisation is still being reviewed".
    /// </summary>
    [Fact]
    public async Task A_pending_organisation_is_told_it_is_pending()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync(status: "PENDING");

        using var response = await harness.GetAsync($"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var (code, _) = await FleetBillingHarness.ProblemAsync(response);
        Assert.Equal("fleet-not-approved", code);
    }

    /// <summary>A bearer with no fleet membership at all reaches nothing.</summary>
    [Fact]
    public async Task A_driver_bearer_reaches_nothing_here()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();

        using var response = await harness.GetAsync(
            $"/v1/fleets/{fleet.Id}/billing", harness.Tokens.Driver(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>And no bearer at all is a 401, from the kernel's deny-by-default fallback.</summary>
    [Fact]
    public async Task An_anonymous_request_is_refused_before_the_filter_runs()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();

        using var response = await harness.GetAsync($"/v1/fleets/{fleet.Id}/billing");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The guarantee for whoever adds the next route: <b>every</b> endpoint under
    /// <c>/v1/fleets/{fleetId}</c> in this service refuses a Manager and refuses a non-member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walked off the endpoint data source and driven, rather than asserted per route by hand, so a
    /// route mapped outside the group carrying <see cref="FleetBillingAccessFilter"/> fails
    /// <em>this</em> test rather than shipping unscoped. fleet-svc's
    /// <c>Every_vehicle_and_assignment_route_is_gated</c>, one service over — and driven rather
    /// than inspected because an endpoint filter added to a group leaves no metadata to look for.
    /// </para>
    /// <para>
    /// The assertion is "not a success", not "exactly 403": a route may legitimately answer 404 for
    /// a path parameter this test invents, and what matters is that a caller without the Owner
    /// sub-role never gets an answer about the organisation's money.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task No_fleet_scoped_route_answers_a_manager_or_a_non_member()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        var (_, manager) = await harness.Seed.AddMemberAsync(fleet.Id, "manager");
        var stranger = await harness.Seed.CreateFleetAsync();

        var routes = harness.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/v1/fleets/{fleetId}", StringComparison.Ordinal) == true)
            .Select(endpoint => (
                Method: endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()
                    ?.HttpMethods.FirstOrDefault() ?? "GET",
                Template: endpoint.RoutePattern.RawText!))
            .Distinct()
            .ToArray();

        // A silent zero here would make everything below vacuously true.
        Assert.True(routes.Length >= 8, $"expected the whole billing surface; found {routes.Length} routes.");

        var leaks = new List<string>();

        foreach (var (method, template) in routes)
        {
            var path = template
                .Replace("{fleetId}", fleet.Id.ToString(), StringComparison.Ordinal)
                .Replace("{invoiceId}", Guid.NewGuid().ToString(), StringComparison.Ordinal)
                .Replace("{topupId}", Guid.NewGuid().ToString(), StringComparison.Ordinal);

            foreach (var (who, bearer) in new[] { ("manager", manager), ("non-member", stranger.Bearer) })
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
                request.Headers.TryAddWithoutValidation(
                    MageRide.Shared.Http.MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

                using var response = await harness.Client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    leaks.Add($"{method,-4} {template} answered {(int)response.StatusCode} to a {who}");
                }
            }
        }

        Assert.True(leaks.Count == 0, string.Join(Environment.NewLine, leaks));
    }

    /// <summary>
    /// The internal plane answers 404 without its key, matching what the gateway does for the
    /// <c>/v1/internal</c> prefix: a caller who is not entitled to it should not be able to map it.
    /// </summary>
    [Fact]
    public async Task The_internal_run_route_is_unmappable_without_its_key()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        using var without = await harness.PostAsync("/v1/internal/fleet-billing/run");
        Assert.Equal(HttpStatusCode.NotFound, without.StatusCode);

        using var wrong = await harness.PostAsync(
            "/v1/internal/fleet-billing/run", internalKey: "not-the-key");
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);

        using var right = await harness.PostAsync(
            "/v1/internal/fleet-billing/run", internalKey: FleetBillingHarness.InternalApiKey);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    /// <summary>The internal route does what the runner does, on a month a caller names.</summary>
    [Fact]
    public async Task The_internal_run_route_invoices_a_month_that_was_missed()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        var june = new DateOnly(2026, 6, 1);
        await harness.Seed.RaiseModeBChargesAsync(june);
        await harness.Seed.CreditAsync(fleet.Id, 100_000);

        using var response = await harness.PostAsync(
            "/v1/internal/fleet-billing/run?periodMonth=2026-06",
            internalKey: FleetBillingHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<BillingRunResponse>(
            MageRide.Shared.Http.MageRideJson.Options);

        Assert.Equal(june, result!.PeriodMonth);
        Assert.Equal(1, result.InvoicesRaised);
        Assert.Equal(1, result.LinesAdded);
        Assert.Equal(1, result.Settled);

        var page = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        var invoice = Assert.Single(page.Items);

        Assert.Equal(june, invoice.PeriodMonth);
        Assert.Equal("PAID", invoice.Status);
    }
}
