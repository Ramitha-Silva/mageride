using System.Globalization;
using System.Text.Json;
using MageRide.Registry.Configuration;
using MageRide.Registry.Domain;
using MageRide.Registry.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.Shared.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Registry.Onboarding;

/// <summary>Body of <c>PUT /v1/drivers/profile</c> — Profile Setup (SCR-DA/DI-003a, AL-27).</summary>
public sealed record UpsertDriverProfileCommand(
    Guid DriverId,
    string? DriverName,
    string? ProfilePhotoFileId,
    string? LicenseFrontFileId,
    string? LicenseBackFileId,
    string? NicNo,
    IReadOnlyList<string>? AllowedVehicleTypes);

/// <summary>
/// What Profile Setup produced: the stored profile plus every field and where it came from.
/// </summary>
/// <param name="Status">
/// <c>PENDING</c> while any field is waiting on a Verification Officer, <c>APPROVED</c> once none
/// is. D3' types this as a <c>RegistrationStatus</c>, which is the vehicle vocabulary; for a
/// driver it is read as "has this identity been checked".
/// </param>
public sealed record DriverProfileResult(DriverProfile Profile, string Status, IReadOnlyList<DocumentField> Fields);

/// <summary>Body of <c>PUT /v1/vehicles/{vehicleId}/onboarding/{step}</c> (AL-30).</summary>
/// <param name="Fields">
/// Driver-entered corrections. Each lands <c>source='manual'</c>, <c>verify_status='pending'</c>
/// and takes its step to <c>pending_review</c> — BR-25.2 lets the driver proceed and trusts the
/// value only once an officer confirms it.
/// </param>
public sealed record SaveOnboardingStepCommand(
    Guid DriverId,
    Guid VehicleId,
    string Step,
    string? RegistrationNumber,
    string? VehicleType,
    string? FileId,
    string? FileIdBack,
    IReadOnlyDictionary<string, string>? Fields);

/// <summary>
/// Everything <c>GET /v1/vehicles/{id}/onboarding-status</c> and the step-save response need.
/// </summary>
/// <param name="Steps">All four steps, always — an unsaved one reads <c>PENDING_INPUT</c>.</param>
/// <param name="NextStep">
/// The first step that is not <c>VERIFIED</c>, or <see langword="null"/> when all four are. This
/// is AL-30's resume rule and the reason the wizard never reopens at Step 1.
/// </param>
/// <param name="OcrJobId">The extraction ocr-svc queued, when it queued one. Absent otherwise.</param>
public sealed record OnboardingState(
    Vehicle Vehicle,
    IReadOnlyDictionary<string, string> Steps,
    string? NextStep,
    IReadOnlyList<DocumentField> Fields,
    Guid? OcrJobId = null)
{
    /// <summary>The verdict of one step.</summary>
    public string StepStatus(string step) => Steps.TryGetValue(step, out var status) ? status : StepVerdicts.PendingInput;
}

/// <summary>
/// Driver-identity Profile Setup and the four-step Mode-C vehicle onboarding machine
/// (AL-27, AL-29, AL-30).
/// </summary>
public interface IOnboardingService
{
    Task<DriverProfileResult> UpsertProfileAsync(UpsertDriverProfileCommand command, CancellationToken cancellationToken);

    Task<OnboardingState> SaveStepAsync(SaveOnboardingStepCommand command, CancellationToken cancellationToken);

    Task<OnboardingState> GetStateAsync(Guid driverId, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>
    /// Re-derives every saved step from its fields and re-applies the AL-30 approval rule, without
    /// taking any new input.
    /// </summary>
    /// <remarks>
    /// The seam a Verification Officer's Confirm needs (SCR-AP-003, AL-30: "Approve unlocks only
    /// when all Pending fields are confirmed"). admin-bff (C062) writes
    /// <c>registry.document_fields.verify_status='confirmed'</c> and calls this; without it the
    /// vehicle would sit at <c>pending_review</c> for a field that is no longer pending.
    /// </remarks>
    Task<OnboardingState> RecomputeAsync(Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IOnboardingService"/>
public sealed class OnboardingService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IVehicleRepository vehicles,
    IDriverProfileRepository profiles,
    IDocumentRepository documents,
    IOnboardingStepRepository steps,
    IDocumentExtractionClient extraction,
    IOutboxWriter outbox,
    IOptions<RegistryOptions> options,
    TimeProvider clock,
    ILogger<OnboardingService> logger) : IOnboardingService
{
    /// <summary><c>registry.yaml</c>'s <c>maxLength</c> on the Profile Setup name.</summary>
    private const int MaxDriverNameLength = 200;

    /// <summary><c>registry.yaml</c>'s <c>maxLength</c> on <c>nicNo</c>.</summary>
    private const int MaxNicLength = 20;

    private readonly RegistryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<DriverProfileResult> UpsertProfileAsync(
        UpsertDriverProfileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var displayName = RequireDriverName(command.DriverName);
        var nicNo = RequireNic(command.NicNo);
        var allowedTypes = RequireAllowedVehicleTypes(command.AllowedVehicleTypes);

        // AL-27: "name + **required** photo + driving-license front/back". All three are required
        // at this screen, and a profile written without the photo would send a driver to Home with
        // nothing for a passenger to recognise them by (US-2.12).
        var photo = await RequireUploadAsync(command.DriverId, command.ProfilePhotoFileId, "profilePhotoFileId", cancellationToken);
        var front = await RequireUploadAsync(command.DriverId, command.LicenseFrontFileId, "licenseFrontFileId", cancellationToken);
        var back = await RequireUploadAsync(command.DriverId, command.LicenseBackFileId, "licenseBackFileId", cancellationToken);

        // Outside the transaction: extraction is a network call to another service, and holding a
        // Postgres transaction open across it would put ocr-svc's latency on registry's connection
        // pool. Nothing is written until every document has come back.
        var prepared = new List<PreparedDocument>
        {
            await PrepareAsync(front, DocumentKinds.DrivingLicense, DocumentSides.Front, null, cancellationToken),
            await PrepareAsync(back, DocumentKinds.DrivingLicense, DocumentSides.Back, null, cancellationToken),
        };

        // A driver-supplied value is the driver correcting an unclear scan, so it replaces what
        // was read and carries manual provenance (AL-29, US-2.4a).
        var manual = new Dictionary<string, string>(StringComparer.Ordinal);
        if (nicNo is not null)
        {
            manual[DocumentFieldKeys.NicNo] = nicNo;
        }

        if (allowedTypes is not null)
        {
            manual[DocumentFieldKeys.AllowedVehicleTypes] = string.Join(',', allowedTypes);
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var written = new List<DocumentField>();

        foreach (var document in prepared)
        {
            // The manual override goes on whichever side the field belongs to, so a corrected NIC
            // lands beside the licence number it was read with rather than on an unrelated scan.
            var applicable = manual
                .Where(entry => DocumentFieldKeys.AcceptedFor(document.Kind, document.Side).Contains(entry.Key))
                .ToDictionary(StringComparer.Ordinal);

            var (_, fields) = await WriteDocumentAsync(
                unitOfWork, command.DriverId, vehicleId: null, document, applicable, cancellationToken);

            written.AddRange(fields);
        }

        var pending = written.Where(field => field.IsPending).ToArray();
        var verifiedAt = pending.Length == 0 ? clock.GetUtcNow() : (DateTimeOffset?)null;

        var profile = await UpsertProfileRowAsync(
            unitOfWork,
            command.DriverId,
            displayName,
            photo.StorageUrl,
            nicNo ?? ValueOf(written, DocumentFieldKeys.NicNo),
            allowedTypes ?? SplitTypes(ValueOf(written, DocumentFieldKeys.AllowedVehicleTypes)),
            verifiedAt,
            cancellationToken);

        // US-2.4a: a manual or doubtful identity field goes to the same officer queue a vehicle's
        // does. Without this the driver reaches Home — AL-27 lets them — with a NIC nobody will
        // ever look at.
        if (pending.Length > 0)
        {
            await outbox.WriteAsync(
                unitOfWork,
                OnboardingEvents.ReviewRequired(
                    null,
                    command.DriverId,
                    OnboardingEvents.ProfileStep,
                    [.. pending.Select(field => field.FieldKey).Distinct(StringComparer.Ordinal)]),
                cancellationToken);
        }

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Profile Setup stored for driver {DriverId}: {FieldCount} fields, {PendingCount} pending review",
            command.DriverId, written.Count, pending.Length);

        return new DriverProfileResult(
            profile,
            pending.Length == 0 ? RegistrationStatuses.Approved : RegistrationStatuses.Pending,
            written);
    }

    public async Task<OnboardingState> SaveStepAsync(
        SaveOnboardingStepCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var step = RequireStep(command.Step);

        Vehicle vehicle;

        await using (var connection = await connectionFactory.OpenAsync(cancellationToken))
        {
            vehicle = await RequireOwnedVehicleAsync(connection, null, command.DriverId, command.VehicleId, cancellationToken);
        }

        RequireOnboardable(vehicle);

        return step == OnboardingSteps.Details
            ? await SaveDetailsStepAsync(command, vehicle, cancellationToken)
            : await SaveDocumentStepAsync(command, step, vehicle, cancellationToken);
    }

    public async Task<OnboardingState> GetStateAsync(
        Guid driverId, Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var vehicle = await RequireOwnedVehicleAsync(connection, null, driverId, vehicleId, cancellationToken);

        return await ReadStateAsync(connection, null, vehicle, cancellationToken);
    }

    public async Task<OnboardingState> RecomputeAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var vehicle = await vehicles.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, vehicleId, cancellationToken)
            ?? throw new MageRideException(MageRideErrors.VehicleNotFound, $"No vehicle {vehicleId}.");

        // Mode C only, like the wizard this re-derives. A Fleet Portal vehicle's approval is
        // SCR-FP-004's and has no `registry.onboarding_steps` rows to derive anything from, so
        // running the AL-30 rule over it would say "not all four steps verified" about a vehicle
        // that never had four steps.
        RequireOnboardable(vehicle);

        var state = await SettleAsync(unitOfWork, vehicle, emitReviewFor: null, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return state;
    }

    // -------------------------------------------------------------------------------------------
    // Step 1/4 — vehicle details. Driver-entered, so entering it IS the verification (D5' §14.1a).
    // -------------------------------------------------------------------------------------------

    private async Task<OnboardingState> SaveDetailsStepAsync(
        SaveOnboardingStepCommand command, Vehicle vehicle, CancellationToken cancellationToken)
    {
        // Both fields are optional on the request and default to what the vehicle already carries:
        // POST /v1/vehicles has already taken them once, and a driver stepping back through the
        // wizard to change only the type must not have to retype the plate.
        var registrationNumber = command.RegistrationNumber is null
            ? vehicle.RegistrationNumber
            : NormaliseRegistration(command.RegistrationNumber);

        var vehicleType = command.VehicleType ?? vehicle.VehicleType;
        RequireDriverAppVehicleType(vehicleType);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var updated = await vehicles.UpdateDetailsAsync(
            unitOfWork.Connection, unitOfWork.Transaction, vehicle.Id, registrationNumber, vehicleType, cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.RegistrationExists,
                $"Registration {registrationNumber} is already held by a live vehicle (D-37).");

        await steps.SaveAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            vehicle.Id,
            OnboardingSteps.Details,
            StepStatuses.Verified,
            Serialize(new { registrationNumber, vehicleType }),
            cancellationToken);

        // Editing the plate invalidates a photos step that matched the old one. Left alone, a
        // vehicle could be approved with front and back photos of a different registration —
        // which is the single thing step 4 exists to rule out.
        if (!string.Equals(registrationNumber, vehicle.RegistrationNumber, StringComparison.Ordinal))
        {
            await RejudgePlateMatchAsync(unitOfWork, vehicle.Id, registrationNumber, cancellationToken);
        }

        var state = await SettleAsync(unitOfWork, updated, OnboardingSteps.Details, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return state;
    }

    // -------------------------------------------------------------------------------------------
    // Steps 2–4 — one or two uploaded documents, extracted and judged.
    // -------------------------------------------------------------------------------------------

    private async Task<OnboardingState> SaveDocumentStepAsync(
        SaveOnboardingStepCommand command, string step, Vehicle vehicle, CancellationToken cancellationToken)
    {
        var kind = OnboardingSteps.DocumentKind(step)!;
        var uploads = new List<(PendingUpload Upload, string? Side)>();

        if (step == OnboardingSteps.Photos)
        {
            // D5' §14.1a's step 4 is "Front & back photos", and the plate has to be legible on
            // both — one photo cannot show a vehicle's front and back plates at once.
            uploads.Add((await RequireUploadAsync(vehicle.OwnerId, command.FileId, "fileId", cancellationToken), DocumentSides.Front));
            uploads.Add((await RequireUploadAsync(vehicle.OwnerId, command.FileIdBack, "fileIdBack", cancellationToken), DocumentSides.Back));
        }
        else
        {
            uploads.Add((await RequireUploadAsync(vehicle.OwnerId, command.FileId, "fileId", cancellationToken), null));
        }

        var prepared = new List<PreparedDocument>();

        foreach (var (upload, side) in uploads)
        {
            prepared.Add(await PrepareAsync(upload, kind, side, vehicle.RegistrationNumber, cancellationToken));
        }

        var manual = RequireManualFields(command.Fields);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var documentIds = new List<Guid>(prepared.Count);
        var written = new List<DocumentField>();

        foreach (var document in prepared)
        {
            var (documentId, fields) = await WriteDocumentAsync(
                unitOfWork, vehicle.OwnerId, vehicle.Id, document, manual, cancellationToken);

            documentIds.Add(documentId);
            written.AddRange(fields);
        }

        // The step records which documents it saved, and the verdict is derived from those rows
        // alone. A re-upload after a failed extraction therefore supersedes cleanly: the previous
        // attempt's pending fields stay in the audit trail without holding the step down forever.
        await steps.SaveAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            vehicle.Id,
            step,
            DeriveStepStatus(step, written),
            Serialize(new { fileIds = uploads.Select(entry => entry.Upload.Id), documentIds }),
            cancellationToken);

        if (step == OnboardingSteps.Photos)
        {
            await vehicles.UpdateVehiclePhotoAsync(
                unitOfWork.Connection, unitOfWork.Transaction, vehicle.Id, uploads[0].Upload.StorageUrl, cancellationToken);
        }

        var refreshed = await vehicles.FindAsync(
            unitOfWork.Connection, unitOfWork.Transaction, vehicle.Id, cancellationToken) ?? vehicle;

        var state = await SettleAsync(unitOfWork, refreshed, step, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return state with { OcrJobId = prepared.Select(document => document.Extraction.JobId).FirstOrDefault(id => id is not null) };
    }

    /// <summary>
    /// Writes one document and every field belonging to it, and returns those fields.
    /// </summary>
    /// <remarks>
    /// A required key extraction did not return is written anyway — null value, <c>source='ai'</c>,
    /// <c>verify_status='pending'</c> — so the officer queue shows "insurance expiry could not be
    /// read" as a row to fill rather than as an absence they have to notice.
    /// </remarks>
    private async Task<(Guid DocumentId, IReadOnlyList<DocumentField> Fields)> WriteDocumentAsync(
        IUnitOfWork unitOfWork,
        Guid driverId,
        Guid? vehicleId,
        PreparedDocument document,
        IReadOnlyDictionary<string, string> manual,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var values = new Dictionary<string, DocumentField>(StringComparer.Ordinal);
        var order = new List<string>();

        void Put(string key, string? value, decimal? confidence, string source)
        {
            if (!values.ContainsKey(key))
            {
                order.Add(key);
            }

            values[key] = new DocumentField(
                Guid.Empty,
                Guid.Empty,
                key,
                value,
                // ck_document_fields_manual_confidence: a hand-typed value carries no confidence,
                // because a number invented for something nobody scanned would read as evidence.
                source == FieldSources.Manual ? null : confidence,
                source,
                DeriveVerifyStatus(key, value, confidence, source),
                null,
                null);
        }

        foreach (var field in document.Extraction.Fields)
        {
            Put(field.Key, field.Value, field.Confidence, FieldSources.Ai);
        }

        foreach (var (key, value) in manual)
        {
            Put(key, value, null, FieldSources.Manual);
        }

        foreach (var key in DocumentFieldKeys.RequiredFor(document.Kind, document.Side))
        {
            if (!values.TryGetValue(key, out var existing) || string.IsNullOrWhiteSpace(existing.FieldValue))
            {
                Put(key, null, null, FieldSources.Ai);
            }
        }

        var expiresAt = ResolveExpiry(document.Kind, values);

        var row = await documents.CreateAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            driverId,
            vehicleId,
            document.Kind,
            document.Upload.StorageUrl,
            expiresAt,
            // A certificate whose extracted expiry is already past is stored as EXPIRED on the
            // spot rather than waiting for tonight's sweep to notice — the driver uploaded it
            // today and must not be told it is fine until midnight.
            expiresAt is not null && expiresAt <= now ? DocumentStatuses.Expired : DocumentStatuses.Valid,
            cancellationToken);

        var fields = order.Select(key => values[key] with { DocumentId = row.Id }).ToArray();

        await documents.AddFieldsAsync(unitOfWork.Connection, unitOfWork.Transaction, row.Id, fields, cancellationToken);

        return (row.Id, fields);
    }

    // -------------------------------------------------------------------------------------------
    // AL-30's derivations: step verdict, resume point, onboarding_status, auto-approval.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Re-derives every saved step from its own fields, applies AL-30's approval rule and E-03's
    /// dispatch gate, and queues whatever events those transitions owe.
    /// </summary>
    /// <param name="emitReviewFor">
    /// The step just saved, if any. Only that step's move into <c>pending_review</c> is announced
    /// to the officer queue — a recompute that changed nothing must not re-queue work.
    /// </param>
    private async Task<OnboardingState> SettleAsync(
        IUnitOfWork unitOfWork, Vehicle vehicle, string? emitReviewFor, CancellationToken cancellationToken)
    {
        var connection = unitOfWork.Connection;
        var transaction = unitOfWork.Transaction;
        var events = new List<OutboxRecord>();

        var saved = await steps.ListAsync(connection, transaction, vehicle.Id, cancellationToken);
        var verdicts = new Dictionary<string, string>(StringComparer.Ordinal);
        var allFields = new List<DocumentField>();

        foreach (var stepName in OnboardingSteps.All)
        {
            var row = saved.FirstOrDefault(entry => entry.Step == stepName);

            if (row is null)
            {
                verdicts[stepName] = StepVerdicts.PendingInput;
                continue;
            }

            var fields = await documents.ListFieldsAsync(
                connection, transaction, DocumentIdsOf(row), cancellationToken);

            allFields.AddRange(fields);

            var status = DeriveStepStatus(stepName, fields);
            verdicts[stepName] = ToVerdict(status);

            if (status != row.Status)
            {
                await steps.SetStatusAsync(connection, transaction, vehicle.Id, stepName, status, cancellationToken);
            }

            if (status == StepStatuses.PendingReview && stepName == emitReviewFor)
            {
                events.Add(OnboardingEvents.ReviewRequired(
                    vehicle.Id,
                    vehicle.OwnerId,
                    stepName,
                    [.. fields.Where(field => field.IsPending).Select(field => field.FieldKey).Distinct(StringComparer.Ordinal)]));
            }
        }

        var settled = await ApplyApprovalAsync(
            unitOfWork, vehicle, verdicts, saved.Count > 0, events, cancellationToken);
        settled = await RefreshDispatchStateAsync(unitOfWork, settled, events, cancellationToken);

        if (events.Count > 0)
        {
            await outbox.WriteAsync(unitOfWork, events, cancellationToken);
        }

        return new OnboardingState(settled, verdicts, ResolveNextStep(verdicts), allFields);
    }

    /// <summary>AL-27's auto-approval and AL-30's derived <c>onboarding_status</c>.</summary>
    /// <param name="hasSavedSteps">
    /// Whether this vehicle was onboarded through the wizard at all. A vehicle with no saved steps
    /// has nothing to derive from, and its <c>onboarding_status</c> belongs to whoever did approve
    /// it — a Fleet Portal vehicle (AL-50) would otherwise be marked Incomplete by a rule about a
    /// wizard it never went through.
    /// </param>
    private async Task<Vehicle> ApplyApprovalAsync(
        IUnitOfWork unitOfWork,
        Vehicle vehicle,
        IReadOnlyDictionary<string, string> verdicts,
        bool hasSavedSteps,
        List<OutboxRecord> events,
        CancellationToken cancellationToken)
    {
        var complete = OnboardingSteps.All.All(step => verdicts[step] == StepVerdicts.Verified);

        if (!complete)
        {
            // Derived, so it comes back down as well as up. `status` deliberately does NOT: a
            // driver whose renewal upload came back blurry is Incomplete on My Vehicles and still
            // on the road, because the certificate they are carrying has not lapsed. E-03 is what
            // takes them off it when one actually does.
            if (hasSavedSteps && vehicle.OnboardingStatus == OnboardingStatuses.Approved)
            {
                await vehicles.SetOnboardingStatusAsync(
                    unitOfWork.Connection, unitOfWork.Transaction, vehicle.Id, OnboardingStatuses.Incomplete, cancellationToken);

                return vehicle with { OnboardingStatus = OnboardingStatuses.Incomplete };
            }

            return vehicle;
        }

        // AL-10, checked here rather than trusted from the steps: insurance and the revenue
        // licence are mandatory for every mode and a vehicle "cannot transition to APPROVED
        // without them (enforced in registry-svc)". A verified step whose document has since
        // expired must not be able to approve.
        var missing = await MissingMandatoryDocumentsAsync(unitOfWork, vehicle.Id, cancellationToken);

        if (missing.Count > 0)
        {
            logger.LogWarning(
                "Vehicle {VehicleId} has all four onboarding steps verified but is held at {Status} by AL-10: {Missing}",
                vehicle.Id, vehicle.Status, string.Join(", ", missing));

            return vehicle;
        }

        var wasApproved = vehicle.Status == RegistrationStatuses.Approved;

        var approved = await vehicles.ApproveAsync(
            unitOfWork.Connection, unitOfWork.Transaction, vehicle.Id, cancellationToken);

        if (approved is null)
        {
            // REJECTED or DEACTIVATED. RequireOnboardable refuses the latter up front, and the
            // former is a Verification Officer's decision that four green steps do not overturn.
            return vehicle;
        }

        if (!wasApproved)
        {
            logger.LogInformation(
                "Vehicle {VehicleId} auto-approved: all four onboarding steps verified, no officer step (AL-27)",
                vehicle.Id);

            events.Add(OnboardingEvents.VehicleApproved(approved, clock.GetUtcNow()));
        }

        return approved;
    }

    /// <summary>
    /// E-03's release: a vehicle suspended for an expired document comes back once every current
    /// document is valid again.
    /// </summary>
    private async Task<Vehicle> RefreshDispatchStateAsync(
        IUnitOfWork unitOfWork, Vehicle vehicle, List<OutboxRecord> events, CancellationToken cancellationToken)
    {
        if (vehicle.DispatchState != DispatchStates.Suspended)
        {
            return vehicle;
        }

        // "Until re-uploaded and re-approved" (E-03), read strictly: both of AL-10's mandatory
        // documents have to be current, unexpired and carrying a real expiry, and the driver's own
        // identity documents have to be in date. A renewal whose expiry nobody could read is not a
        // renewal anybody can rely on, so it does not put the vehicle back on the road.
        var missing = await MissingMandatoryDocumentsAsync(unitOfWork, vehicle.Id, cancellationToken);

        if (missing.Count > 0 || (await ExpiredIdentityDocumentsAsync(unitOfWork, vehicle, cancellationToken)).Count > 0)
        {
            return vehicle;
        }

        if (await vehicles.SetDispatchStateAsync(
                unitOfWork.Connection, unitOfWork.Transaction, vehicle.Id, DispatchStates.Active, cancellationToken))
        {
            logger.LogInformation(
                "Vehicle {VehicleId} is out of DISPATCH_SUSPENDED: every current document is valid again (E-03)",
                vehicle.Id);

            events.Add(OnboardingEvents.DispatchResumed(vehicle.Id, vehicle.OwnerId, "documents-renewed"));
        }

        return vehicle with { DispatchState = DispatchStates.Active };
    }

    /// <summary>
    /// Which of AL-10's mandatory documents this vehicle does not currently have in a usable
    /// state. Empty is the approval gate.
    /// </summary>
    private async Task<IReadOnlyList<string>> MissingMandatoryDocumentsAsync(
        IUnitOfWork unitOfWork, Guid vehicleId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var current = await documents.ListCurrentForVehicleAsync(
            unitOfWork.Connection, unitOfWork.Transaction, vehicleId, cancellationToken);

        return
        [
            .. new[] { DocumentKinds.Insurance, DocumentKinds.RevenueLicense }
                .Where(kind => current.FirstOrDefault(document => document.Kind == kind) is not { } document
                               || document.Status == DocumentStatuses.Expired
                               || document.ExpiresAt is null
                               || document.ExpiresAt <= now),
        ];
    }

    /// <summary>
    /// The driver's own lapsed identity documents. E-03 suspends "the driver", and a lapsed
    /// driving licence is not made good by the vehicle's insurance being in order.
    /// </summary>
    private async Task<IReadOnlyList<VehicleDocument>> ExpiredIdentityDocumentsAsync(
        IUnitOfWork unitOfWork, Vehicle vehicle, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var identity = await documents.ListCurrentForDriverAsync(
            unitOfWork.Connection, unitOfWork.Transaction, vehicle.OwnerId, cancellationToken);

        return
        [
            .. identity.Where(document => document.Status == DocumentStatuses.Expired
                                          || (document.ExpiresAt is not null && document.ExpiresAt <= now)),
        ];
    }

    /// <summary>
    /// A step is <c>pending_review</c> when any of its fields is pending, and <c>verified</c>
    /// otherwise (BR-25.3, migration 0305's comment).
    /// </summary>
    /// <remarks>
    /// The plate mismatch and the missing-field case both arrive here as a pending field, so this
    /// rule has one clause rather than three that can drift apart. <c>details</c> is verified on
    /// save because it has no document and therefore no field to be pending — D5' §14.1a's
    /// "(entered)".
    /// </remarks>
    private static string DeriveStepStatus(string step, IReadOnlyList<DocumentField> fields) =>
        step != OnboardingSteps.Details && (fields.Count == 0 || fields.Any(field => field.IsPending))
            ? StepStatuses.PendingReview
            : StepStatuses.Verified;

    /// <summary>AL-29's per-field rule, spelled once.</summary>
    private string DeriveVerifyStatus(string key, string? value, decimal? confidence, string source)
    {
        if (source == FieldSources.Manual)
        {
            return VerifyStatuses.Pending;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return VerifyStatuses.Pending;
        }

        // I-25.2's `reg_no_match`. A plate that read as something else is not a low-confidence
        // value — it is a confident reading of the wrong vehicle — so it is pending however sure
        // ocr-svc was.
        if (key == DocumentFieldKeys.RegNoMatch && !IsTrue(value))
        {
            return VerifyStatuses.Pending;
        }

        // No confidence is treated exactly like a low one. An unscored value has not been
        // verified, whatever produced it.
        return confidence is null || confidence < _options.OcrConfidenceThreshold
            ? VerifyStatuses.Pending
            : VerifyStatuses.AutoVerified;
    }

    /// <summary>
    /// AL-30's resume rule: the first step that is not <c>VERIFIED</c>, never Step 1 by default.
    /// </summary>
    private static string? ResolveNextStep(IReadOnlyDictionary<string, string> verdicts) =>
        OnboardingSteps.All.FirstOrDefault(step => verdicts[step] != StepVerdicts.Verified);

    // -------------------------------------------------------------------------------------------
    // Reads and plumbing.
    // -------------------------------------------------------------------------------------------

    private async Task<OnboardingState> ReadStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Vehicle vehicle, CancellationToken cancellationToken)
    {
        var saved = await steps.ListAsync(connection, transaction, vehicle.Id, cancellationToken);
        var verdicts = new Dictionary<string, string>(StringComparer.Ordinal);
        var allFields = new List<DocumentField>();

        foreach (var stepName in OnboardingSteps.All)
        {
            var row = saved.FirstOrDefault(entry => entry.Step == stepName);
            verdicts[stepName] = row is null ? StepVerdicts.PendingInput : ToVerdict(row.Status);

            if (row is not null)
            {
                allFields.AddRange(await documents.ListFieldsAsync(
                    connection, transaction, DocumentIdsOf(row), cancellationToken));
            }
        }

        return new OnboardingState(vehicle, verdicts, ResolveNextStep(verdicts), allFields);
    }

    private async Task<PreparedDocument> PrepareAsync(
        PendingUpload upload,
        string kind,
        string? side,
        string? registrationNumber,
        CancellationToken cancellationToken)
    {
        DocumentExtraction result;

        try
        {
            result = await extraction.ExtractAsync(
                new DocumentExtractionRequest(upload.Id, upload.StorageUrl, kind, side, registrationNumber),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // C054's fence in registry's own words: Gemini being down must not stop onboarding.
            // The step still saves, lands pending_review, and a Verification Officer picks it up —
            // which is exactly what D5' §14.1a does with a document that failed to extract.
            logger.LogError(
                ex, "ocr-svc could not extract {Kind} upload {UploadId}; the step will be pending_review", kind, upload.Id);

            result = DocumentExtraction.Unavailable;
        }

        return new PreparedDocument(upload, kind, side, result);
    }

    private async Task<PendingUpload> RequireUploadAsync(
        Guid ownerId, string? fileId, string field, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(fileId, out var uploadId))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                [field] = [$"{field} is required and must be an upload identifier."],
            });
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var upload = await documents.FindUploadAsync(connection, null, uploadId, cancellationToken);

        // Ownership, not just existence. Without it a driver could attach somebody else's upload
        // and have its extracted licence number verify against their own profile.
        if (upload is null || upload.OwnerId != ownerId)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                [field] = [$"{field} does not name an upload belonging to this driver."],
            });
        }

        return upload;
    }

    private async Task<DriverProfile> UpsertProfileRowAsync(
        IUnitOfWork unitOfWork,
        Guid driverId,
        string displayName,
        string? photoUrl,
        string? nicNo,
        string[]? allowedVehicleTypes,
        DateTimeOffset? verifiedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await profiles.UpsertAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                driverId,
                displayName,
                photoUrl,
                nicNo,
                allowedVehicleTypes,
                verifiedAt,
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // A valid token for an account iam-svc no longer has. 404 rather than 500: nothing is
            // broken here, the subject simply does not exist.
            throw new MageRideException(
                MageRideErrors.NotFound, $"No account {driverId}. registry-svc does not create accounts; iam-svc does.");
        }
    }

    /// <summary>
    /// Recomputes <c>reg_no_match</c> for every photo of a vehicle against a registration number
    /// that has just changed.
    /// </summary>
    private async Task RejudgePlateMatchAsync(
        IUnitOfWork unitOfWork, Guid vehicleId, string registrationNumber, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
             UPDATE registry.document_fields f
                SET field_value = CASE WHEN upper(p.field_value) = upper($2) THEN 'true' ELSE 'false' END,
                    verify_status = CASE
                      WHEN f.source = '{FieldSources.Manual}' THEN '{VerifyStatuses.Pending}'
                      WHEN upper(p.field_value) <> upper($2) THEN '{VerifyStatuses.Pending}'
                      WHEN f.confidence IS NULL OR f.confidence < $3 THEN '{VerifyStatuses.Pending}'
                      ELSE '{VerifyStatuses.AutoVerified}' END
               FROM registry.document_fields p
               JOIN registry.documents d ON d.id = p.document_id
              WHERE f.document_id = p.document_id
                AND f.field_key = '{DocumentFieldKeys.RegNoMatch}'
                AND p.field_key = '{DocumentFieldKeys.PlateText}'
                AND d.vehicle_id = $1
                AND d.kind = '{DocumentKinds.Registration}';
             """,
            unitOfWork.Connection,
            unitOfWork.Transaction);

        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = vehicleId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = registrationNumber });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Numeric, Value = _options.OcrConfidenceThreshold });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<Vehicle> RequireOwnedVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.FindAsync(connection, transaction, vehicleId, cancellationToken)
            ?? throw new MageRideException(MageRideErrors.VehicleNotFound, $"No vehicle {vehicleId}.");

        // Ownership, not entitlement. An assigned driver operates a fleet vehicle (US-13.9); its
        // documents are the fleet's to upload in the Fleet Portal (AL-50, SCR-FP-004).
        return vehicle.OwnerId == driverId
            ? vehicle
            : throw new MageRideException(MageRideErrors.NotOwner, "This vehicle belongs to another driver.");
    }

    /// <summary>The AL-27 fence: in-app vehicle onboarding is Mode C, and only Mode C.</summary>
    private static void RequireOnboardable(Vehicle vehicle)
    {
        if (vehicle.Mode != OperatingModes.C)
        {
            throw new MageRideException(
                MageRideErrors.ModeNotAllowed,
                "In-app vehicle onboarding is Mode C only. Mode A and Mode B vehicles and their permits are " +
                "onboarded in the Fleet Portal (SCR-FP-004, AL-27/AL-50).");
        }

        if (vehicle.Status == RegistrationStatuses.Deactivated)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"Vehicle {vehicle.Id} is deactivated. Onboarding a retired vehicle would put its plate back into " +
                "the active set behind D-37's back.");
        }
    }

    private static IReadOnlyList<Guid> DocumentIdsOf(OnboardingStepRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Fields))
        {
            return [];
        }

        using var document = JsonDocument.Parse(row.Fields);

        if (!document.RootElement.TryGetProperty("documentIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. ids.EnumerateArray().Select(id => Guid.TryParse(id.GetString(), out var parsed) ? parsed : Guid.Empty).Where(id => id != Guid.Empty)];
    }

    /// <summary>
    /// The expiry an extracted document carries, as an instant. A date with no time expires at the
    /// end of that Colombo day (D-38) — a licence valid "to 1 August" is valid all of 1 August.
    /// </summary>
    private static DateTimeOffset? ResolveExpiry(string kind, IReadOnlyDictionary<string, DocumentField> fields)
    {
        var key = DocumentFieldKeys.ExpiryFieldFor(kind);

        if (key is null || !fields.TryGetValue(key, out var field) || string.IsNullOrWhiteSpace(field.FieldValue))
        {
            return null;
        }

        if (DateOnly.TryParse(field.FieldValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return BusinessCalendar.EndOfDay(date);
        }

        return DateTimeOffset.TryParse(
            field.FieldValue, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant
            : null;
    }

    private static string ToVerdict(string stepStatus) => stepStatus switch
    {
        StepStatuses.Verified => StepVerdicts.Verified,
        StepStatuses.PendingReview => StepVerdicts.PendingReview,
        _ => StepVerdicts.PendingInput,
    };

    private static bool IsTrue(string? value) =>
        bool.TryParse(value, out var parsed) ? parsed : string.Equals(value, "1", StringComparison.Ordinal);

    private static string? ValueOf(IEnumerable<DocumentField> fields, string key) =>
        fields.FirstOrDefault(field => field.FieldKey == key)?.FieldValue;

    private static string[]? SplitTypes(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string Serialize(object value) => JsonSerializer.Serialize(value, MageRideJson.StorageOptions);

    private static string RequireStep(string? step) =>
        OnboardingSteps.IsKnown(step)
            ? step!
            : throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["step"] = [$"step must be one of {string.Join(", ", OnboardingSteps.All)}."],
            });

    private static string RequireDriverName(string? driverName)
    {
        var name = driverName?.Trim();

        return string.IsNullOrEmpty(name) || name.Length > MaxDriverNameLength
            ? throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["driverName"] = [$"driverName is required and must be at most {MaxDriverNameLength} characters."],
            })
            : name;
    }

    private static string? RequireNic(string? nicNo)
    {
        var nic = nicNo?.Trim();

        if (string.IsNullOrEmpty(nic))
        {
            return null;
        }

        return nic.Length <= MaxNicLength
            ? nic
            : throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["nicNo"] = [$"nicNo must be at most {MaxNicLength} characters."],
            });
    }

    private static string[]? RequireAllowedVehicleTypes(IReadOnlyList<string>? allowedVehicleTypes)
    {
        if (allowedVehicleTypes is null || allowedVehicleTypes.Count == 0)
        {
            return null;
        }

        var invalid = allowedVehicleTypes.Where(type => !VehicleTypes.IsCanonical(type)).ToArray();

        return invalid.Length == 0
            ? [.. allowedVehicleTypes]
            : throw new MageRideException(
                MageRideErrors.InvalidVehicleType,
                $"'{string.Join("', '", invalid)}' are not canonical vehicle types. AL-09 renamed 'car' to 'sedan'; " +
                "the set is " + string.Join(", ", VehicleTypes.All.Order(StringComparer.Ordinal)) + ".");
    }

    private static IReadOnlyDictionary<string, string> RequireManualFields(IReadOnlyDictionary<string, string>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var invalid = fields.Keys.Where(key => key.Length is 0 or > 64).ToArray();

        return invalid.Length == 0
            ? fields
            : throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["fields"] = ["Every correction key must be between 1 and 64 characters."],
            });
    }

    private static string NormaliseRegistration(string? value) =>
        RegistrationNumbers.TryNormalise(value, out var normalised)
            ? normalised
            : throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["registrationNumber"] =
                [
                    $"registrationNumber must be at most {RegistrationNumbers.MaxLength} characters and may " +
                    "contain only letters, digits, spaces and hyphens.",
                ],
            });

    private static void RequireDriverAppVehicleType(string? vehicleType)
    {
        if (!VehicleTypes.IsCanonical(vehicleType))
        {
            throw new MageRideException(
                MageRideErrors.InvalidVehicleType,
                $"'{vehicleType}' is not a canonical vehicle type. AL-09 renamed 'car' to 'sedan'; the set is " +
                string.Join(", ", VehicleTypes.All.Order(StringComparer.Ordinal)) + ".");
        }

        if (!VehicleTypes.IsDriverApp(vehicleType))
        {
            throw new MageRideException(
                MageRideErrors.ModeNotAllowed,
                $"'{vehicleType}' is a Mode A vehicle. Buses are registered in the Fleet Portal and trains by " +
                "admin-bff; the Driver App onboards Mode C only.");
        }
    }

    /// <summary>One upload, its intended slot, and what ocr-svc made of it.</summary>
    private sealed record PreparedDocument(
        PendingUpload Upload, string Kind, string? Side, DocumentExtraction Extraction);
}
