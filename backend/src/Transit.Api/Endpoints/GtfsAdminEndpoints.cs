using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Primitives;
using MageRide.Transit.Configuration;
using MageRide.Transit.Gtfs;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace MageRide.Transit.Endpoints;

/// <summary>The 202 of <c>POST /v1/admin/transit/gtfs/uploads</c>.</summary>
public sealed record FeedUploadAcceptedResponse(Guid FeedVersionId);

/// <summary>`FeedUploadStatus` — what SCR-AP-016's status stepper and preview card poll (US-28.1).</summary>
/// <param name="Warnings">
/// One line per warning. BR-32.1's warnings never block activation, so they are shown beside the
/// Activate button rather than instead of it.
/// </param>
/// <param name="ErrorSummary">
/// <b>Capped at five</b> by the contract. The full row-level report is a separate download, so a
/// feed with thousands of errors does not make this response unusable.
/// </param>
public sealed record FeedUploadStatusResponse(
    Guid FeedVersionId,
    string Status,
    [property: JsonConverter(typeof(LiteralKeyDictionaryConverter<long>))]
    IReadOnlyDictionary<string, long> Counts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FeedInfoVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateOnly? ServiceStart,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateOnly? ServiceEnd,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> ErrorSummary);

/// <summary>`FeedVersion` — one row of SCR-AP-016's history table, and the 200 of activate.</summary>
public sealed record FeedVersionResponse(
    Guid FeedVersionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FeedInfoVersion,
    string FileName,
    string Sha256,
    Guid UploadedBy,
    DateTimeOffset UploadedAt,
    [property: JsonConverter(typeof(LiteralKeyDictionaryConverter<long>))]
    IReadOnlyDictionary<string, long> Counts,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ActivatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ArchivedAt);

/// <summary>
/// The GTFS Dataset Manager (AL-54, SCR-AP-016): upload, validate, preview, activate, roll back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Admin and Super Admin only, deny-by-default</b> (AL-06, D2 SCR-AP-016). A Verification
/// Officer, a CSR, a Finance Officer and an Auditor get no Transit-data nav entry and no route
/// here — the Auditor reads what happened through <c>audit.events</c>, which every mutation on
/// this surface writes (D-35).
/// </para>
/// <para>
/// <b>Everything here is transit-svc's, and admin-bff proxies it</b> (ADD §6, C062). The screen
/// talks to `admin.mageride.lk`; the lifecycle lives with the tables it swaps.
/// </para>
/// </remarks>
public static class GtfsAdminEndpoints
{
    /// <summary>The multipart part name the zip arrives as.</summary>
    private const string FilePart = "file";

    /// <summary>Room for the multipart envelope on top of the 200 MB file (BR-32.1).</summary>
    private const long MultipartOverheadBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapGtfsAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var gtfs = routes.MapGroup("/v1/admin/transit/gtfs")
            .WithTags("gtfs-admin")
            .RequireMageRideRole(MageRideRoles.Admin, MageRideRoles.SuperAdmin);

        gtfs.MapPost("/uploads", UploadAsync)
            .WithName("uploadGtfsFeed")
            .WithSummary("Upload a GTFS zip for validation (US-28.1).")
            // No antiforgery token: this is a bearer-authenticated API surface, not a browser form
            // post, and the portal sends the token in a header.
            .DisableAntiforgery()
            // The header is still demanded — by the handler, see RequireIdempotencyKey. What is
            // opted out of is the kernel's *replay*, which hashes and buffers the request body:
            // this body is up to 200 MB, and the endpoint's dedupe key is the file's own sha256
            // (BR-32.1), which is both cheaper and stronger.
            .AllowMissingIdempotencyKey();

        gtfs.MapGet("/uploads/{feedVersionId:guid}", GetUploadAsync)
            .WithName("getGtfsUpload")
            .WithSummary("Validation status and preview.");

        gtfs.MapGet("/uploads/{feedVersionId:guid}/report", GetReportAsync)
            .WithName("getGtfsValidationReport")
            .WithSummary("The full row-level validation report, JSON or CSV.");

        gtfs.MapPost("/uploads/{feedVersionId:guid}/activate", ActivateAsync)
            .WithName("activateGtfsFeed")
            .WithSummary("Swap this feed into the live tables (US-28.2; rollback is the same call, BR-32.3).");

        gtfs.MapGet("/versions", ListVersionsAsync)
            .WithName("listGtfsVersions")
            .WithSummary("Version history, newest first (US-28.3).");

        gtfs.MapGet("/versions/{feedVersionId:guid}/download", DownloadAsync)
            .WithName("downloadGtfsFeed")
            .WithSummary("302 to a short-lived signed URL for the original zip.");

        // Δ C057, and deliberately outside the group above: this is what the 302 points at, and a
        // browser following a redirect does not carry the bearer token that authorised it. The
        // HMAC in the query string is the credential — the same shape a presigned object-storage
        // URL has, which is what this stands in for until an S3 client exists.
        routes.MapGet("/v1/admin/transit/gtfs/objects/{feedVersionId:guid}", GetObjectAsync)
            .AllowAnonymous()
            .WithTags("gtfs-admin")
            .WithName("downloadSignedGtfsObject")
            .WithSummary("Serve a stored feed zip against a signed link.");

        return routes;
    }

    // -----------------------------------------------------------------------------------------
    // US-28.1 — upload, status, report
    // -----------------------------------------------------------------------------------------

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        IGtfsUploadService uploads,
        IOptions<TransitOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(options);

        RequireIdempotencyKey(context);

        var limit = options.Value.Gtfs.MaxUploadBytes;

        RequireWithinLimit(context, limit);

        var section = await ReadFileSectionAsync(context, cancellationToken);

        var version = await uploads.UploadAsync(
            FileName(section), section.Body, context.User.RequireSubjectId(), cancellationToken);

        // 202, not 201: validation has not run, so what exists is an upload awaiting a verdict.
        return TypedResults.Accepted(
            $"/v1/admin/transit/gtfs/uploads/{version.FeedVersionId:D}",
            new FeedUploadAcceptedResponse(version.FeedVersionId));
    }

    private static async Task<IResult> GetUploadAsync(
        Guid feedVersionId, IGtfsFeedVersionRepository repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var version = await Require(repository, feedVersionId, cancellationToken);
        var report = version.Report();

        return Results.Ok(new FeedUploadStatusResponse(
            version.FeedVersionId,
            version.Status,
            version.Counts(),
            version.FeedInfoVersion,
            version.ServiceStart,
            version.ServiceEnd,
            [.. report.Warnings.Select(issue => issue.ToLine())],
            [.. report.Errors.Take(5).Select(issue => issue.ToLine())]));
    }

    private static async Task<IResult> GetReportAsync(
        Guid feedVersionId,
        string? format,
        IGtfsFeedVersionRepository repository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var version = await Require(repository, feedVersionId, cancellationToken);
        var report = version.Report();

        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(report);
        }

        // "What an operator actually fixes the feed from" (D3'): one row per finding, severity
        // first, so a spreadsheet sorts and filters it without a formula.
        return Results.File(
            Encoding.UTF8.GetBytes(ToCsv(report)),
            "text/csv; charset=utf-8",
            $"gtfs-{feedVersionId:D}-report.csv");
    }

    // -----------------------------------------------------------------------------------------
    // US-28.2 — activation
    // -----------------------------------------------------------------------------------------

    private static async Task<IResult> ActivateAsync(
        Guid feedVersionId,
        HttpContext context,
        IGtfsActivationService activation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(activation);

        var version = await activation.ActivateAsync(
            feedVersionId, context.User.RequireSubjectId(), cancellationToken);

        return Results.Ok(ToResponse(version));
    }

    // -----------------------------------------------------------------------------------------
    // US-28.3 — history, rollback, download
    // -----------------------------------------------------------------------------------------

    private static async Task<IResult> ListVersionsAsync(
        HttpContext context, IGtfsFeedVersionRepository repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(repository);

        var page = PageRequest.FromQuery(context.Request);

        (DateTimeOffset UploadedAt, Guid FeedVersionId)? after = null;

        if (page.Cursor is not null)
        {
            if (!CursorCodec.Unsigned.TryDecode<VersionCursor>(page.Cursor, out var decoded) || decoded is null)
            {
                throw new MageRideException(MageRideErrors.ValidationFailed, "cursor is not a cursor this endpoint issued.");
            }

            after = (decoded.UploadedAt, decoded.FeedVersionId);
        }

        var rows = await repository.ListAsync(page, after, cancellationToken);

        var pageOfRows = CursorPage<FeedVersionRow>.FromOverfetch(
            rows,
            page.Limit,
            row => CursorCodec.Unsigned.Encode(new VersionCursor(row.UploadedAt, row.FeedVersionId)));

        return Results.Ok(pageOfRows.Select(ToResponse));
    }

    private static async Task<IResult> DownloadAsync(
        Guid feedVersionId,
        HttpContext context,
        IGtfsFeedVersionRepository repository,
        GtfsDownloadLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(links);

        _ = await Require(repository, feedVersionId, cancellationToken);

        // 302 and not the bytes: BR-32.3's download is a signed object-storage URL, and answering
        // the file here would make the redirect an implementation detail clients could not rely on
        // once a bucket is configured.
        return Results.Redirect(
            links.Create(feedVersionId, context.Request.Scheme, context.Request.Host.Value ?? string.Empty),
            permanent: false);
    }

    private static async Task<IResult> GetObjectAsync(
        Guid feedVersionId,
        HttpContext context,
        IGtfsFeedVersionRepository repository,
        IGtfsObjectStore objects,
        GtfsDownloadLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(links);

        var expiry = context.Request.Query[GtfsDownloadLinks.ExpiryParameter].ToString();
        var signature = context.Request.Query[GtfsDownloadLinks.SignatureParameter].ToString();

        if (!links.IsValid(feedVersionId, expiry, signature))
        {
            // 401 rather than 403: the signature *is* the credential, so a bad or expired one is a
            // missing credential, not an insufficient one.
            throw new MageRideException(
                MageRideErrors.Unauthorized, "This download link is not valid, or it has expired.");
        }

        var version = await Require(repository, feedVersionId, cancellationToken);

        var stream = await objects.OpenAsync(version.StorageKey, cancellationToken)
                     ?? throw new MageRideException(
                         MageRideErrors.NotFound, "The original zip for this feed version is no longer in storage.");

        return Results.File(stream, "application/zip", version.FileName, enableRangeProcessing: true);
    }

    // -----------------------------------------------------------------------------------------
    // Shared
    // -----------------------------------------------------------------------------------------

    /// <summary>The opaque position `GET …/versions` pages on.</summary>
    /// <remarks>
    /// Both halves of the sort key. A cursor on the timestamp alone would drop a version whenever
    /// two uploads shared a microsecond, which two admins clicking Upload at once can produce.
    /// </remarks>
    private sealed record VersionCursor(DateTimeOffset UploadedAt, Guid FeedVersionId);

    private static async Task<FeedVersionRow> Require(
        IGtfsFeedVersionRepository repository, Guid feedVersionId, CancellationToken cancellationToken) =>
        await repository.FindAsync(feedVersionId, cancellationToken)
        ?? throw new MageRideException(MageRideErrors.NotFound, "No such GTFS feed version.");

    private static FeedVersionResponse ToResponse(FeedVersionRow row) => new(
        row.FeedVersionId,
        row.FeedInfoVersion,
        row.FileName,
        row.Sha256,
        row.UploadedBy,
        row.UploadedAt,
        row.Counts(),
        row.Status,
        row.ActivatedAt,
        row.ArchivedAt);

    /// <summary>
    /// The contract makes <c>Idempotency-Key</c> required on this POST, and it stays required even
    /// though the kernel's replay is off here (see the route registration).
    /// </summary>
    /// <remarks>
    /// Enforced with the kernel's own rules so a client cannot tell the two endpoints apart by
    /// what they accept — the difference is what happens on a <em>retry</em>, and there the sha256
    /// answers `409 feed-duplicate` naming the version the first attempt created.
    /// </remarks>
    private static void RequireIdempotencyKey(HttpContext context)
    {
        var key = context.Request.Headers[MageRideHeaders.IdempotencyKey].ToString().Trim();

        if (key.Length == 0)
        {
            throw new MageRideException(
                MageRideErrors.IdempotencyKeyRequired,
                $"POST mutations require an {MageRideHeaders.IdempotencyKey} header (ULID or UUID).");
        }

        if (key.Length is < 16 or > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
        {
            throw new MageRideException(
                MageRideErrors.IdempotencyKeyInvalid,
                $"{MageRideHeaders.IdempotencyKey} must be 16-128 characters of [A-Za-z0-9_-].");
        }
    }

    /// <summary>
    /// Refuses an oversized upload before reading it, and raises Kestrel's own ceiling to ours.
    /// </summary>
    /// <remarks>
    /// Two guards for one rule, because they catch different clients. A declared
    /// <c>Content-Length</c> is refused immediately — there is no reason to read 400 MB to say no.
    /// A chunked upload declares nothing, so Kestrel's limit is raised to exactly BR-32.1's
    /// ceiling plus the multipart envelope and it terminates the request itself; the object store
    /// counts the file's own bytes as a third and final check.
    /// </remarks>
    private static void RequireWithinLimit(HttpContext context, long limit)
    {
        var ceiling = limit + MultipartOverheadBytes;

        if (context.Request.ContentLength is { } declared && declared > ceiling)
        {
            throw new MageRideException(
                MageRideErrors.PayloadTooLarge,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The upload declares {declared} bytes; the limit is {limit} (BR-32.1: 200 MB)."));
        }

        var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();

        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = ceiling;
        }
    }

    private static async Task<MultipartSection> ReadFileSectionAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType ||
            !MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType) ||
            !mediaType.MediaType.HasValue ||
            !mediaType.MediaType.Value!.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [FilePart] = ["Expected multipart/form-data with a `file` part carrying the GTFS zip."],
            });
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;

        if (string.IsNullOrEmpty(boundary))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [FilePart] = ["The multipart/form-data request declares no boundary."],
            });
        }

        // MultipartReader rather than ReadFormAsync: the form reader buffers the whole body to a
        // temp file before the handler sees a byte, so a 200 MB feed would be written to disk
        // twice — once by the form reader and once by the object store — and the 413 would come
        // after both.
        var reader = new MultipartReader(boundary, context.Request.Body);

        while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
        {
            if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) &&
                disposition.IsFileDisposition() &&
                string.Equals(
                    HeaderUtilities.RemoveQuotes(disposition.Name).Value, FilePart, StringComparison.OrdinalIgnoreCase))
            {
                return section;
            }
        }

        throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [FilePart] = ["The request carries no `file` part."],
        });
    }

    private static string FileName(MultipartSection section) =>
        ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
            ? HeaderUtilities.RemoveQuotes(disposition.FileName).Value ?? "feed.zip"
            : "feed.zip";

    private static string ToCsv(FeedValidationReport report)
    {
        var csv = new StringBuilder("severity,file,row,code,message\n");

        foreach (var issue in report.Errors)
        {
            Append(csv, "error", issue);
        }

        foreach (var issue in report.Warnings)
        {
            Append(csv, "warning", issue);
        }

        return csv.ToString();

        static void Append(StringBuilder csv, string severity, FeedIssue issue)
        {
            csv.Append(severity).Append(',')
                .Append(Quote(issue.File)).Append(',')
                .Append(issue.Row?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(Quote(issue.Code)).Append(',')
                .Append(Quote(issue.Message)).Append('\n');
        }

        // RFC 4180 quoting on the way out as well as on the way in: a GTFS message names ids the
        // feed chose, and a route id with a comma in it would otherwise shift every later column.
        static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
