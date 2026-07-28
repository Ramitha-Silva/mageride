using System.Text;
using MageRide.Provisioning.Bulk;
using MageRide.Provisioning.Configuration;
using MageRide.Provisioning.Domain;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace MageRide.Provisioning.Endpoints;

/// <summary>
/// <c>/v1/fleets/{fleetId}/trackers/bulk</c> — T-09, US-3.2 bulk IMEI onboarding.
/// </summary>
/// <remarks>
/// <para>
/// The upload is <c>multipart/form-data</c> with one <c>file</c> part, as D3' specifies. The body
/// size is capped before it is read: 5,000 rows of <c>imei,registrationNumber</c> is around 150 KB,
/// and a limit expressed in bytes is the only one that applies before the whole thing is in memory.
/// </para>
/// <para>
/// The error report hangs off the job rather than off a bucket — see <see cref="IErrorReportLinks"/>.
/// Its route takes no bearer, which is the point of a signed link, so it is mapped outside the
/// authenticated group.
/// </para>
/// </remarks>
public static class FleetTrackerEndpoints
{
    /// <summary>
    /// Bytes accepted for one upload.
    /// </summary>
    /// <remarks>
    /// 5,000 rows is about 150 KB. 2 MB leaves room for a file with long plates, quoting and CRLF
    /// endings while still refusing something that was never a tracker CSV — and refusing it at the
    /// pipe rather than after buffering it. A file that is under this and still over the row limit
    /// gets D3''s <c>413 too-many-rows</c> with the count in it, which is the answer an operator
    /// can act on.
    /// </remarks>
    internal const long MaxUploadBytes = 2 * 1024 * 1024;

    /// <summary>The form field D3' names.</summary>
    private const string FileField = "file";

    /// <summary>
    /// Optional form field choosing the credential type for the whole batch.
    /// </summary>
    /// <remarks>
    /// ⚠ Not in D3', which gives the bulk body a <c>file</c> and nothing else while making
    /// <c>credentialType</c> required on the single bind. A fleet is one hardware generation more
    /// often than not, so the choice belongs to the batch rather than to the row; it defaults to
    /// <c>x509</c>, which is what an MQTT-capable tracker needs and what D6' §4.1 lists as the
    /// current-firmware path. Raised as a C030 micro-change-set.
    /// </remarks>
    private const string CredentialTypeField = "credentialType";

    public static IEndpointRouteBuilder MapFleetTrackerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var fleets = endpoints.MapGroup("/v1/fleets/{fleetId}/trackers/bulk")
            .WithTags("fleet-trackers")
            .RequireMageRideRole(
                MageRideRoles.FleetOwner, MageRideRoles.Admin, MageRideRoles.SuperAdmin);

        fleets.MapPost("/", SubmitAsync)
            .WithName("bulkBindTrackers")
            .DisableAntiforgery();

        fleets.MapGet("/{jobId}", GetAsync).WithName("getBulkTrackerJob");

        // Anonymous by design: the signature in the query string is the credential, which is what
        // lets the Admin Portal hand the link straight to a browser download.
        endpoints.MapGet("/v1/fleets/{fleetId}/trackers/bulk/{jobId}/errors.csv", DownloadReportAsync)
            .WithName("downloadBulkTrackerErrorReport")
            .WithTags("fleet-trackers")
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task<Accepted<BulkJobResponse>> SubmitAsync(
        string fleetId,
        HttpContext context,
        IBulkTrackerService service,
        IErrorReportLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(links);

        var fleet = RequireFleetId(fleetId);

        if (!context.Request.HasFormContentType)
        {
            throw new MageRideException(
                MageRideErrors.CsvInvalid, "Expected multipart/form-data with a `file` part (D3').");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files[FileField]
                   ?? throw new MageRideException(MageRideErrors.CsvInvalid, "No `file` part in the upload.");

        if (file.Length > MaxUploadBytes)
        {
            throw new MageRideException(
                MageRideErrors.PayloadTooLarge,
                $"The upload is {file.Length} bytes; the limit is {MaxUploadBytes}.");
        }

        var credentialType = form.TryGetValue(CredentialTypeField, out var requested) && requested.Count > 0
            ? requested[0] ?? CredentialTypes.X509
            : CredentialTypes.X509;

        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var csv = await reader.ReadToEndAsync(cancellationToken);

        var job = await service.SubmitAsync(
            context.User.RequireSubjectId(), fleet, credentialType, csv, cancellationToken);

        return TypedResults.Accepted(
            $"/v1/fleets/{fleet}/trackers/bulk/{job.Id}",
            BulkJobResponse.From(job, links.Create(fleet, job.Id)));
    }

    private static async Task<Ok<BulkJobResponse>> GetAsync(
        string fleetId,
        string jobId,
        HttpContext context,
        IBulkTrackerService service,
        IErrorReportLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(links);

        var fleet = RequireFleetId(fleetId);
        var job = await service.GetAsync(context.User.RequireSubjectId(), fleet, RequireJobId(jobId), cancellationToken);

        return TypedResults.Ok(BulkJobResponse.From(job, links.Create(fleet, job.Id)));
    }

    private static async Task<IResult> DownloadReportAsync(
        string fleetId,
        string jobId,
        string? expires,
        string? signature,
        IBulkTrackerService service,
        IErrorReportLinks links,
        IOptions<ProvisioningOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(options);

        var fleet = RequireFleetId(fleetId);
        var job = RequireJobId(jobId);

        if (!links.Verify(fleet, job, expires, signature))
        {
            // 404, not 403. The link *is* the credential, so a bad or expired one has not proved
            // that the job exists — and answering 403 would confirm to somebody guessing job ids
            // that they had found a real one.
            throw new MageRideException(MageRideErrors.NotFound, "This report link is not valid or has expired.");
        }

        var report = await service.BuildErrorReportAsync(fleet, job, cancellationToken);

        return Results.File(
            Encoding.UTF8.GetBytes(report), "text/csv; charset=utf-8", $"tracker-bulk-{job}.csv");
    }

    private static Guid RequireFleetId(string? fleetId) =>
        Guid.TryParse(fleetId, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new MageRideException(
                MageRideErrors.Forbidden, "You are not an owner or manager of this fleet (AL-03).");

    private static Guid RequireJobId(string? jobId) =>
        Guid.TryParse(jobId, out var parsed)
            ? parsed
            : throw new MageRideException(MageRideErrors.NotFound, "No such bulk job.");
}
