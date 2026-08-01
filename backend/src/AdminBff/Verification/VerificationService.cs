using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Domain;
using MageRide.AdminBff.Endpoints;
using MageRide.AdminBff.Persistence;
using MageRide.AdminBff.Upstream;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using Npgsql;

namespace MageRide.AdminBff.Verification;

/// <summary>What <c>GET /v1/admin/documents/{docId}</c> answers with, once the view is recorded.</summary>
public sealed record DocumentView(Guid DocId, string Kind, string SignedUrl, DateTimeOffset ExpiresAt);

/// <summary>
/// SCR-AP-003/003a/003b/003c — the Verification Officer's three queues, the subject detail, the
/// per-field decision, the two verdicts and the audited document viewer (AL-39).
/// </summary>
/// <remarks>
/// <para>
/// <b>One family, three subjects, and the owner of each decision decides it.</b> AL-39 states the
/// officer's routes once for a driver, a vehicle and a fleet organisation, so the routes are
/// subject-agnostic — but "approve" means a different write in each case, and the BFF rule holds:
/// where a service exposes a route for the operation, admin-bff forwards to it. The AL-30 recompute
/// is registry-svc's (it built <c>/v1/internal/vehicles/{id}/onboarding/recompute</c> for this
/// caller), AL-50's fleet-vehicle gate and AL-49's organisation approval are fleet-svc's (its whole
/// <c>/v1/internal/fleets/**</c> plane exists for this BFF). What is left over — the queues, which
/// no service exposes; <c>registry.vehicles.rejection_reason</c>, which registry-svc's own file
/// leaves to this component; and the driver's identity verdict, which nothing anywhere exposes — is
/// written here.
/// </para>
/// <para>
/// <b>The queues are AL-27's fence expressed as a query.</b> A subject is in a queue exactly when it
/// has a <c>registry.document_fields</c> row still <c>pending</c>. An auto-verified document
/// produces no such row and therefore cannot appear, which is stronger than filtering it out: there
/// is no code path that could stop filtering.
/// </para>
/// <para>
/// <b>Approve is refused while a flagged field is unconfirmed, and it is one query.</b> US-2.10a's
/// rule is checked against the same rows the queue is built from, inside the transaction that
/// writes the verdict where there is one — so a field flagged while the officer had the screen open
/// stops the approval rather than being overtaken by it.
/// </para>
/// </remarks>
public interface IVerificationService
{
    Task<CursorPage<DriverQueueRowResponse>> DriverQueueAsync(
        string? search, string? status, PageRequest page, CancellationToken cancellationToken);

    Task<CursorPage<VehicleQueueRowResponse>> VehicleQueueAsync(
        string? search, string? status, PageRequest page, CancellationToken cancellationToken);

    Task<CursorPage<OrgQueueRowResponse>> OrgQueueAsync(
        string? search, string? status, PageRequest page, HttpContext context, CancellationToken cancellationToken);

    Task<VerificationDetailResponse> DetailAsync(
        Guid subjectId, HttpContext context, CancellationToken cancellationToken);

    Task<OrgVerificationResponse> OrgDetailAsync(
        Guid orgId, HttpContext context, CancellationToken cancellationToken);

    Task<DecideFieldResponse> DecideFieldAsync(
        Guid subjectId,
        string fieldKey,
        string? value,
        Guid officerId,
        HttpContext context,
        CancellationToken cancellationToken);

    Task<VerificationDecisionResponse> ApproveAsync(
        Guid subjectId, Guid officerId, HttpContext context, CancellationToken cancellationToken);

    Task<VerificationDecisionResponse> RejectAsync(
        Guid subjectId, string reason, Guid officerId, HttpContext context, CancellationToken cancellationToken);

    Task<DocumentView> ViewDocumentAsync(Guid docId, string? variant, CancellationToken cancellationToken);

    Task<CursorPage<DriverPayoutQueueRowResponse>> DriverPayoutQueueAsync(
        string? search, string? status, PageRequest page, CancellationToken cancellationToken);

    Task<DriverPayoutVerificationResponse> DriverPayoutDetailAsync(
        Guid driverId, CancellationToken cancellationToken);

    Task<DriverPayoutDecisionResponse> ApproveDriverPayoutAsync(
        Guid driverId, Guid officerId, HttpContext context, CancellationToken cancellationToken);

    Task<DriverPayoutDecisionResponse> RejectDriverPayoutAsync(
        Guid driverId, string reason, Guid officerId, HttpContext context, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVerificationService"/>
internal sealed class VerificationService(
    IUnitOfWorkFactory unitOfWorkFactory,
    INpgsqlConnectionFactory connections,
    IVerificationRepository verification,
    IDocumentLinks links,
    IAdminUpstream upstream,
    IAdminAuditContext audit,
    TimeProvider clock,
    ILogger<VerificationService> logger) : IVerificationService
{
    // ---------------------------------------------------------------------------------------
    // Queues
    // ---------------------------------------------------------------------------------------

    public async Task<CursorPage<DriverQueueRowResponse>> DriverQueueAsync(
        string? search, string? status, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var rows = await verification.DriverQueueAsync(
            QueryFor(search, status, page), cancellationToken);

        return CursorPage<DriverQueueRow>.FromOverfetch(
                rows, page.Limit, row => QueueCursors.Encode(row.SubmittedAt, row.DriverId))
            .Select(row => new DriverQueueRowResponse(
                row.DriverId, row.Name, row.SubmittedAt, row.FlaggedFields, row.Status));
    }

    public async Task<CursorPage<VehicleQueueRowResponse>> VehicleQueueAsync(
        string? search, string? status, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var rows = await verification.VehicleQueueAsync(
            QueryFor(search, status, page), cancellationToken);

        return CursorPage<VehicleQueueRow>.FromOverfetch(
                rows, page.Limit, row => QueueCursors.Encode(row.SubmittedAt, row.VehicleId))
            .Select(row => new VehicleQueueRowResponse(
                row.VehicleId, row.RegNo, row.OwnerDriverId, row.SubmittedAt, row.FlaggedFields, row.Status));
    }

    /// <remarks>
    /// <para>
    /// <b>Forwarded, and the vehicle count is joined on locally.</b> fleet-svc owns the
    /// organisations and their payout versions; <c>registry.fleet_vehicles</c> is a registry table
    /// this service already reads for the other two queues, and counting there is one statement for
    /// the whole page rather than a query per organisation on the far side of a hop.
    /// </para>
    /// <para>
    /// <b>The page is not a cursor page, and says so.</b> fleet-svc's internal queue answers a flat
    /// capped list — it has no cursor and its own file explains why — so <c>cursor</c> is null and
    /// <c>hasMore</c> is false rather than invented. The search is applied here for the same reason:
    /// there is no search parameter to forward, and the list is bounded by how many organisations
    /// are awaiting an officer.
    /// </para>
    /// </remarks>
    public async Task<CursorPage<OrgQueueRowResponse>> OrgQueueAsync(
        string? search, string? status, PageRequest page, HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var query = $"/v1/internal/fleets/queue?limit={page.Limit}"
                    + (string.IsNullOrWhiteSpace(status) ? string.Empty : $"&status={Uri.EscapeDataString(status)}");

        using var request = upstream.Request(AdminUpstreams.Fleet, HttpMethod.Get, query);

        var answer = await upstream.SendAsync<FleetQueuePage>(
            AdminUpstreams.Fleet, request, context, cancellationToken);

        var rows = (answer.Items ?? [])
            .Where(row => Matches(row, search))
            .Take(page.Limit)
            .ToArray();

        var counts = await verification.FleetVehicleCountsAsync(
            [.. rows.Select(row => row.FleetId)], cancellationToken);

        return new CursorPage<OrgQueueRowResponse>(
            [
                .. rows.Select(row => new OrgQueueRowResponse(
                    row.FleetId,
                    row.Name,
                    // US-13.A7's evidence, reduced to the one question the list has room for: is
                    // there anything for the officer to read yet.
                    string.IsNullOrWhiteSpace(row.RegistrationNo) || string.IsNullOrWhiteSpace(row.ContactPhone)
                        ? "incomplete"
                        : "complete",
                    counts.TryGetValue(row.FleetId, out var count) ? count : 0,
                    row.Status,
                    row.PayoutProfileStatus)),
            ],
            Cursor: null,
            HasMore: false);
    }

    private static bool Matches(FleetQueueRow row, string? search) =>
        string.IsNullOrWhiteSpace(search)
        || row.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || (row.RegistrationNo?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    private static VerificationQueueQuery QueryFor(string? search, string? status, PageRequest page)
    {
        var (at, id) = QueueCursors.Decode(page.Cursor);

        return new VerificationQueueQuery(search, RequireStatus(status), at, id, page.OverfetchLimit);
    }

    /// <summary>SCR-AP-003's status filter, checked rather than passed through to a comparison.</summary>
    private static string? RequireStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var normalised = status.Trim().ToUpperInvariant();

        return normalised is VerificationStatuses.Pending
            or VerificationStatuses.Approved
            or VerificationStatuses.Rejected
            ? normalised
            : throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["status"] =
                [
                    $"status must be one of {VerificationStatuses.Pending}, {VerificationStatuses.Approved}, "
                    + $"{VerificationStatuses.Rejected}.",
                ],
            });
    }

    // ---------------------------------------------------------------------------------------
    // Detail
    // ---------------------------------------------------------------------------------------

    public async Task<VerificationDetailResponse> DetailAsync(
        Guid subjectId, HttpContext context, CancellationToken cancellationToken)
    {
        var subject = await RequireSubjectAsync(subjectId, cancellationToken);

        if (subject.Type == VerificationSubjectTypes.Org)
        {
            // AL-39 gives the org its own detail shape (kyc + documents) and the same id reaches
            // both routes. Answering the org's shape here rather than an empty field list keeps
            // `GET /v1/admin/verification/{subjectId}` honest for all three subjects.
            var org = await OrgDetailAsync(subjectId, context, cancellationToken);

            return new VerificationDetailResponse(
                new VerificationSubjectResponse(subject.Id, subject.Type, subject.DisplayName),
                Fields: [],
                org.Documents,
                Steps: [new VerificationStepResponse("kyc", KycVerdict(org))],
                Approvable: true);
        }

        var fields = await verification.FieldsAsync(subject, cancellationToken);
        var documents = await verification.DocumentsAsync(subject, cancellationToken);
        var steps = await BuildStepsAsync(subject, fields, documents, cancellationToken);

        return new VerificationDetailResponse(
            new VerificationSubjectResponse(subject.Id, subject.Type, subject.DisplayName),
            [.. fields.Select(ToResponse)],
            [.. documents.Select(ToResponse)],
            steps,
            Approvable: !fields.Any(field => field.IsPending));
    }

    /// <remarks>
    /// The whole organisation view is fleet-svc's, and only the links are minted here — which is
    /// exactly the division that service's own file records ("it holds no signing key and no
    /// object-storage client; admin-bff mints them").
    /// </remarks>
    public async Task<OrgVerificationResponse> OrgDetailAsync(
        Guid orgId, HttpContext context, CancellationToken cancellationToken)
    {
        using var request = upstream.Request(
            AdminUpstreams.Fleet, HttpMethod.Get, $"/v1/internal/fleets/{orgId:D}");

        var answer = await upstream.SendAsync<FleetVerificationPayload>(
            AdminUpstreams.Fleet, request, context, cancellationToken);

        var kyc = answer.Kyc ?? throw new MageRideException(
            MageRideErrors.DependencyUnavailable, "fleet-svc answered a verification detail with no organisation.");

        return new OrgVerificationResponse(
            new OrgKycResponse(
                orgId,
                kyc.Name,
                kyc.RegistrationNo,
                kyc.ContactPhone,
                kyc.ContactEmail,
                kyc.Address,
                kyc.Status,
                kyc.RejectionReason,
                answer.PayoutProfile is { } payout
                    ? new OrgPayoutResponse(
                        payout.Bank,
                        payout.Branch,
                        payout.AccountNo,
                        payout.AccountHolderName,
                        payout.Status,
                        payout.RejectionReason,
                        payout.VerifiedAt)
                    : null),
            answer.PayoutProfileStatus,
            [
                .. (answer.Documents ?? []).Select(document => new DocumentRefResponse(
                    document.DocId,
                    document.Kind,
                    links.Create(document.DocId, DocumentVariants.Thumb),
                    links.Create(document.DocId, DocumentVariants.Full),
                    // AL-43's provenance is about onboarding photographs; fleet-svc leaves it NULL
                    // on a payout document deliberately, and inventing a value here would put a
                    // fraud signal on every bank statement on the platform.
                    CapturedVia: null)),
            ]);
    }

    /// <summary>The one row an organisation's decision rail has: is the evidence approved yet.</summary>
    private static string KycVerdict(OrgVerificationResponse org) =>
        org.Kyc.Status == VerificationStatuses.Approved && org.PayoutProfileStatus is null or "verified"
            ? VerificationStepVerdicts.Verified
            : VerificationStepVerdicts.PendingReview;

    /// <remarks>
    /// <para>
    /// <b>Two vocabularies, because there are two onboarding surfaces.</b> A Mode C vehicle has
    /// AL-30's four saved steps and they are authoritative — registry-svc recomputes them and this
    /// service must not derive a second opinion. A fleet vehicle has none at all, so its rail is
    /// AL-50's named slots, derived by the same rule registry-svc applies to a step: verified when
    /// no field of it is pending.
    /// </para>
    /// <para>
    /// A driver's licence is one synthetic <c>profile</c> step for the same reason — SCR-AP-003a's
    /// rail with nothing in it reads as "nothing to check".
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<VerificationStepResponse>> BuildStepsAsync(
        VerificationSubject subject,
        IReadOnlyList<VerificationField> fields,
        IReadOnlyList<VerificationDocumentRow> documents,
        CancellationToken cancellationToken)
    {
        if (subject.Type == VerificationSubjectTypes.Vehicle)
        {
            var saved = await verification.StepsAsync(subject.Id, cancellationToken);

            if (saved.Count > 0)
            {
                return [.. saved
                    .OrderBy(step => WizardOrder(step.Step))
                    .Select(step => new VerificationStepResponse(step.Step, ToVerdict(step.Status)))];
            }
        }

        var pendingByDocument = fields
            .GroupBy(field => field.DocumentId)
            .ToDictionary(group => group.Key, group => group.Any(field => field.IsPending));

        return
        [
            .. documents
                .GroupBy(document => VerificationSteps.ForKind(document.Kind, hasWizardSteps: false))
                .Select(group => new VerificationStepResponse(
                    group.Key,
                    group.Any(document =>
                        pendingByDocument.TryGetValue(document.DocId, out var pending) && pending)
                        ? VerificationStepVerdicts.PendingReview
                        : VerificationStepVerdicts.Verified))
                .OrderBy(step => step.Step, StringComparer.Ordinal),
        ];
    }

    /// <summary>AL-30's wizard order, which is the order the rail is read in — not alphabetic.</summary>
    private static int WizardOrder(string step) => step switch
    {
        "details" => 0,
        "insurance" => 1,
        "revenue" => 2,
        "photos" => 3,
        _ => 4,
    };

    private static string ToVerdict(string stepStatus) => stepStatus switch
    {
        "verified" => VerificationStepVerdicts.Verified,
        "pending_review" => VerificationStepVerdicts.PendingReview,
        _ => VerificationStepVerdicts.PendingInput,
    };

    // ---------------------------------------------------------------------------------------
    // Confirm / edit-and-confirm
    // ---------------------------------------------------------------------------------------

    public async Task<DecideFieldResponse> DecideFieldAsync(
        Guid subjectId,
        string fieldKey,
        string? value,
        Guid officerId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var subject = await RequireSubjectAsync(subjectId, cancellationToken);

        if (subject.Type == VerificationSubjectTypes.Org)
        {
            // Nothing extracts fields from a bank statement, so there is no field to decide. 404
            // rather than 400: the key does not exist on this subject.
            throw new MageRideException(
                MageRideErrors.NotFound,
                "A fleet organisation has no extracted fields. Its evidence is approved or rejected whole "
                + "(AL-49).");
        }

        var normalised = value?.Trim();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var before = await verification.FieldsByKeyAsync(
            unitOfWork.Connection, unitOfWork.Transaction, subject, fieldKey, cancellationToken);

        if (before.Count == 0)
        {
            throw new MageRideException(
                MageRideErrors.NotFound, $"No field '{fieldKey}' was extracted for this subject.");
        }

        var moved = await verification.ConfirmFieldAsync(
            unitOfWork, subject, fieldKey, normalised, officerId, clock.GetUtcNow(), cancellationToken);

        var after = await verification.FieldsByKeyAsync(
            unitOfWork.Connection, unitOfWork.Transaction, subject, fieldKey, cancellationToken);

        audit.Record(
            subject.Id,
            before: new { field = fieldKey, rows = before.Select(Snapshot) },
            after: new { field = fieldKey, rows = after.Select(Snapshot), edited = normalised is not null, moved },
            action: AdminAuditActions.FieldConfirmed,
            entityType: EntityTypeOf(subject));

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        // After the commit, because it is another service reading what this transaction wrote.
        // registry-svc built this route for exactly this moment and says so: without it the vehicle
        // sits at pending_review for a field that is no longer pending.
        await RecomputeAsync(subject, context, cancellationToken);

        var fields = await verification.FieldsAsync(subject, cancellationToken);
        var documents = await verification.DocumentsAsync(subject, cancellationToken);
        var steps = await BuildStepsAsync(subject, fields, documents, cancellationToken);

        var decided = after[0];
        var kind = documents.FirstOrDefault(document => document.DocId == decided.DocumentId)?.Kind;

        var stepStatus = kind is null
            ? VerificationStepVerdicts.PendingInput
            : steps.FirstOrDefault(step =>
                    step.Step == VerificationSteps.ForKind(kind, HasWizardSteps(subject, steps)))?.Status
              ?? VerificationStepVerdicts.PendingInput;

        return new DecideFieldResponse(
            ToResponse(decided), stepStatus, Approvable: !fields.Any(field => field.IsPending));
    }

    /// <summary>Whether the rail we just built is AL-30's wizard rather than AL-50's slots.</summary>
    private static bool HasWizardSteps(
        VerificationSubject subject, IReadOnlyList<VerificationStepResponse> steps) =>
        subject.Type == VerificationSubjectTypes.Vehicle && steps.Any(step => step.Step == "details");

    // ---------------------------------------------------------------------------------------
    // Approve / reject
    // ---------------------------------------------------------------------------------------

    public async Task<VerificationDecisionResponse> ApproveAsync(
        Guid subjectId, Guid officerId, HttpContext context, CancellationToken cancellationToken)
    {
        var subject = await RequireSubjectAsync(subjectId, cancellationToken);

        await RequireNothingPendingAsync(subject, cancellationToken);

        return subject.Type switch
        {
            VerificationSubjectTypes.Org => await ApproveOrgAsync(subject, officerId, context, cancellationToken),
            VerificationSubjectTypes.Driver => await ApproveDriverAsync(subject, cancellationToken),
            _ => await ApproveVehicleAsync(subject, officerId, context, cancellationToken),
        };
    }

    public async Task<VerificationDecisionResponse> RejectAsync(
        Guid subjectId, string reason, Guid officerId, HttpContext context, CancellationToken cancellationToken)
    {
        var subject = await RequireSubjectAsync(subjectId, cancellationToken);

        return subject.Type switch
        {
            VerificationSubjectTypes.Org =>
                await RejectOrgAsync(subject, reason, officerId, context, cancellationToken),
            VerificationSubjectTypes.Driver => await RejectDriverAsync(subject, reason, cancellationToken),
            _ => await RejectVehicleAsync(subject, reason, officerId, context, cancellationToken),
        };
    }

    /// <summary>US-2.10a, checked against the same rows the queue is built from.</summary>
    private async Task RequireNothingPendingAsync(
        VerificationSubject subject, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var pending = await verification.PendingFieldCountAsync(
            connection, transaction: null, subject, cancellationToken);

        if (pending > 0)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"{pending} flagged field(s) are still awaiting Confirm or Edit & confirm. Approve unlocks only "
                + "when every one of them is confirmed (US-2.10a).");
        }
    }

    /// <remarks>
    /// <para>
    /// <b>Mode C is registry-svc's rule, run by registry-svc.</b> AL-30's auto-approval reads four
    /// steps and AL-10's mandatory documents, and re-deriving either here would be a second opinion
    /// about the same rows. So the officer's Approve confirms the fields, asks for a recompute and
    /// then reports what the vehicle actually reached — a step still short of VERIFIED and an
    /// insurance certificate that lapsed while the queue item waited are both a 409 rather than a
    /// silent partial approval.
    /// </para>
    /// <para>
    /// <b>A REJECTED vehicle is reopened first, and that is its own audited fact.</b> registry-svc
    /// declines to auto-approve one ("a Verification Officer's decision that four green steps do not
    /// overturn"), so without this a resubmission after a refusal could never be approved. Withdrawing
    /// a rejection is a decision in its own right and gets its own row — which also means the trail
    /// is honest when the approval that follows it is then refused.
    /// </para>
    /// <para>
    /// <b>Mode A/B is fleet-svc's AL-50 gate.</b> A fleet vehicle has no
    /// <c>registry.onboarding_steps</c> rows for the wizard rule to read; its required slots —
    /// registration, insurance, revenue licence, and a route permit for Mode A — are re-derived
    /// inside the transaction that writes the status, on the far side of the hop.
    /// </para>
    /// </remarks>
    private async Task<VerificationDecisionResponse> ApproveVehicleAsync(
        VerificationSubject subject, Guid officerId, HttpContext context, CancellationToken cancellationToken)
    {
        if (subject.FleetId is { } fleetId && subject.Mode is not "C")
        {
            using var request = upstream.Request(
                AdminUpstreams.Fleet,
                HttpMethod.Post,
                $"/v1/internal/fleets/{fleetId:D}/vehicles/{subject.Id:D}/approve");

            request.Content = System.Net.Http.Json.JsonContent.Create(
                new { officerId = officerId.ToString() }, options: MageRideJson.Options);

            await upstream.SendAsync<FleetVehicleDecisionPayload>(
                AdminUpstreams.Fleet, request, context, cancellationToken);

            audit.Record(
                subject.Id,
                after: new { status = VerificationStatuses.Approved, mode = subject.Mode, fleetId },
                action: AdminAuditActions.VerificationApproved,
                entityType: AdminAuditActions.VehicleEntity);

            return new VerificationDecisionResponse(
                subject.Id, VerificationStatuses.Approved, Reason: null, MerchantBound: false);
        }

        if (subject.Status == VerificationStatuses.Rejected)
        {
            await ReopenVehicleAsync(subject, cancellationToken);
        }

        var settled = await RecomputeAsync(subject, context, cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.DependencyUnavailable, "registry-svc answered the recompute with no body.");

        if (settled.NextStep is { Length: > 0 } next)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"Onboarding step '{next}' is not verified, so this vehicle cannot be approved (AL-30).");
        }

        if (settled.Status != VerificationStatuses.Approved)
        {
            // All four steps verified and registry-svc still declined: AL-10's gate, re-read at the
            // moment of approval. The certificate the queue item was raised against has lapsed.
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"Every step is verified but the vehicle is still {settled.Status}. AL-10 requires a current, "
                + "unexpired insurance certificate and revenue licence before a registration is approved.");
        }

        logger.LogInformation(
            "Vehicle {VehicleId} approved by officer {OfficerId}. The D-11 OnePay merchant bind is NOT "
            + "triggered: registry-svc's POST /v1/internal/vehicles/{{id}}/merchant requires a merchantId and "
            + "nothing on this platform onboards one, so fare settlement answers 402 merchant-not-onboarded "
            + "until it does.",
            subject.Id,
            officerId);

        audit.Record(
            subject.Id,
            before: new { status = subject.Status },
            after: new { status = settled.Status, onboardingStatus = settled.OnboardingStatus, merchantBound = false },
            action: AdminAuditActions.VerificationApproved,
            entityType: AdminAuditActions.VehicleEntity);

        return new VerificationDecisionResponse(
            subject.Id, VerificationStatuses.Approved, Reason: null, MerchantBound: false);
    }

    private async Task ReopenVehicleAsync(VerificationSubject subject, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var before = await verification.VehicleStateAsync(
            unitOfWork.Connection, unitOfWork.Transaction, subject.Id, cancellationToken);

        try
        {
            if (!await verification.ClearVehicleRejectionAsync(unitOfWork, subject.Id, cancellationToken))
            {
                return;
            }
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // ux_vehicles_regno_active covers PENDING and APPROVED, so a plate that was freed by
            // this rejection and claimed by somebody else is a real conflict rather than a 500.
            throw new MageRideException(
                MageRideErrors.RegistrationExists,
                $"Registration {subject.DisplayName} was claimed by another live vehicle after this one was "
                + "rejected (D-37), so the rejection cannot be withdrawn.");
        }

        audit.Record(
            subject.Id,
            before: new { status = VerificationStatuses.Rejected, rejectionReason = before?.RejectionReason },
            after: new { status = VerificationStatuses.Pending, rejectionReason = (string?)null },
            action: AdminAuditActions.VerificationReopened,
            entityType: AdminAuditActions.VehicleEntity);

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
    }

    private async Task<VerificationDecisionResponse> ApproveDriverAsync(
        VerificationSubject subject, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var before = await verification.DriverStateAsync(
            unitOfWork.Connection, unitOfWork.Transaction, subject.Id, cancellationToken);

        if (before is null)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                "This driver has not completed Profile Setup, so there is no identity submission to approve "
                + "(AL-27).");
        }

        var now = clock.GetUtcNow();

        await verification.ApproveDriverAsync(unitOfWork, subject.Id, now, cancellationToken);

        audit.Record(
            subject.Id,
            before: new { verifiedAt = before.Value.VerifiedAt, rejectionReason = before.Value.RejectionReason },
            after: new { verifiedAt = now, rejectionReason = (string?)null },
            action: AdminAuditActions.VerificationApproved,
            entityType: AdminAuditActions.DriverEntity);

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        return new VerificationDecisionResponse(
            subject.Id, VerificationStatuses.Approved, Reason: null, MerchantBound: false);
    }

    /// <remarks>
    /// AL-49's whole point: this is the call that moves <c>registry.fleet_payout_profiles.status</c>
    /// to <c>verified</c>, which is what makes <c>payTo</c> available to subscription-svc's pay
    /// sheet and unlocks Paid classification (BR-31.1). fleet-svc does both rows in one transaction.
    /// </remarks>
    private async Task<VerificationDecisionResponse> ApproveOrgAsync(
        VerificationSubject subject, Guid officerId, HttpContext context, CancellationToken cancellationToken)
    {
        using var request = upstream.Request(
            AdminUpstreams.Fleet, HttpMethod.Post, $"/v1/internal/fleets/{subject.Id:D}/approve");

        request.Content = System.Net.Http.Json.JsonContent.Create(
            new { officerId = officerId.ToString() }, options: MageRideJson.Options);

        var decision = await upstream.SendAsync<FleetDecisionPayload>(
            AdminUpstreams.Fleet, request, context, cancellationToken);

        audit.Record(
            subject.Id,
            before: new { status = subject.Status },
            after: new
            {
                status = decision.Fleet?.Status ?? VerificationStatuses.Approved,
                payoutProfileStatus = decision.PayoutProfile?.Status,
            },
            action: AdminAuditActions.VerificationApproved,
            entityType: AdminAuditActions.FleetOrgEntity);

        return new VerificationDecisionResponse(
            subject.Id,
            decision.Fleet?.Status ?? VerificationStatuses.Approved,
            Reason: null,
            MerchantBound: false);
    }

    private async Task<VerificationDecisionResponse> RejectVehicleAsync(
        VerificationSubject subject, string reason, Guid officerId, HttpContext context, CancellationToken cancellationToken)
    {
        if (subject.FleetId is { } fleetId && subject.Mode is not "C")
        {
            using var request = upstream.Request(
                AdminUpstreams.Fleet,
                HttpMethod.Post,
                $"/v1/internal/fleets/{fleetId:D}/vehicles/{subject.Id:D}/reject");

            request.Content = System.Net.Http.Json.JsonContent.Create(
                new { officerId = officerId.ToString(), reason }, options: MageRideJson.Options);

            await upstream.SendAsync<FleetVehicleDecisionPayload>(
                AdminUpstreams.Fleet, request, context, cancellationToken);

            audit.Record(
                subject.Id,
                before: new { status = subject.Status },
                after: new { status = VerificationStatuses.Rejected, reason, fleetId },
                action: AdminAuditActions.VerificationRejected,
                entityType: AdminAuditActions.VehicleEntity);

            return new VerificationDecisionResponse(subject.Id, VerificationStatuses.Rejected, reason, false);
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        if (!await verification.RejectVehicleAsync(unitOfWork, subject.Id, reason, cancellationToken))
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                "A DEACTIVATED registration cannot be rejected — it has already left D-37's live set.");
        }

        audit.Record(
            subject.Id,
            before: new { status = subject.Status },
            after: new { status = VerificationStatuses.Rejected, rejectionReason = reason },
            action: AdminAuditActions.VerificationRejected,
            entityType: AdminAuditActions.VehicleEntity);

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Vehicle {VehicleId} rejected by officer {OfficerId}. The driver reads the reason at "
            + "GET /v1/vehicles/{{id}}/status (US-2.15) and re-enters this queue on the next upload.",
            subject.Id,
            officerId);

        return new VerificationDecisionResponse(subject.Id, VerificationStatuses.Rejected, reason, false);
    }

    private async Task<VerificationDecisionResponse> RejectDriverAsync(
        VerificationSubject subject, string reason, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var before = await verification.DriverStateAsync(
            unitOfWork.Connection, unitOfWork.Transaction, subject.Id, cancellationToken);

        if (before is null || !await verification.RejectDriverAsync(unitOfWork, subject.Id, reason, cancellationToken))
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                "This driver has not completed Profile Setup, so there is no identity submission to reject.");
        }

        audit.Record(
            subject.Id,
            before: new { verifiedAt = before.Value.VerifiedAt, rejectionReason = before.Value.RejectionReason },
            after: new { verifiedAt = (DateTimeOffset?)null, rejectionReason = reason },
            action: AdminAuditActions.VerificationRejected,
            entityType: AdminAuditActions.DriverEntity);

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        return new VerificationDecisionResponse(subject.Id, VerificationStatuses.Rejected, reason, false);
    }

    private async Task<VerificationDecisionResponse> RejectOrgAsync(
        VerificationSubject subject, string reason, Guid officerId, HttpContext context, CancellationToken cancellationToken)
    {
        using var request = upstream.Request(
            AdminUpstreams.Fleet, HttpMethod.Post, $"/v1/internal/fleets/{subject.Id:D}/reject");

        request.Content = System.Net.Http.Json.JsonContent.Create(
            new { officerId = officerId.ToString(), reason }, options: MageRideJson.Options);

        var decision = await upstream.SendAsync<FleetDecisionPayload>(
            AdminUpstreams.Fleet, request, context, cancellationToken);

        audit.Record(
            subject.Id,
            before: new { status = subject.Status },
            after: new
            {
                status = decision.Fleet?.Status ?? VerificationStatuses.Rejected,
                reason,
                payoutProfileStatus = decision.PayoutProfile?.Status,
            },
            action: AdminAuditActions.VerificationRejected,
            entityType: AdminAuditActions.FleetOrgEntity);

        return new VerificationDecisionResponse(
            subject.Id, decision.Fleet?.Status ?? VerificationStatuses.Rejected, reason, false);
    }

    // ---------------------------------------------------------------------------------------
    // The viewer
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// The <c>DOC_VIEW</c> row is recorded here and written by the interceptor, exactly as a
    /// mutation's is — the route declares the action with <c>.Audited(...)</c> and the handler says
    /// what was opened. A read that recorded nothing would still be a 200, which is why AL-39's
    /// audit obligation is met by routing every fetch through this method rather than by trusting a
    /// client to come back here.
    /// </remarks>
    public async Task<DocumentView> ViewDocumentAsync(
        Guid docId, string? variant, CancellationToken cancellationToken)
    {
        // An unknown rendition is the full document rather than a 400: the officer asked to see
        // this document, and refusing over a query-string typo would be a refusal to show evidence
        // somebody is waiting on. The value that was honoured is what the audit row records.
        var rendition = DocumentVariants.IsKnown(variant) ? variant! : DocumentVariants.Full;

        var document = await verification.FindDocumentAsync(docId, cancellationToken)
            ?? throw new MageRideException(MageRideErrors.NotFound, $"No document {docId}.");

        var (url, expiresAt) = links.SignedObjectUrl(document, rendition);

        audit.Record(
            docId,
            after: new
            {
                kind = document.Kind,
                source = document.Source,
                variant = rendition,
                ownerId = document.OwnerId,
                vehicleId = document.VehicleId,
                fleetId = document.FleetId,
                expiresAt,
            },
            action: AdminAuditActions.DocumentViewed,
            entityType: AdminAuditActions.DocumentEntity);

        return new DocumentView(docId, document.Kind, url, expiresAt);
    }

    // ---------------------------------------------------------------------------------------
    // The driver's bank & payout profile (AL-58, AL-59)
    // ---------------------------------------------------------------------------------------

    public async Task<CursorPage<DriverPayoutQueueRowResponse>> DriverPayoutQueueAsync(
        string? search, string? status, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var rows = await verification.DriverPayoutQueueAsync(
            QueryFor(search, status, page), cancellationToken);

        return CursorPage<DriverPayoutQueueRow>.FromOverfetch(
                rows, page.Limit, row => QueueCursors.Encode(row.SubmittedAt, row.DriverId))
            .Select(row => new DriverPayoutQueueRowResponse(
                row.DriverId, row.Name, row.Bank, row.AccountNo,
                row.SubmittedAt, row.HasProof, row.HasLankaQr, row.Status));
    }

    /// <remarks>
    /// <para>
    /// <b>The evidence is opened through the same audited viewer as everything else.</b> A payout
    /// document is a <c>docs.uploads</c> row with no <c>registry.documents</c> row — exactly like
    /// AL-49's — and <c>FindDocumentSql</c>'s second branch already resolves it, so the officer's
    /// lightbox and its <c>DOC_VIEW</c> row work here with nothing added.
    /// </para>
    /// <para>
    /// <b><c>approvable</c> is "there is something to decide", not "the evidence is sufficient".</b>
    /// Whether a bank statement actually shows this account is the judgement the officer is there to
    /// make, and a rule that refused Approve without an upload would be this service overruling
    /// them — a driver may well have proved their account another way. What it does exclude is
    /// pressing Approve on a version somebody already decided.
    /// </para>
    /// </remarks>
    public async Task<DriverPayoutVerificationResponse> DriverPayoutDetailAsync(
        Guid driverId, CancellationToken cancellationToken)
    {
        var profile = await RequirePayoutProfileAsync(driverId, cancellationToken);

        return new DriverPayoutVerificationResponse(
            profile.DriverId,
            profile.Name,
            profile.Bank,
            profile.Branch,
            profile.AccountNo,
            profile.AccountHolderName,
            profile.Status,
            profile.RejectionReason,
            profile.VerifiedAt,
            [
                .. PayoutDocuments(profile).Select(pair => new DocumentRefResponse(
                    pair.DocId,
                    pair.Kind,
                    links.Create(pair.DocId, DocumentVariants.Thumb),
                    links.Create(pair.DocId, DocumentVariants.Full),
                    // fleet-svc leaves captured_via NULL on a payout document and registry-svc now
                    // does the same: AL-43's provenance is about onboarding photographs, and a bank
                    // statement is exported from a banking app.
                    CapturedVia: null)),
            ],
            Approvable: profile.Status == DriverPayoutStatuses.PendingVerification);
    }

    /// <summary>The two evidence slots, in the order the officer reads them.</summary>
    private static IEnumerable<(Guid DocId, string Kind)> PayoutDocuments(DriverPayoutProfileRow profile)
    {
        if (profile.ProofUploadId is { } proof)
        {
            // The kind is on the docs.uploads row and is either bank_statement or
            // passbook_first_page; the column cannot say which, so the label is the slot.
            yield return (proof, DriverPayoutDocumentKinds.ProofOfAccount);
        }

        if (profile.LankaqrUploadId is { } qr)
        {
            yield return (qr, DriverPayoutDocumentKinds.LankaqrCode);
        }
    }

    /// <remarks>
    /// <para>
    /// <b>Forwarded to registry-svc, and that is the BFF rule rather than an accident.</b> Approving
    /// is BR-31.1's versioning transition — supersede the incumbent, then verify the replacement, in
    /// one transaction and in that order, because <c>ux_driver_payout_verified</c> admits one
    /// verified row. That invariant belongs to the service that owns the table and whose repository
    /// already holds its other half. The driver's *identity* verdict is written here for the
    /// opposite reason: it is one column and nobody exposes a route for it.
    /// </para>
    /// <para>
    /// <b>This is a different decision from approving the driver, on the same id.</b> It gets its own
    /// action and its own entity type, because "I checked this bank statement against this account
    /// number" and "I checked this driving licence" are different claims — and an officer refusing
    /// an illegible statement must not thereby refuse somebody's licence and stop them driving.
    /// </para>
    /// </remarks>
    public async Task<DriverPayoutDecisionResponse> ApproveDriverPayoutAsync(
        Guid driverId, Guid officerId, HttpContext context, CancellationToken cancellationToken)
    {
        var before = await RequirePayoutProfileAsync(driverId, cancellationToken);

        var decided = await DecideAsync(driverId, officerId, reason: null, context, cancellationToken);

        audit.Record(
            driverId,
            before: new { status = before.Status, accountNo = before.AccountNo, bank = before.Bank },
            after: new { status = decided.Status, accountNo = decided.AccountNo, bank = decided.Bank },
            action: AdminAuditActions.PayoutProfileApproved,
            entityType: AdminAuditActions.PayoutProfileEntity);

        logger.LogInformation(
            "Officer {OfficerId} approved driver {DriverId}'s payout profile. payout-svc's weekly sweep "
            + "can now pay them (AL-58).",
            officerId,
            driverId);

        return new DriverPayoutDecisionResponse(driverId, decided.Status, Reason: null, decided.VerifiedAt);
    }

    public async Task<DriverPayoutDecisionResponse> RejectDriverPayoutAsync(
        Guid driverId, string reason, Guid officerId, HttpContext context, CancellationToken cancellationToken)
    {
        var before = await RequirePayoutProfileAsync(driverId, cancellationToken);

        var decided = await DecideAsync(driverId, officerId, reason, context, cancellationToken);

        audit.Record(
            driverId,
            before: new { status = before.Status, accountNo = before.AccountNo, bank = before.Bank },
            after: new { status = decided.Status, reason },
            action: AdminAuditActions.PayoutProfileRejected,
            entityType: AdminAuditActions.PayoutProfileEntity);

        return new DriverPayoutDecisionResponse(
            driverId, decided.Status, decided.RejectionReason ?? reason, decided.VerifiedAt);
    }

    private async Task<DriverPayoutDecision> DecideAsync(
        Guid driverId, Guid officerId, string? reason, HttpContext context, CancellationToken cancellationToken)
    {
        var verdict = reason is null ? "approve" : "reject";

        using var request = upstream.Request(
            AdminUpstreams.Registry,
            HttpMethod.Post,
            $"/v1/internal/drivers/{driverId:D}/payout-profile/{verdict}");

        request.Content = System.Net.Http.Json.JsonContent.Create(
            new { officerId = officerId.ToString(), reason }, options: MageRideJson.Options);

        return await upstream.SendAsync<DriverPayoutDecision>(
            AdminUpstreams.Registry, request, context, cancellationToken);
    }

    private async Task<DriverPayoutProfileRow> RequirePayoutProfileAsync(
        Guid driverId, CancellationToken cancellationToken) =>
        await verification.FindDriverPayoutAsync(driverId, cancellationToken)
        ?? throw new MageRideException(
            MageRideErrors.PayoutProfileNotFound,
            $"Driver {driverId} has not submitted a bank and payout profile. Their earnings accrue on their "
            + "wallet and are never lost, but nothing can be paid out until they do (AL-58).");

    // ---------------------------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------------------------

    private async Task<VerificationSubject> RequireSubjectAsync(Guid subjectId, CancellationToken cancellationToken) =>
        await verification.FindSubjectAsync(subjectId, cancellationToken)
        ?? throw new MageRideException(
            MageRideErrors.NotFound,
            $"{subjectId} does not name a driver, a vehicle or a fleet organisation.");

    /// <summary>
    /// Asks registry-svc to re-derive AL-30's state after this service changed a field it owns the
    /// consequences of. Null for anything that has no wizard.
    /// </summary>
    private async Task<OnboardingSettlement?> RecomputeAsync(
        VerificationSubject subject, HttpContext context, CancellationToken cancellationToken)
    {
        if (subject.Type != VerificationSubjectTypes.Vehicle || subject.Mode is not "C")
        {
            // A fleet vehicle has no onboarding_steps rows and registry-svc refuses one outright; a
            // driver's licence has no vehicle to settle. Both derive their verdict from the fields
            // this service just wrote, with nothing to tell anybody.
            return null;
        }

        using var request = upstream.Request(
            AdminUpstreams.Registry, HttpMethod.Post, $"/v1/internal/vehicles/{subject.Id:D}/onboarding/recompute");

        return await upstream.SendAsync<OnboardingSettlement>(
            AdminUpstreams.Registry, request, context, cancellationToken);
    }

    private static string EntityTypeOf(VerificationSubject subject) => subject.Type switch
    {
        VerificationSubjectTypes.Vehicle => AdminAuditActions.VehicleEntity,
        VerificationSubjectTypes.Org => AdminAuditActions.FleetOrgEntity,
        _ => AdminAuditActions.DriverEntity,
    };

    private static ExtractedFieldResponse ToResponse(VerificationField field) => new(
        field.FieldKey, field.FieldValue, field.Source, field.Confidence, field.VerifyStatus);

    private DocumentRefResponse ToResponse(VerificationDocumentRow document) => new(
        document.DocId,
        document.Kind,
        links.Create(document.DocId, DocumentVariants.Thumb),
        links.Create(document.DocId, DocumentVariants.Full),
        document.CapturedVia);

    /// <summary>The field as it goes into an audit image — value included, because that is what changed.</summary>
    private static object Snapshot(VerificationField field) => new
    {
        documentId = field.DocumentId,
        value = field.FieldValue,
        source = field.Source,
        confidence = field.Confidence,
        verifyStatus = field.VerifyStatus,
    };

    // The upstream shapes, declared here rather than shared: they are another service's wire
    // format and this is the only place that reads them.
    private sealed record DriverPayoutDecision(
        string Status, string Bank, string AccountNo, string? RejectionReason, DateTimeOffset? VerifiedAt);

    private sealed record FleetQueuePage(IReadOnlyList<FleetQueueRow>? Items);

    private sealed record FleetQueueRow(
        Guid FleetId,
        string Name,
        string? RegistrationNo,
        string? ContactPhone,
        string Status,
        string? PayoutProfileStatus,
        int DocumentCount,
        DateTimeOffset CreatedAt);

    private sealed record FleetVerificationPayload(
        FleetKycPayload? Kyc,
        string? PayoutProfileStatus,
        FleetPayoutPayload? PayoutProfile,
        IReadOnlyList<FleetDocumentPayload>? Documents);

    private sealed record FleetKycPayload(
        Guid FleetId,
        string Name,
        string? RegistrationNo,
        string? ContactPhone,
        string? ContactEmail,
        string? Address,
        string Status,
        string? RejectionReason,
        DateTimeOffset CreatedAt);

    private sealed record FleetPayoutPayload(
        string Bank,
        string Branch,
        string AccountNo,
        string AccountHolderName,
        string Status,
        string? RejectionReason,
        DateTimeOffset? VerifiedAt);

    private sealed record FleetDocumentPayload(Guid DocId, string Kind, DateTimeOffset CreatedAt);

    private sealed record FleetDecisionPayload(FleetKycPayload? Fleet, FleetPayoutPayload? PayoutProfile);

    private sealed record FleetVehicleDecisionPayload(string? DocsStatus);

    /// <summary>registry-svc's <c>OnboardingStatusResponse</c>, narrowed to what a decision needs.</summary>
    private sealed record OnboardingSettlement(string Status, string OnboardingStatus, string? NextStep);
}

/// <summary>
/// The opaque position of a queue page: the row's submitted-at and its subject id.
/// </summary>
/// <remarks>
/// Both halves, because <c>submitted_at</c> is <c>max(created_at)</c> over a batch of documents
/// saved in one transaction and is therefore not unique — two applications uploaded in the same
/// millisecond would drop or repeat a row at a page boundary if the id were not in the key.
/// </remarks>
internal static class QueueCursors
{
    public static string Encode(DateTimeOffset at, Guid id) =>
        CursorCodec.Unsigned.Encode(new Position(at, id));

    public static (DateTimeOffset? At, Guid? Id) Decode(string? cursor) =>
        CursorCodec.Unsigned.TryDecode<Position>(cursor, out var position) && position is not null
            ? (position.At, position.Id)
            : (null, null);

    private sealed record Position(DateTimeOffset At, Guid Id);
}
