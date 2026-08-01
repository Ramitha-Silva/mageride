using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using MageRide.Support.Configuration;
using MageRide.Support.Faq;
using MageRide.Support.Persistence;
using MageRide.Support.Screenshots;
using MageRide.Support.Tickets;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.Support.Endpoints;

/// <summary>
/// <c>/v1/support</c> — the FAQ, a user's own tickets, and the screenshot they attach to one.
/// </summary>
public static class SupportEndpoints
{
    public static IEndpointRouteBuilder MapSupportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var support = endpoints.MapGroup("/v1/support").WithTags("support").RequireAuthorization();

        // Bearer, any role — a CSR answering a ticket reads the same FAQ the passenger does, which
        // is also why content-svc gates its own copy the same way.
        support.MapGet("/faq", ListFaqAsync).WithName("listFaqArticles");
        support.MapGet("/faq/{articleId}", GetFaqArticleAsync).WithName("getFaqArticle");

        support.MapPost("/tickets", RaiseTicketAsync).WithName("createSupportTicket");
        support.MapGet("/tickets/{userId}", ListTicketsAsync).WithName("listSupportTickets");
        support.MapGet("/tickets/{userId}/{ticketId}", GetTicketAsync).WithName("getSupportTicket");

        // DisableAntiforgery for the reason ride-svc's proof photo and subscription-svc's transfer
        // slip do: the request is a Bearer-authenticated multipart POST from a mobile app, not a
        // browser form, so there is no cookie to protect and no token for a phone to carry.
        support.MapPost("/screenshots", UploadScreenshotAsync)
            .WithName("uploadSupportScreenshot")
            .DisableAntiforgery();

        // AllowAnonymous because the signature is the credential — see IScreenshotLinks. The route
        // sits inside the authorized group, so the attribute is what opens it and it is the only
        // one here that carries it.
        support.MapGet("/screenshots/{uploadId}", GetScreenshotAsync)
            .AllowAnonymous()
            .WithName("getSupportScreenshot");

        return endpoints;
    }

    // -----------------------------------------------------------------------------------------
    // FAQ (US-16.1)
    // -----------------------------------------------------------------------------------------

    private static async Task<Ok<FaqListResponse>> ListFaqAsync(
        string? lang,
        string? category,
        IFaqService faq,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(faq);

        var answer = await faq.ListAsync(lang, category, cancellationToken);

        return TypedResults.Ok(new FaqListResponse(
            [
                .. answer.Value.Select(article => new FaqSummaryResponse(
                    article.Id, article.Title, article.Category, answer.Language)),
            ]));
    }

    private static async Task<Ok<FaqArticleResponse>> GetFaqArticleAsync(
        string articleId,
        string? lang,
        IFaqService faq,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(faq);

        var answer = await faq.GetAsync(RequestIds.Require(articleId, "articleId"), lang, cancellationToken);
        var article = answer.Value;

        // The id returned is the row actually served, not the one asked for: a client that followed
        // a fallback and then bookmarked the answer should bookmark what it read.
        return TypedResults.Ok(new FaqArticleResponse(
            article.Id, article.Title, article.Category, answer.Language, article.Body));
    }

    // -----------------------------------------------------------------------------------------
    // Tickets (US-16.2)
    // -----------------------------------------------------------------------------------------

    private static async Task<Created<TicketResponse>> RaiseTicketAsync(
        RaiseTicketBody? body,
        HttpContext context,
        ITicketService tickets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tickets);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var category = body?.Category?.Trim();
        var description = body?.Description?.Trim();

        if (string.IsNullOrWhiteSpace(category))
        {
            errors["category"] = ["A category is required."];
        }
        else if (category.Length > 60)
        {
            errors["category"] = ["A category is at most 60 characters."];
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            errors["description"] = ["A description is required."];
        }
        else if (description.Length > 4_000)
        {
            errors["description"] = ["A description is at most 4000 characters."];
        }

        Guid? tripId = null;

        if (!string.IsNullOrWhiteSpace(body?.TripId))
        {
            tripId = RequestIds.Optional(body.TripId);

            if (tripId is null)
            {
                errors["tripId"] = ["tripId is not an id."];
            }
        }

        Guid? screenshotFileId = null;

        if (!string.IsNullOrWhiteSpace(body?.ScreenshotFileId))
        {
            screenshotFileId = RequestIds.Optional(body.ScreenshotFileId);

            if (screenshotFileId is null)
            {
                errors["screenshotFileId"] = ["screenshotFileId is not an id."];
            }
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors, "The ticket is not valid.");
        }

        var subject = context.User.RequireSubjectId();

        var ticket = await tickets.RaiseAsync(
            subject, category!, description!, tripId, screenshotFileId, cancellationToken);

        return TypedResults.Created(
            $"/v1/support/tickets/{subject}/{ticket.Id}", TicketResponse.From(ticket));
    }

    private static async Task<Ok<CursorPage<TicketResponse>>> ListTicketsAsync(
        string userId,
        string? cursor,
        int? limit,
        HttpContext context,
        ITicketService tickets,
        IOptions<SupportOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(options);

        // A caller may only read their own. There is deliberately **no back-office exception** here,
        // unlike subscription-svc's fee history: an agent has the queue, which is RBAC-gated and
        // audited by admin-bff (D-35), and a route that let any internal role page a named user's
        // complaints from the app surface would be the same read with none of that.
        var subject = RequestIds.RequireSelf(context.User.RequireSubjectId(), userId);

        var page = PageRequest.Create(cursor, limit);
        var size = Math.Min(page.Limit, options.Value.MaxPageSize);

        var rows = await tickets.ListForUserAsync(
            subject, TicketCursors.Decode(page.Cursor), size + 1, cancellationToken);

        return TypedResults.Ok(
            CursorPage<SupportTicket>.FromOverfetch(rows, size, TicketCursors.Encode)
                .Select(TicketResponse.From));
    }

    private static async Task<Ok<TicketDetailResponse>> GetTicketAsync(
        string userId,
        string ticketId,
        HttpContext context,
        ITicketService tickets,
        IScreenshotLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(links);

        var subject = RequestIds.RequireSelf(context.User.RequireSubjectId(), userId);

        var detail = await tickets.ReadForUserAsync(
            subject, RequestIds.Require(ticketId, "ticketId"), cancellationToken);

        return TypedResults.Ok(TicketProjections.Detail(
            detail.Ticket,
            detail.Thread,
            detail.Ticket.ScreenshotUploadId is { } upload ? links.Create(upload) : null));
    }

    // -----------------------------------------------------------------------------------------
    // Screenshots (US-16.2)
    // -----------------------------------------------------------------------------------------

    /// <remarks>
    /// Multipart, one <c>file</c> part, read as a stream rather than buffered — a phone screenshot
    /// is megabytes and every one of them would otherwise sit in this process's heap.
    /// </remarks>
    private static async Task<Created<UploadedScreenshotResponse>> UploadScreenshotAsync(
        HttpContext context,
        ITicketService tickets,
        IOptions<SupportOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(options);

        if (!context.Request.HasFormContentType)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["file"] = ["Expected multipart/form-data with a `file` part."],
            });
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files["file"]
                   ?? throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
                   {
                       ["file"] = ["No `file` part in the upload."],
                   });

        ScreenshotUpload.RequireWithinLimit(file.Length, options.Value.ScreenshotMaxBytes);

        await using var content = file.OpenReadStream();

        var stored = await tickets.StoreScreenshotAsync(
            context.User.RequireSubjectId(), file.FileName, content, cancellationToken);

        return TypedResults.Created(
            $"/v1/support/screenshots/{stored.FileId}",
            new UploadedScreenshotResponse(
                stored.FileId,
                stored.Bytes,
                Convert.ToHexStringLower(stored.Sha256),
                stored.AutoDeleteAt));
    }

    /// <remarks>
    /// A bad signature, an expired one and an unknown id all answer <c>403</c> with the same
    /// message: distinguishing them tells somebody probing which half of a forged link to work on,
    /// and "that id exists" is itself something a forged link should not be able to learn.
    /// </remarks>
    private static async Task<IResult> GetScreenshotAsync(
        string uploadId,
        string? expires,
        string? signature,
        IUploadRepository uploads,
        IScreenshotStore store,
        IScreenshotLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(links);

        var id = RequestIds.Optional(uploadId) ?? Guid.Empty;

        if (id == Guid.Empty || !links.Verify(id, expires, signature))
        {
            throw new MageRideException(MageRideErrors.Forbidden, "That link is not valid.");
        }

        var upload = await uploads.FindAsync(id, cancellationToken);

        // The kind is checked as well as the id: the signature covers the kind, but this route reads
        // `docs.uploads`, which also holds driving licences and bank statements. A signing key that
        // ever leaked would then be a key to somebody's NIC rather than to a screenshot.
        if (upload is null
            || !string.Equals(upload.Kind, SupportUploadKinds.Screenshot, StringComparison.Ordinal))
        {
            throw new MageRideException(MageRideErrors.Forbidden, "That link is not valid.");
        }

        var opened = store.Open(upload.StorageUrl);

        if (opened is not { } file)
        {
            // Δ D-36: on a bucket the bytes are not this process's to stream, so the officer's
            // browser is redirected to a short-lived presigned URL. The signature check above has
            // already happened, so the redirect is issued only to a caller who proved the link.
            if (store.Presign(upload.StorageUrl) is { } direct)
            {
                return TypedResults.Redirect(direct, permanent: false, preserveMethod: false);
            }

            // On a filesystem this means the pod that wrote the image is gone, which the store
            // warns about at start-up.
            return TypedResults.NotFound();
        }

        return TypedResults.Stream(file.Content, file.ContentType);
    }
}
