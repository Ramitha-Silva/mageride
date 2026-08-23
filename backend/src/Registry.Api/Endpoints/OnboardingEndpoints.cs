using System.Text.Json;
using MageRide.Registry.Domain;
using MageRide.Registry.Onboarding;
using MageRide.Registry.Vehicles;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace MageRide.Registry.Endpoints;

/// <summary>
/// Profile Setup and the four-step Mode-C onboarding wizard (AL-27, AL-29, AL-30).
/// </summary>
/// <remarks>
/// <para>
/// <b>Profile Setup is not part of vehicle onboarding and does not live under
/// <c>/v1/vehicles</c>.</b> AL-27 splits driver onboarding in two: identity — name, required photo
/// and licence — precedes Home and needs no vehicle, and the four-step wizard is optional and
/// Mode-C only. A driver may sit at Home for a month with a profile and no vehicle, and the route
/// table says so.
/// </para>
/// <para>
/// Every route here requires the <c>driver</c> role, like the rest of <c>/v1/vehicles</c>.
/// </para>
/// </remarks>
public static class OnboardingEndpoints
{
    public static IEndpointRouteBuilder MapOnboardingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var drivers = endpoints.MapGroup("/v1/drivers")
            .WithTags("drivers")
            .RequireMageRideRole(MageRideRoles.Driver);

        // Δ MCS-05 — the boot router's read. Inside the driver-role group like the write beside
        // it: signing into the Driver App grants that role now, so the first thing a driver does
        // after OTP can ask this.
        drivers.MapGet("/profile", ReadProfileAsync).WithName("getDriverProfile");

        // Δ MCS-25 — the bytes behind the profile photo, for the header both apps draw.
        //
        // `AllowAnonymous` inside a group that requires the driver role, deliberately and for the
        // same reason support-svc's screenshot route is: the caller is an image loader, which
        // carries no bearer token. The link is what authorises, and it is unguessable and expiring.
        // A token in a query string would be a token in every proxy log between here and the phone.
        drivers.MapGet("/{driverId}/profile-photo", GetProfilePhotoAsync)
            .AllowAnonymous()
            .WithName("getDriverProfilePhoto");

        // Δ MCS-01 — `DisableAntiforgery` because the route now also takes multipart/form-data.
        // The token would be a browser-form defence on an endpoint only a bearer-authenticated
        // mobile client reaches, and its absence is what ASP.NET refuses a form over otherwise.
        drivers.MapPut("/profile", UpsertProfileAsync)
            .WithName("upsertDriverProfile")
            .DisableAntiforgery();

        // Δ AL-58/AL-59 — where a driver's swept earnings go, and the LankaQR a passenger scans to
        // pay them. Replaces D-11's merchant binding, which never existed (AL-57).
        drivers.MapGet("/payout-profile", ReadPayoutProfileAsync).WithName("getDriverPayoutProfile");
        drivers.MapPut("/payout-profile", UpsertPayoutProfileAsync).WithName("upsertDriverPayoutProfile");
        drivers.MapPost("/payout-profile/documents", UploadPayoutDocumentAsync)
            .WithName("uploadDriverPayoutDocument")
            .DisableAntiforgery();

        var vehicles = endpoints.MapGroup("/v1/vehicles")
            .WithTags("vehicles")
            .RequireMageRideRole(MageRideRoles.Driver);

        vehicles.MapPut("/{vehicleId}/onboarding/{step}", SaveStepAsync)
            .WithName("saveVehicleOnboardingStep")
            .DisableAntiforgery();
        vehicles.MapGet("/{vehicleId}/onboarding-status", GetStatusAsync).WithName("getVehicleOnboardingStatus");

        return endpoints;
    }

    /// <summary>
    /// <c>GET /v1/drivers/profile</c> — has this driver completed Profile Setup? (Δ MCS-05)
    /// </summary>
    /// <remarks>
    /// SCR-DA/DI-001 decides between Profile Setup and Home on this, and used to decide it on
    /// iam-svc's <c>first_name</c> — a column Profile Setup never writes. See
    /// <see cref="IOnboardingService.ReadProfileAsync"/>.
    /// </remarks>
    private static async Task<IResult> ReadProfileAsync(
        HttpContext context,
        IOnboardingService onboarding,
        IDriverPhotoLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(onboarding);
        ArgumentNullException.ThrowIfNull(links);

        var profile = await onboarding.ReadProfileAsync(context.User.RequireSubjectId(), cancellationToken);

        // `oneOf(DriverProfileSummary, null)` and a 200, which is what the contract types and what
        // ride-svc's two recovery reads already do: a driver with no profile is the normal answer
        // on a cold start, and a 404 is something an app shows as an error over the right
        // behaviour. `TypedResults.Ok(null)` writes nothing at all, so the literal is explicit.
        return profile is null
            ? TypedResults.Content("null", "application/json; charset=utf-8")
            : TypedResults.Ok(DriverProfileSummaryResponse.From(profile, links));
    }

    /// <summary>
    /// <c>GET /v1/drivers/{driverId}/profile-photo</c> — the bytes behind the signed link
    /// (Δ MCS-25).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anonymous, because the caller is an image loader and those carry no bearer token. The
    /// signature is the credential; <see cref="IDriverPhotoLinks"/> says why that is the right one.
    /// </para>
    /// <para>
    /// <b>Every refusal is the same refusal.</b> A malformed id, a forged signature, an expired
    /// link, a driver who does not exist and a driver with no photo all answer identically.
    /// Distinguishing them would let somebody holding a link they cannot use learn which driver ids
    /// are real, and "that driver exists" is itself something a forged link should not be able to
    /// ask.
    /// </para>
    /// </remarks>
    private static async Task<IResult> GetProfilePhotoAsync(
        string driverId,
        string? expires,
        string? signature,
        IOnboardingService onboarding,
        IDriverPhotoLinks links,
        IObjectStore objects,
        IOptions<RegistryOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onboarding);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(options);

        if (!Guid.TryParse(driverId, out var id) || !links.Verify(id, expires, signature))
        {
            throw new MageRideException(MageRideErrors.Forbidden, "That link is not valid.");
        }

        var profile = await onboarding.ReadProfileAsync(id, cancellationToken);
        var storageUrl = profile?.Profile.PhotoUrl;

        if (string.IsNullOrWhiteSpace(storageUrl))
        {
            throw new MageRideException(MageRideErrors.Forbidden, "That link is not valid.");
        }

        var opened = await objects.ReadAsync(storageUrl, cancellationToken);

        if (opened is not { } file)
        {
            // Δ D-36: on a bucket the bytes are not this process's to stream, so the caller is sent
            // on to a short-lived presigned URL. The signature was checked above, so this redirect
            // only ever reaches somebody who already proved the link.
            if (objects.TryPresign(storageUrl, options.Value.ProfilePhotoLinkTtl, out var direct))
            {
                return TypedResults.Redirect(direct, permanent: false, preserveMethod: false);
            }

            // On a filesystem store this means the pod that wrote the photo is gone.
            return TypedResults.NotFound();
        }

        return TypedResults.File(file.Bytes.ToArray(), file.ContentType);
    }

    /// <summary>
    /// <c>GET /v1/drivers/payout-profile</c> — the version the driver is looking at (AL-58).
    /// </summary>
    private static async Task<Ok<DriverPayoutProfileResponse>> ReadPayoutProfileAsync(
        HttpContext context, IDriverPayoutProfileService profiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);

        var profile = await profiles.ReadAsync(context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(DriverPayoutProfileResponse.From(profile));
    }

    /// <summary>
    /// <c>PUT /v1/drivers/payout-profile</c> — set or change the bank details (AL-58).
    /// </summary>
    /// <remarks>
    /// Always the caller's own: the subject comes from the token and there is no path parameter, so
    /// there is no route by which one driver could write another's bank account.
    /// </remarks>
    private static async Task<Ok<DriverPayoutProfileResponse>> UpsertPayoutProfileAsync(
        DriverPayoutProfileBody? body,
        HttpContext context,
        IDriverPayoutProfileService profiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);

        var saved = await profiles.UpsertAsync(
            context.User.RequireSubjectId(),
            new DriverPayoutDraft(
                body?.Bank?.Trim() ?? string.Empty,
                body?.Branch?.Trim() ?? string.Empty,
                body?.AccountNo?.Trim() ?? string.Empty,
                body?.AccountHolderName?.Trim() ?? string.Empty),
            cancellationToken);

        return TypedResults.Ok(DriverPayoutProfileResponse.From(saved));
    }

    /// <summary>
    /// <c>POST /v1/drivers/payout-profile/documents</c> — proof of account, or the driver's own
    /// LankaQR (AL-58/AL-59).
    /// </summary>
    /// <remarks>
    /// The bytes are written before the <c>docs.uploads</c> row, which is fleet-svc's rule for the
    /// same slots and for the same reason: a crash between them leaves an orphan file that NFR-28's
    /// deadline sweeps, while the other order leaves a profile pointing at a document the officer is
    /// told exists and cannot open.
    /// </remarks>
    private static async Task<Created<DriverPayoutDocumentResponse>> UploadPayoutDocumentAsync(
        HttpContext context,
        IDriverPayoutProfileService profiles,
        IPayoutDocumentStore documents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(documents);

        if (!context.Request.HasFormContentType)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["file"] = ["The request must be multipart/form-data carrying `kind` and `file`."],
            });
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var kind = form["kind"].ToString().Trim();
        var file = form.Files.GetFile("file");

        if (file is null || file.Length == 0)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["file"] = ["file is required and must not be empty."],
            });
        }

        var driverId = context.User.RequireSubjectId();

        await using var content = file.OpenReadStream();

        var uploadId = await documents.WriteAsync(
            driverId,
            kind,
            content,
            // What the client said it is. Recorded rather than trusted: it decides the Content-Type
            // the officer's browser is handed back, and nothing branches on it.
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            cancellationToken);

        await profiles.AttachAsync(driverId, uploadId, kind, cancellationToken);

        return TypedResults.Created(
            (string?)null, new DriverPayoutDocumentResponse(uploadId.ToString(), kind));
    }

    /// <summary>
    /// <c>PUT /v1/drivers/profile</c> — Profile Setup, as JSON upload ids or as the images
    /// themselves (SCR-DA/DI-003a, AL-27).
    /// </summary>
    /// <remarks>
    /// The body is read by hand rather than bound, because this operation declares **two** media
    /// types and a bound complex parameter would answer `415` to the multipart one before the
    /// handler ran. `saveVehicleOnboardingStep` is the same shape for the same reason.
    /// </remarks>
    private static async Task<Ok<DriverProfileResponse>> UpsertProfileAsync(
        HttpContext context,
        IOnboardingService onboarding,
        IOnboardingDocumentStore documents,
        IDriverPhotoLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(onboarding);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(links);

        var driverId = context.User.RequireSubjectId();

        var body = context.Request.HasFormContentType
            ? await ReadProfileFormAsync(context, driverId, documents, cancellationToken)
            : await ReadJsonBodyAsync<UpsertDriverProfileBody>(context, cancellationToken);

        var result = await onboarding.UpsertProfileAsync(
            new UpsertDriverProfileCommand(
                driverId,
                body?.DriverName,
                body?.ProfilePhotoFileId,
                body?.LicenseFrontFileId,
                body?.LicenseBackFileId,
                body?.NicNo,
                body?.AllowedVehicleTypes,
                body?.LicenceNo,
                body?.LicenceExpiry),
            cancellationToken);

        return TypedResults.Ok(DriverProfileResponse.From(result, links));
    }

    /// <inheritdoc cref="UpsertProfileAsync" path="/remarks"/>
    private static async Task<Ok<SaveOnboardingStepResponse>> SaveStepAsync(
        string vehicleId,
        string step,
        HttpContext context,
        IOnboardingService onboarding,
        IOnboardingDocumentStore documents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(onboarding);
        ArgumentNullException.ThrowIfNull(documents);

        var driverId = context.User.RequireSubjectId();

        var body = context.Request.HasFormContentType
            ? await ReadStepFormAsync(context, driverId, step, documents, cancellationToken)
            : await ReadJsonBodyAsync<OnboardingStepBody>(context, cancellationToken);

        var state = await onboarding.SaveStepAsync(
            new SaveOnboardingStepCommand(
                driverId,
                VehicleEndpoints.RequireVehicleId(vehicleId),
                step,
                body?.RegistrationNumber,
                body?.VehicleType,
                body?.FileId,
                body?.FileIdBack,
                body?.Fields),
            cancellationToken);

        return TypedResults.Ok(SaveOnboardingStepResponse.From(state, step));
    }

    // -------------------------------------------------------------------------------------------
    // Δ MCS-01 — the multipart arms. Each stores its parts and hands the rest of the pipeline the
    // same body the JSON arm produces, so `OnboardingService` sees one shape and every AL-29/AL-30
    // verdict rule stays where it was.
    //
    // The bytes are stored BEFORE the service checks anything, so a request the service then
    // rejects — a vehicle the caller does not own, a step name that is not one of the four — leaves
    // an object nobody references. NFR-28's deadline reclaims it, which is the same trade
    // fleet-svc's document upload and this file's payout upload already make, and the alternative
    // is buffering an 8 MiB image in memory to find out.
    // -------------------------------------------------------------------------------------------

    private static async Task<UpsertDriverProfileBody> ReadProfileFormAsync(
        HttpContext context,
        Guid driverId,
        IOnboardingDocumentStore documents,
        CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);

        var photo = await StorePartAsync(
            form, "photo", OnboardingUploadKinds.ProfilePhoto, driverId, documents, cancellationToken);
        var front = await StorePartAsync(
            form, "licenseFront", DocumentKinds.DrivingLicense, driverId, documents, cancellationToken);
        var back = await StorePartAsync(
            form, "licenseBack", DocumentKinds.DrivingLicense, driverId, documents, cancellationToken);

        return new UpsertDriverProfileBody(
            form["driverName"].ToString(),
            photo,
            front,
            back,
            NullIfBlank(form["nicNo"].ToString()),
            ReadList(form, "allowedVehicleTypes"),
            NullIfBlank(form["licenceNo"].ToString()),
            NullIfBlank(form["licenceExpiry"].ToString()));
    }

    private static async Task<OnboardingStepBody> ReadStepFormAsync(
        HttpContext context,
        Guid driverId,
        string step,
        IOnboardingDocumentStore documents,
        CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);

        // `details` carries no document — its multipart arm is the type and the plate. The other
        // three each save one, and `photos` saves two.
        var kind = OnboardingSteps.DocumentKind(step);

        // Δ MCS-02 — a corrections-only save carries no file, and must not. BR-25.3 lets a driver
        // edit a doubtful extracted value, and asking them to re-photograph the document to retype
        // its expiry is the roadside experience that rule exists to avoid. The service is what
        // decides whether the step actually HAS a document to correct; refusing here would refuse
        // the legitimate case as well.
        var corrections = ReadCorrections(form);

        if (kind is not null && form.Files.GetFile("file") is null && corrections.Count == 0)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["file"] = ["file is required on this step unless the request carries a correction."],
            });
        }

        var file = kind is null || form.Files.GetFile("file") is null
            ? null
            : await StorePartAsync(form, "file", kind, driverId, documents, cancellationToken);

        // Only step 4 has a back, and the service is what insists on it (D5' §14.1a: one photo
        // cannot show a vehicle's front and back plates at once).
        var fileBack = form.Files.GetFile("fileBack") is null || kind is null
            ? null
            : await StorePartAsync(form, "fileBack", kind, driverId, documents, cancellationToken);

        return new OnboardingStepBody(
            NullIfBlank(form["registrationNumber"].ToString()),
            NullIfBlank(form["vehicleType"].ToString()),
            file,
            fileBack,
            corrections.Count == 0 ? null : corrections);
    }

    /// <summary>
    /// The driver corrections a step form may carry (Δ MCS-02, AL-29 / BR-25.3).
    /// </summary>
    /// <remarks>
    /// Named parts rather than a free map: <see cref="DocumentFieldKeys.AcceptedFor"/> already
    /// fixes which keys a document kind accepts, and the service filters against it again — so a
    /// key that does not belong to the step is dropped there rather than stored here. Listing them
    /// keeps the contract able to say what it accepts, which a `Dictionary&lt;string,string&gt;`
    /// over `multipart/form-data` cannot.
    /// </remarks>
    private static Dictionary<string, string> ReadCorrections(IFormCollection form)
    {
        var corrections = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string part, string key)
        {
            var value = NullIfBlank(form[part].ToString());
            if (value is not null)
            {
                corrections[key] = value;
            }
        }

        Add("insuranceExpiry", DocumentFieldKeys.InsuranceExpiry);
        Add("insurancePolicyNo", DocumentFieldKeys.InsurancePolicyNo);
        Add("revenueNo", DocumentFieldKeys.RevenueNo);
        Add("revenueExpiry", DocumentFieldKeys.RevenueExpiry);

        return corrections;
    }

    /// <summary>Stores one file part and answers its <c>docs.uploads</c> id, as a string.</summary>
    private static async Task<string?> StorePartAsync(
        IFormCollection form,
        string part,
        string kind,
        Guid driverId,
        IOnboardingDocumentStore documents,
        CancellationToken cancellationToken)
    {
        var file = form.Files.GetFile(part);

        if (file is null)
        {
            return null;
        }

        if (file.Length == 0)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [part] = [$"{part} must not be empty."],
            });
        }

        await using var content = file.OpenReadStream();

        // Per part, not per request. A driver scans the licence through SCR-DA/DI-005 and picks the
        // avatar out of the gallery in the same submission, so one `capturedVia` for the whole form
        // would be wrong about one of them — and AL-43's whole value is that the officer can tell.
        var uploadId = await documents.WriteAsync(
            driverId,
            kind,
            form[$"{part}CapturedVia"].ToString().Trim(),
            content,
            // Recorded rather than trusted: it decides the Content-Type an officer's browser is
            // handed back, and nothing branches on it.
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            cancellationToken);

        return uploadId.ToString();
    }

    /// <summary>
    /// Reads the JSON arm, when there is one.
    /// </summary>
    /// <remarks>
    /// A request with no body — or one whose content type is neither JSON nor a form — resolves to
    /// <see langword="null"/> and falls through to the service's own "driverName is required".
    /// That is what the bound parameter did before this route took two media types, and the tests
    /// that assert those messages are asserting the service's rule, not the binder's.
    /// </remarks>
    private static async Task<T?> ReadJsonBodyAsync<T>(HttpContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType())
        {
            return default;
        }

        try
        {
            return await context.Request.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (JsonException cause)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["body"] = [$"The request body is not valid JSON: {cause.Message}"],
            });
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// A repeated form field, or one field carrying a comma-separated list.
    /// </summary>
    /// <remarks>
    /// Both because both are written in the wild: `multipart/form-data` has no array type, so a
    /// client either repeats the field or joins it, and refusing one of the two would be a rule
    /// nobody can read off the contract.
    /// </remarks>
    private static IReadOnlyList<string>? ReadList(IFormCollection form, string key)
    {
        var values = form[key]
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries))
            .ToArray();

        return values.Length == 0 ? null : values;
    }

    private static async Task<Ok<OnboardingStatusResponse>> GetStatusAsync(
        string vehicleId, HttpContext context, IOnboardingService onboarding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(onboarding);

        var state = await onboarding.GetStateAsync(
            context.User.RequireSubjectId(), VehicleEndpoints.RequireVehicleId(vehicleId), cancellationToken);

        return TypedResults.Ok(OnboardingStatusResponse.From(state));
    }
}
