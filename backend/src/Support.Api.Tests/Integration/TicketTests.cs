using System.Net;
using System.Text;
using MageRide.Shared.Primitives;
using MageRide.Support.Endpoints;
using MageRide.Support.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Support.Tests.Integration;

/// <summary>
/// US-16.2 — raising a ticket, attaching a screenshot to it, and reading your own back.
/// </summary>
[Collection<SupportCollection>]
public sealed class TicketTests(PostgresFixture postgres)
{
    /// <summary>A tiny PNG. The bytes are never decoded; what matters is that they round-trip.</summary>
    private static byte[] Screenshot(string marker = "c053") =>
        [.. new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, .. Encoding.UTF8.GetBytes(marker)];

    [Fact]
    public async Task A_ticket_opens_with_its_own_thread_entry()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (userId, bearer) = await harness.CreatePassengerAsync();

        var tripId = Guid.CreateVersion7();

        using var response = await harness.PostAsync(
            "/v1/support/tickets",
            new { category = "booking", description = "The driver never arrived.", tripId },
            bearer);

        var ticket = await SupportHarness.OkAsync<TicketResponse>(response, "POST /v1/support/tickets");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("OPEN", ticket.Status);
        Assert.Equal("support", ticket.Queue);
        Assert.Equal(tripId, ticket.TripId);
        Assert.Null(ticket.ResolvedAt);

        // The thread starts full rather than empty: the user's own complaint is the first thing in
        // the record of it.
        var thread = await harness.ThreadAsync(ticket.TicketId);
        var opened = Assert.Single(thread);

        Assert.Equal("opened", opened.Kind);
        Assert.Null(opened.From);
        Assert.Equal("OPEN", opened.To);
        Assert.Equal(userId, opened.ActorId);
    }

    [Fact]
    public async Task A_screenshot_is_stored_in_object_storage_and_linked_by_id()
    {
        // The second definition of done, asserted on the row rather than on the response.
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (userId, bearer) = await harness.CreatePassengerAsync();

        using var uploaded = await harness.UploadScreenshotAsync(bearer, Screenshot());
        var file = await SupportHarness.OkAsync<UploadedScreenshotResponse>(uploaded, "POST /v1/support/screenshots");

        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        Assert.Equal(12, file.SizeBytes);
        Assert.Equal(64, file.Sha256.Length);

        // NFR-28's 90-day raw delete is on the row, measured from the same `now()` the row's own
        // `created_at` is — so the promise is checkable against the row rather than against a
        // replica's clock.
        var upload = await harness.UploadAsync(file.FileId);
        Assert.Equal(userId, upload.OwnerId);
        Assert.Equal("support_screenshot", upload.Kind);
        Assert.Equal(upload.CreatedAt.AddDays(90), upload.AutoDeleteAt);

        using var created = await harness.PostAsync(
            "/v1/support/tickets",
            new { category = "wallet", description = "Top-up did not arrive.", screenshotFileId = file.FileId },
            bearer);

        var ticket = await SupportHarness.OkAsync<TicketResponse>(created, "POST /v1/support/tickets");

        // Linked by id, not by public URL: the ticket carries the docs.uploads id and §13's
        // `screenshot_url` — the public-URL column — stays NULL.
        var row = await harness.TicketRowAsync(ticket.TicketId);

        Assert.Equal(file.FileId, row["screenshot_upload_id"]);
        Assert.Null(row["screenshot_url"]);

        // And the user reads it back through a signed, expiring link rather than a durable URL.
        var detail = await harness.GetAsync<TicketDetailResponse>(
            $"/v1/support/tickets/{userId}/{ticket.TicketId}", bearer);

        Assert.NotNull(detail.ScreenshotUrl);
        Assert.StartsWith($"/v1/support/screenshots/{file.FileId}?expires=", detail.ScreenshotUrl, StringComparison.Ordinal);
        Assert.Contains("&signature=", detail.ScreenshotUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_screenshot_link_serves_the_bytes_and_stops_working_when_it_expires()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (userId, bearer) = await harness.CreatePassengerAsync();

        var bytes = Screenshot("round-trip");

        using var uploaded = await harness.UploadScreenshotAsync(bearer, bytes);
        var file = await SupportHarness.OkAsync<UploadedScreenshotResponse>(uploaded, "upload");

        using var created = await harness.PostAsync(
            "/v1/support/tickets",
            new { category = "wallet", description = "See attached.", screenshotFileId = file.FileId },
            bearer);

        var ticket = await SupportHarness.OkAsync<TicketResponse>(created, "raise");

        var detail = await harness.GetAsync<TicketDetailResponse>(
            $"/v1/support/tickets/{userId}/{ticket.TicketId}", bearer);

        // No bearer: the signature is the credential, which is what lets an image loader follow it.
        using var served = await harness.GetAsync(detail.ScreenshotUrl!);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(bytes, await served.Content.ReadAsByteArrayAsync());

        // Past the TTL the same link is refused.
        harness.Clock.Advance(TimeSpan.FromMinutes(16));

        using var expired = await harness.GetAsync(detail.ScreenshotUrl!);
        var (status, code, _) = await SupportHarness.ProblemAsync(expired);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("forbidden", code);
    }

    [Fact]
    public async Task An_unsigned_or_tampered_screenshot_link_is_refused()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (_, bearer) = await harness.CreatePassengerAsync();

        using var uploaded = await harness.UploadScreenshotAsync(bearer, Screenshot());
        var file = await SupportHarness.OkAsync<UploadedScreenshotResponse>(uploaded, "upload");

        // Knowing the id is not enough — which is the whole reason the ticket links an id and the
        // user is served a signature.
        using var bare = await harness.GetAsync($"/v1/support/screenshots/{file.FileId}");
        Assert.Equal(HttpStatusCode.Forbidden, bare.StatusCode);

        using var forged = await harness.GetAsync(
            $"/v1/support/screenshots/{file.FileId}?expires=9999999999&signature=deadbeef");
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
    }

    [Fact]
    public async Task A_screenshot_that_is_not_yours_cannot_be_attached()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (_, mine) = await harness.CreatePassengerAsync();
        var (_, theirs) = await harness.CreatePassengerAsync();

        using var uploaded = await harness.UploadScreenshotAsync(theirs, Screenshot());
        var file = await SupportHarness.OkAsync<UploadedScreenshotResponse>(uploaded, "upload");

        using var stolen = await harness.PostAsync(
            "/v1/support/tickets",
            new { category = "wallet", description = "Not mine.", screenshotFileId = file.FileId },
            mine);

        var (status, code, _) = await SupportHarness.ProblemAsync(stolen);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("validation-failed", code);

        // An id that does not exist answers identically — telling the two apart would make the
        // route an oracle over other people's uploads.
        using var missing = await harness.PostAsync(
            "/v1/support/tickets",
            new { category = "wallet", description = "Nothing there.", screenshotFileId = Guid.CreateVersion7() },
            mine);

        var (missingStatus, missingCode, _) = await SupportHarness.ProblemAsync(missing);

        Assert.Equal(HttpStatusCode.BadRequest, missingStatus);
        Assert.Equal("validation-failed", missingCode);
    }

    [Fact]
    public async Task One_screenshot_belongs_to_one_ticket()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (_, bearer) = await harness.CreatePassengerAsync();

        using var uploaded = await harness.UploadScreenshotAsync(bearer, Screenshot());
        var file = await SupportHarness.OkAsync<UploadedScreenshotResponse>(uploaded, "upload");

        using var first = await harness.PostAsync(
            "/v1/support/tickets",
            new { category = "wallet", description = "First complaint.", screenshotFileId = file.FileId },
            bearer);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await harness.PostAsync(
            "/v1/support/tickets",
            new { category = "wallet", description = "Second complaint.", screenshotFileId = file.FileId },
            bearer);

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task An_upload_larger_than_the_ceiling_is_refused()
    {
        await using var harness = await SupportHarness.StartAsync(
            postgres,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Support:ScreenshotMaxBytes"] = (128 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        var (_, bearer) = await harness.CreatePassengerAsync();

        using var response = await harness.UploadScreenshotAsync(bearer, new byte[200 * 1024]);
        var (status, code, _) = await SupportHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal("payload-too-large", code);
    }

    [Fact]
    public async Task A_double_tapped_submit_under_one_key_raises_one_ticket()
    {
        // R-14, and the route where it matters: the first thing somebody does when nothing appears
        // to happen is tap Submit again.
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (userId, bearer) = await harness.CreatePassengerAsync();

        var key = Guid.NewGuid().ToString();
        var body = new { category = "booking", description = "Charged for a ride I cancelled." };

        using var first = await harness.PostWithKeyAsync("/v1/support/tickets", body, bearer, key);
        using var second = await harness.PostWithKeyAsync("/v1/support/tickets", body, bearer, key);

        var one = await SupportHarness.OkAsync<TicketResponse>(first, "first");
        var two = await SupportHarness.OkAsync<TicketResponse>(second, "second");

        Assert.Equal(one.TicketId, two.TicketId);

        var page = await harness.GetAsync<CursorPage<TicketResponse>>($"/v1/support/tickets/{userId}", bearer);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task A_users_tickets_are_paged_newest_first_and_are_only_theirs()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (userId, bearer) = await harness.CreatePassengerAsync();
        var (otherId, otherBearer) = await harness.CreatePassengerAsync();

        for (var i = 0; i < 3; i++)
        {
            using var raised = await harness.PostAsync(
                "/v1/support/tickets",
                new { category = "booking", description = $"Complaint {i}." },
                bearer);

            Assert.Equal(HttpStatusCode.Created, raised.StatusCode);
        }

        using var theirs = await harness.PostAsync(
            "/v1/support/tickets", new { category = "wallet", description = "Not yours." }, otherBearer);
        Assert.Equal(HttpStatusCode.Created, theirs.StatusCode);

        var first = await harness.GetAsync<CursorPage<TicketResponse>>(
            $"/v1/support/tickets/{userId}?limit=2", bearer);

        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.Cursor);
        Assert.Equal("Complaint 2.", await DescriptionOfAsync(harness, userId, bearer, first.Items[0].TicketId));

        var next = await harness.GetAsync<CursorPage<TicketResponse>>(
            $"/v1/support/tickets/{userId}?limit=2&cursor={Uri.EscapeDataString(first.Cursor!)}", bearer);

        Assert.Single(next.Items);
        Assert.False(next.HasMore);
        Assert.Null(next.Cursor);

        // Three, not four: the other user's ticket is not on this page and never was.
        Assert.Equal(3, first.Items.Count + next.Items.Count);

        // And another user's list is not readable at all, even by id.
        using var forbidden = await harness.GetAsync($"/v1/support/tickets/{otherId}", bearer);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Somebody_elses_ticket_is_not_found_rather_than_forbidden()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (mineId, mine) = await harness.CreatePassengerAsync();
        var (_, theirs) = await harness.CreatePassengerAsync();

        using var created = await harness.PostAsync(
            "/v1/support/tickets", new { category = "wallet", description = "Private." }, theirs);

        var ticket = await SupportHarness.OkAsync<TicketResponse>(created, "raise");

        // Read under the caller's own userId, so the path check passes and the row scoping is what
        // decides. 404, not 403: a 403 would confirm the id names a real complaint.
        using var response = await harness.GetAsync($"/v1/support/tickets/{mineId}/{ticket.TicketId}", mine);
        var (status, code, _) = await SupportHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not-found", code);
    }

    [Fact]
    public async Task A_ticket_with_no_category_or_description_is_refused_field_by_field()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (_, bearer) = await harness.CreatePassengerAsync();

        using var response = await harness.PostAsync(
            "/v1/support/tickets", new { category = "   ", description = "" }, bearer);

        var (status, code, body) = await SupportHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("validation-failed", code);
        Assert.Contains("category", body, StringComparison.Ordinal);
        Assert.Contains("description", body, StringComparison.Ordinal);
    }

    private static async Task<string> DescriptionOfAsync(
        SupportHarness harness, Guid userId, string bearer, Guid ticketId)
    {
        var detail = await harness.GetAsync<TicketDetailResponse>(
            $"/v1/support/tickets/{userId}/{ticketId}", bearer);

        return detail.Description;
    }
}
