using System.Net;
using MageRide.Shared.Primitives;
using MageRide.Support.Endpoints;
using MageRide.Support.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Support.Tests.Integration;

/// <summary>
/// US-16.3 / US-14.13 — the agent queue admin-bff forwards, and US-9.23's Finance routing.
/// </summary>
[Collection<SupportCollection>]
public sealed class QueueTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Every_status_transition_reaches_the_users_thread()
    {
        // The third definition of done: "ticket status transitions are recorded and visible to the
        // user in the thread". The whole agent sequence runs through the internal plane and the
        // user's own detail is read back — two different code paths over one table.
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (userId, bearer) = await harness.CreatePassengerAsync();
        var agentId = await harness.CreateUserAsync("support_csr");

        using var created = await harness.PostAsync(
            "/v1/support/tickets", new { category = "booking", description = "Driver took a longer route." }, bearer);

        var ticket = await SupportHarness.OkAsync<TicketResponse>(created, "raise");

        var assigned = await harness.InternalAsync<TicketRowResponse>(
            HttpMethod.Post,
            $"/v1/internal/support/tickets/{ticket.TicketId}/assign",
            new { actorId = agentId, actorRole = "support_csr" });

        Assert.Equal("IN_PROGRESS", assigned.Status);
        Assert.Equal(agentId, assigned.AssignedTo);

        // Timestamps on this table come from Postgres, not from the service's clock — see
        // ITicketRepository.AppendEventAsync. So they are asserted for presence and for order,
        // which is what a thread has to get right, rather than against a fixed instant.
        Assert.NotNull(assigned.AssignedAt);
        Assert.True(assigned.AssignedAt >= assigned.CreatedAt);

        var answered = await harness.InternalAsync<TicketRowResponse>(
            HttpMethod.Post,
            $"/v1/internal/support/tickets/{ticket.TicketId}/respond",
            new { actorId = agentId, actorRole = "support_csr", response = "We are checking the route with the driver." });

        Assert.Equal("IN_PROGRESS", answered.Status);
        Assert.Null(answered.ResolvedAt);

        var resolved = await harness.InternalAsync<TicketRowResponse>(
            HttpMethod.Post,
            $"/v1/internal/support/tickets/{ticket.TicketId}/resolve",
            new { actorId = agentId, actorRole = "support_csr", response = "The fare has been adjusted. Sorry about that." });

        Assert.Equal("RESOLVED", resolved.Status);
        Assert.NotNull(resolved.ResolvedAt);
        Assert.True(resolved.ResolvedAt >= resolved.AssignedAt);
        Assert.Equal(agentId, resolved.ResolvedBy);

        // What the user sees.
        var detail = await harness.GetAsync<TicketDetailResponse>(
            $"/v1/support/tickets/{userId}/{ticket.TicketId}", bearer);

        Assert.Equal("RESOLVED", detail.Status);
        Assert.Equal("The fare has been adjusted. Sorry about that.", detail.AdminResponse);

        // Three entries, oldest first: the transitions and both replies — and NOT the assignment.
        Assert.Equal(["opened", "responded", "resolved"], detail.Thread.Select(e => e.Kind));

        var responded = detail.Thread[1];
        Assert.Equal("We are checking the route with the driver.", responded.Body);

        // IN_PROGRESS → IN_PROGRESS: the assignment already moved it, and a reply moves nothing on
        // its own. The entry is still recorded, because what the user needs to see is the reply.
        Assert.Equal("IN_PROGRESS", responded.FromStatus);
        Assert.Equal("IN_PROGRESS", responded.ToStatus);
        Assert.Equal("support_csr", responded.ActorRole);

        var closed = detail.Thread[2];
        Assert.Equal("IN_PROGRESS", closed.FromStatus);
        Assert.Equal("RESOLVED", closed.ToStatus);

        // The thread reads in the order things happened, and `resolved` carries the same instant the
        // ticket's own `resolvedAt` does — one clock, one fact.
        Assert.Equal(resolved.ResolvedAt, closed.At);
        Assert.True(detail.Thread[0].At <= detail.Thread[1].At);
        Assert.True(detail.Thread[1].At <= closed.At);

        // Every reply survives — the ticket's own column holds only the latest.
        Assert.Contains(detail.Thread, e => e.Body == "We are checking the route with the driver.");

        // The agent's identity is never on the user's thread.
        Assert.DoesNotContain(agentId.ToString(), System.Text.Json.JsonSerializer.Serialize(detail.Thread), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_assignment_is_recorded_but_withheld_from_the_user()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (userId, bearer) = await harness.CreatePassengerAsync();
        var agentId = await harness.CreateUserAsync("support_csr");

        using var created = await harness.PostAsync(
            "/v1/support/tickets", new { category = "booking", description = "Nobody came." }, bearer);

        var ticket = await SupportHarness.OkAsync<TicketResponse>(created, "raise");

        await harness.InternalAsync<TicketRowResponse>(
            HttpMethod.Post,
            $"/v1/internal/support/tickets/{ticket.TicketId}/assign",
            new { actorId = agentId });

        // Written.
        var stored = await harness.ThreadAsync(ticket.TicketId);
        Assert.Equal(["opened", "assigned"], stored.Select(e => e.Kind));

        // Not shown.
        var detail = await harness.GetAsync<TicketDetailResponse>(
            $"/v1/support/tickets/{userId}/{ticket.TicketId}", bearer);

        Assert.Equal(["opened"], detail.Thread.Select(e => e.Kind));

        // The agent sees the whole thread, and who has it.
        var row = await harness.InternalAsync<TicketRowResponse>(
            HttpMethod.Get, $"/v1/internal/support/tickets/{ticket.TicketId}");

        Assert.Equal(["opened", "assigned"], row.Thread.Select(e => e.Kind));
        Assert.Equal(agentId, row.AssignedTo);
    }

    [Fact]
    public async Task A_daily_fee_refund_request_is_routed_to_the_finance_queue()
    {
        // US-9.23 → US-14.11. The routing is derived from the category, so a row written by
        // subscription-svc's own refund intake — which knows nothing about queues — lands on the
        // Finance pile exactly like one raised through this service.
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (driverId, driverBearer) = await harness.CreateDriverAsync();
        var (_, passengerBearer) = await harness.CreatePassengerAsync();

        var raisedElsewhere = await harness.SeedRefundRequestAsync(driverId);

        using var raisedHere = await harness.PostAsync(
            "/v1/support/tickets",
            new { category = "daily_fee_refund", description = "Charged after the app crashed on Go Online." },
            driverBearer);

        var mine = await SupportHarness.OkAsync<TicketResponse>(raisedHere, "raise");
        Assert.Equal("finance", mine.Queue);

        using var ordinary = await harness.PostAsync(
            "/v1/support/tickets", new { category = "booking", description = "Late pickup." }, passengerBearer);

        var support = await SupportHarness.OkAsync<TicketResponse>(ordinary, "raise");
        Assert.Equal("support", support.Queue);

        var finance = await harness.InternalAsync<CursorPage<TicketRowResponse>>(
            HttpMethod.Get, "/v1/internal/support/tickets?queue=finance");

        Assert.Equal(2, finance.Items.Count);
        Assert.Contains(finance.Items, t => t.TicketId == raisedElsewhere);
        Assert.Contains(finance.Items, t => t.TicketId == mine.TicketId);
        Assert.All(finance.Items, t => Assert.Equal("finance", t.Queue));

        var csr = await harness.InternalAsync<CursorPage<TicketRowResponse>>(
            HttpMethod.Get, "/v1/internal/support/tickets?queue=support");

        var only = Assert.Single(csr.Items);
        Assert.Equal(support.TicketId, only.TicketId);
    }

    [Fact]
    public async Task Another_services_evidence_reaches_the_agent_and_not_the_user()
    {
        // fare-svc (C050) writes AL-47's QR-dispute evidence into §13's original `screenshot_url`,
        // which nothing in support-svc writes. It has to reach the Finance agent — dropping it would
        // lose the attachment on a ticket this service is responsible for showing — and it must not
        // reach the user, because it is an unsigned, uncontrolled URL.
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (driverId, driverBearer) = await harness.CreateDriverAsync();

        const string evidence = "https://storage.mageride.test/claims/c050-evidence.png";
        var ticketId = await harness.SeedQrDisputeAsync(driverId, evidence);

        var row = await harness.InternalAsync<TicketRowResponse>(
            HttpMethod.Get, $"/v1/internal/support/tickets/{ticketId}");

        Assert.Equal("finance", row.Queue);
        Assert.Equal(evidence, row.LegacyScreenshotUrl);
        Assert.Null(row.ScreenshotUrl);

        var detail = await harness.GetAsync<TicketDetailResponse>(
            $"/v1/support/tickets/{driverId}/{ticketId}", driverBearer);

        Assert.Null(detail.ScreenshotUrl);
        Assert.DoesNotContain(
            evidence, System.Text.Json.JsonSerializer.Serialize(detail), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_queue_is_oldest_first_and_filterable()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (_, bearer) = await harness.CreatePassengerAsync();
        var agentId = await harness.CreateUserAsync("support_csr");

        var raised = new List<Guid>();

        for (var i = 0; i < 3; i++)
        {
            using var created = await harness.PostAsync(
                "/v1/support/tickets", new { category = "booking", description = $"Complaint {i}." }, bearer);

            raised.Add((await SupportHarness.OkAsync<TicketResponse>(created, "raise")).TicketId);
        }

        var page = await harness.InternalAsync<CursorPage<TicketRowResponse>>(
            HttpMethod.Get, "/v1/internal/support/tickets?limit=2");

        // A queue is worked from its head: the complaint that has waited longest comes first.
        Assert.Equal(raised[0], page.Items[0].TicketId);
        Assert.Equal(raised[1], page.Items[1].TicketId);
        Assert.True(page.HasMore);

        var next = await harness.InternalAsync<CursorPage<TicketRowResponse>>(
            HttpMethod.Get, $"/v1/internal/support/tickets?limit=2&cursor={Uri.EscapeDataString(page.Cursor!)}");

        Assert.Equal(raised[2], Assert.Single(next.Items).TicketId);
        Assert.False(next.HasMore);

        await harness.InternalAsync<TicketRowResponse>(
            HttpMethod.Post,
            $"/v1/internal/support/tickets/{raised[1]}/assign",
            new { actorId = agentId });

        var open = await harness.InternalAsync<CursorPage<TicketRowResponse>>(
            HttpMethod.Get, "/v1/internal/support/tickets?status=OPEN");

        Assert.Equal(2, open.Items.Count);
        Assert.DoesNotContain(open.Items, t => t.TicketId == raised[1]);

        var mine = await harness.InternalAsync<CursorPage<TicketRowResponse>>(
            HttpMethod.Get, $"/v1/internal/support/tickets?assignedTo={agentId}");

        Assert.Equal(raised[1], Assert.Single(mine.Items).TicketId);
    }

    [Fact]
    public async Task A_second_resolution_is_a_conflict_rather_than_an_overwrite()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (_, bearer) = await harness.CreatePassengerAsync();
        var first = await harness.CreateUserAsync("support_csr");
        var second = await harness.CreateUserAsync("support_csr");

        using var created = await harness.PostAsync(
            "/v1/support/tickets", new { category = "wallet", description = "Missing top-up." }, bearer);

        var ticket = await SupportHarness.OkAsync<TicketResponse>(created, "raise");

        await harness.InternalAsync<TicketRowResponse>(
            HttpMethod.Post,
            $"/v1/internal/support/tickets/{ticket.TicketId}/resolve",
            new { actorId = first, response = "Refunded." });

        using var again = await harness.InternalAsync(
            HttpMethod.Post,
            $"/v1/internal/support/tickets/{ticket.TicketId}/resolve",
            new { actorId = second, response = "Actually, declined." });

        var (status, code, _) = await SupportHarness.ProblemAsync(again);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("conflict", code);

        // Who resolved it, and what they said, survives the loser.
        var row = await harness.InternalAsync<TicketRowResponse>(
            HttpMethod.Get, $"/v1/internal/support/tickets/{ticket.TicketId}");

        Assert.Equal(first, row.ResolvedBy);
        Assert.Equal("Refunded.", row.AdminResponse);
        Assert.Equal(["opened", "resolved"], row.Thread.Select(e => e.Kind));
    }

    [Fact]
    public async Task A_resolved_ticket_cannot_be_assigned_or_answered_again()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (_, bearer) = await harness.CreatePassengerAsync();
        var agentId = await harness.CreateUserAsync("support_csr");

        using var created = await harness.PostAsync(
            "/v1/support/tickets", new { category = "wallet", description = "Closed already." }, bearer);

        var ticket = await SupportHarness.OkAsync<TicketResponse>(created, "raise");

        await harness.InternalAsync<TicketRowResponse>(
            HttpMethod.Post,
            $"/v1/internal/support/tickets/{ticket.TicketId}/resolve",
            new { actorId = agentId, response = "Done." });

        using var assigned = await harness.InternalAsync(
            HttpMethod.Post, $"/v1/internal/support/tickets/{ticket.TicketId}/assign", new { actorId = agentId });

        using var answered = await harness.InternalAsync(
            HttpMethod.Post,
            $"/v1/internal/support/tickets/{ticket.TicketId}/respond",
            new { actorId = agentId, response = "One more thing." });

        Assert.Equal(HttpStatusCode.Conflict, assigned.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, answered.StatusCode);
    }

    [Fact]
    public async Task A_ticket_that_does_not_exist_is_404_and_an_empty_response_is_refused()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var agentId = await harness.CreateUserAsync("support_csr");

        using var missing = await harness.InternalAsync(
            HttpMethod.Post,
            $"/v1/internal/support/tickets/{Guid.CreateVersion7()}/resolve",
            new { actorId = agentId, response = "Nothing here." });

        var (status, code, _) = await SupportHarness.ProblemAsync(missing);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not-found", code);

        var (_, bearer) = await harness.CreatePassengerAsync();

        using var created = await harness.PostAsync(
            "/v1/support/tickets", new { category = "wallet", description = "Real." }, bearer);

        var ticket = await SupportHarness.OkAsync<TicketResponse>(created, "raise");

        // An answer the user will read cannot be empty.
        using var blank = await harness.InternalAsync(
            HttpMethod.Post,
            $"/v1/internal/support/tickets/{ticket.TicketId}/resolve",
            new { actorId = agentId, response = "   " });

        var (blankStatus, blankCode, _) = await SupportHarness.ProblemAsync(blank);

        Assert.Equal(HttpStatusCode.BadRequest, blankStatus);
        Assert.Equal("validation-failed", blankCode);
    }

    [Fact]
    public async Task The_queue_is_unreachable_without_the_internal_key()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);

        using var noKey = await harness.InternalAsync(
            HttpMethod.Get, "/v1/internal/support/tickets", apiKey: null);

        using var wrongKey = await harness.InternalAsync(
            HttpMethod.Get, "/v1/internal/support/tickets", apiKey: "not-the-key");

        // 404, matching what the gateway answers for the whole /v1/internal prefix: a caller who is
        // not entitled to the internal plane should not be able to map it.
        Assert.Equal(HttpStatusCode.NotFound, noKey.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongKey.StatusCode);
    }

    [Fact]
    public async Task With_no_internal_key_configured_the_whole_queue_is_unmapped()
    {
        await using var harness = await SupportHarness.StartAsync(postgres, withInternalPlane: false);
        var (_, bearer) = await harness.CreatePassengerAsync();

        // Users can still raise tickets — and every one of them stays OPEN for ever, which is what
        // start-up says out loud.
        using var created = await harness.PostAsync(
            "/v1/support/tickets", new { category = "wallet", description = "Nobody will read this." }, bearer);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // Not mapped, so the route matches no endpoint — and the kernel's deny-by-default fallback
        // policy (AL-06) covers unmatched requests as well as endpoints, so the answer is `401`
        // rather than routing's `404`. Either way there is no queue behind it, and the C008 gateway
        // refuses the whole `/v1/internal` prefix at the edge in any case. Correct key or not:
        var withKey = await harness.InternalAsync(HttpMethod.Get, "/v1/internal/support/tickets");
        var withoutKey = await harness.InternalAsync(HttpMethod.Get, "/v1/internal/support/tickets", apiKey: null);

        using (withKey)
        using (withoutKey)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, withKey.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);
        }
    }
}
