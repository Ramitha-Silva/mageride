using System.Text.Json.Serialization;
using MageRide.Analytics.Domain;

namespace MageRide.AdminBff.Endpoints;

// The wire shapes of backend/contracts/admin-bff.yaml, one record per schema. Every property is
// nullable on the way in, deliberately: a missing required field has to come back as
// `400 validation-failed` with the field named, not as a framework 400 with no error code.

/// <summary>`ReasonBody` — the body of every suspension and rejection.</summary>
public sealed record ReasonBody(string? Reason);

/// <summary>`GET /v1/admin/dashboard` — the unfiltered landing view (US-14.6).</summary>
public sealed record AdminDashboardResponse(DashboardKpis Kpis, DashboardLive Live);

/// <summary>`GET /v1/admin/dashboard/stats` (AL-38, US-24.7).</summary>
public sealed record DashboardStatsResponse(
    string Period, StatsRangeResponse Range, DashboardKpis Kpis, DashboardDeltas DeltaVsPrev, DashboardLive Live);

/// <summary>The half-open business-date window a period resolved to, Asia/Colombo (D-38).</summary>
public sealed record StatsRangeResponse(DateOnly From, DateOnly To);

/// <summary>`ModerationResult`.</summary>
public sealed record ModerationResultResponse(Guid SubjectId, string Status, string? Reason);

/// <summary>`ReportRow` — one row of the moderation inbox.</summary>
public sealed record ReportRowResponse(
    Guid ReportId,
    Guid VehicleId,
    Guid? ReporterId,
    string? Reason,
    string Status,
    int? ConfirmedCount,
    DateTimeOffset CreatedAt);

/// <summary>`POST /v1/admin/reports/{reportId}/resolve`.</summary>
public sealed record ResolveReportBody(string? Decision, string? Note);

/// <summary>What a moderation decision produced. `vehicleDelisted` is US-12.6's third confirmation.</summary>
public sealed record ResolveReportResponse(
    Guid ReportId, string Status, int ConfirmedCount, bool VehicleDelisted);

/// <summary>`TicketRow`.</summary>
public sealed record TicketRowResponse(
    Guid TicketId,
    Guid UserId,
    string Category,
    string Status,
    string? Description,
    string? Response,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

/// <summary>`POST /v1/admin/support/tickets/{ticketId}/resolve`.</summary>
public sealed record ResolveTicketBody(string? Response);

// -------------------------------------------------------------------------------------------------
// Verification (AL-39, C063)
// -------------------------------------------------------------------------------------------------

/// <summary>`DriverQueueRow` — SCR-AP-003's driving-licence tab.</summary>
public sealed record DriverQueueRowResponse(
    Guid DriverId,
    string Name,
    DateTimeOffset SubmittedAt,
    IReadOnlyList<string> FlaggedFields,
    string Status);

/// <summary>`VehicleQueueRow` — SCR-AP-003's vehicle-registration tab.</summary>
public sealed record VehicleQueueRowResponse(
    Guid VehicleId,
    string RegNo,
    Guid? OwnerDriverId,
    DateTimeOffset SubmittedAt,
    IReadOnlyList<string> FlaggedFields,
    string Status);

/// <summary>`OrgQueueRow` — SCR-AP-003's fleet-org tab (AL-49).</summary>
/// <param name="KycStatus">
/// Whether there is KYC evidence to read at all: <c>complete</c> once the organisation carries a
/// business registration and a contact number, <c>incomplete</c> until it does (US-13.A7). The
/// payout profile's own state is <paramref name="PayoutProfileStatus"/> — the two are separate
/// decisions and one field could not carry both.
/// </param>
public sealed record OrgQueueRowResponse(
    Guid OrgId,
    string Name,
    string KycStatus,
    int VehicleCount,
    string Status,
    string? PayoutProfileStatus);

/// <summary>
/// `DocumentRef` — one document with the two links SCR-AP-003a/003b open it by.
/// </summary>
/// <remarks>
/// <b>Δ C063: both links point at <c>GET /v1/admin/documents/{docId}</c>, not at the bucket.</b>
/// AL-39 wants short-lived signed object-storage URLs <em>and</em> a <c>DOC_VIEW</c> row per read;
/// a bare pre-signed URL here would give the first and silently drop the second. The audited route
/// mints the signed URL per view and redirects to it — see <c>IDocumentLinks</c>.
/// </remarks>
public sealed record DocumentRefResponse(
    Guid DocId, string Kind, string ThumbUrl, string FullUrl, string? CapturedVia);

/// <summary>`ExtractedField` (_shared.yaml) — one AL-29 field with its provenance.</summary>
public sealed record ExtractedFieldResponse(
    string Key, string? Value, string Source, decimal? Confidence, string VerifyStatus);

/// <summary>One row of SCR-AP-003a's decision rail.</summary>
public sealed record VerificationStepResponse(string Step, string Status);

/// <summary>Who the officer is looking at.</summary>
public sealed record VerificationSubjectResponse(Guid Id, string Type, string? DisplayName);

/// <summary>`VerificationDetail` — SCR-AP-003a/003c.</summary>
public sealed record VerificationDetailResponse(
    VerificationSubjectResponse Subject,
    IReadOnlyList<ExtractedFieldResponse> Fields,
    IReadOnlyList<DocumentRefResponse> Documents,
    IReadOnlyList<VerificationStepResponse> Steps,
    bool Approvable);

/// <summary>The organisation KYC an officer reads before approving (US-13.A7).</summary>
public sealed record OrgKycResponse(
    Guid OrgId,
    string Name,
    string? RegistrationNo,
    string? ContactPhone,
    string? ContactEmail,
    string? Address,
    string Status,
    string? RejectionReason,
    OrgPayoutResponse? PayoutProfile);

/// <summary>The bank details AL-49's pay sheet will render once they are verified.</summary>
public sealed record OrgPayoutResponse(
    string Bank,
    string Branch,
    string AccountNo,
    string AccountHolderName,
    string Status,
    string? RejectionReason,
    DateTimeOffset? VerifiedAt);

/// <summary>`GET /v1/admin/verification/org/{orgId}` — SCR-AP-003c.</summary>
public sealed record OrgVerificationResponse(
    OrgKycResponse Kyc, string? PayoutProfileStatus, IReadOnlyList<DocumentRefResponse> Documents);

/// <summary>
/// `PUT /v1/admin/verification/{subjectId}/fields/{fieldKey}` — omit `value` to confirm as is.
/// </summary>
public sealed record DecideFieldBody(string? Value);

/// <summary>What one field decision left behind.</summary>
public sealed record DecideFieldResponse(
    ExtractedFieldResponse Field, string StepStatus, bool Approvable);

/// <summary>`VerificationDecision` — the answer to approve and to reject.</summary>
/// <param name="MerchantBound">
/// D-11's OnePay bind. Always <see langword="false"/> today and deliberately so: registry-svc's
/// <c>POST /v1/internal/vehicles/{id}/merchant</c> requires a `merchantId` and nothing on this
/// platform onboards one, so a `true` here would be a claim that settlement has a payee.
/// </param>
public sealed record VerificationDecisionResponse(
    Guid SubjectId, string Status, string? Reason, bool MerchantBound);

/// <summary>`Tariff` — one Mode C rate-card row (US-14.4).</summary>
public sealed record TariffResponse(
    string VehicleType,
    long FirstKmMinor,
    long PerKmMinor,
    int PeakSurchargePct,
    int NightSurchargePct,
    string Currency);

/// <summary>`PeakWindow` — Asia/Colombo wall-clock; `endLocal` may wrap midnight.</summary>
public sealed record PeakWindowResponse(string Kind, string StartLocal, string EndLocal, int MultiplierPct);

/// <summary>`PUT /v1/admin/fares/tariffs`.</summary>
public sealed record UpdateTariffsBody(
    DateTimeOffset? EffectiveFrom,
    IReadOnlyList<TariffInput>? Tariffs,
    IReadOnlyList<PeakWindowInput>? PeakWindows);

/// <inheritdoc cref="UpdateTariffsBody"/>
public sealed record TariffInput(
    string? VehicleType,
    long? FirstKmMinor,
    long? PerKmMinor,
    int? PeakSurchargePct,
    int? NightSurchargePct,
    string? Currency);

/// <inheritdoc cref="UpdateTariffsBody"/>
public sealed record PeakWindowInput(string? Kind, string? StartLocal, string? EndLocal, int? MultiplierPct);

/// <summary>The published version, echoed back so the Config screen renders what actually landed.</summary>
public sealed record TariffsResponse(
    DateTimeOffset EffectiveFrom,
    IReadOnlyList<TariffResponse> Tariffs,
    IReadOnlyList<PeakWindowResponse> PeakWindows);

/// <summary>`GeoPoint` — <c>{"lat":…,"lng":…}</c> (D6' §2.2).</summary>
public sealed record GeoPointBody(double? Lat, double? Lng);

/// <summary>`OperatingCityInput`.</summary>
public sealed record OperatingCityBody(
    string? Code, string? NameEn, string? NameSi, string? NameTa, GeoPointBody? Centroid, int? SortOrder);

/// <summary>`PATCH /v1/admin/config/cities/{cityCode}` — every field optional.</summary>
public sealed record UpdateOperatingCityBody(
    string? NameEn, string? NameSi, string? NameTa, GeoPointBody? Centroid, int? SortOrder, bool? Active);

/// <summary>`OperatingCity`.</summary>
public sealed record OperatingCityResponse(
    string Code,
    string NameEn,
    string NameSi,
    string NameTa,
    GeoPointBody Centroid,
    int SortOrder,
    bool Active);

/// <summary>`FeatureFlag` — Δ C062; URD §2.3's feature-flag row had no contract.</summary>
public sealed record FeatureFlagResponse(
    string Key,
    bool Enabled,
    string? Description,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt);

/// <summary>`PUT /v1/admin/config/feature-flags/{key}` — Δ C062.</summary>
public sealed record SetFeatureFlagBody(bool? Enabled, string? Description);

/// <summary>`TrainInput` (US-2.17/2.18).</summary>
public sealed record TrainBody(string? Name, string? TrainNumber, string? RouteId, bool? Active);

/// <summary>`Train`.</summary>
public sealed record TrainResponse(Guid TrainId, string Name, string TrainNumber, Guid? RouteId, bool Active);

/// <summary>`POST /v1/admin/announcements` (US-14.8, D-26).</summary>
public sealed record PublishAnnouncementBody(
    IReadOnlyDictionary<string, string>? MessageByLang,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    bool? Push);

/// <summary>The 201 of an announcement.</summary>
public sealed record AnnouncementResponse(Guid BroadcastId);

/// <summary>`AuditEvent` — one row of the immutable log (US-19.3).</summary>
/// <remarks>
/// <c>before</c>, <c>after</c> and <c>detail</c> are re-emitted as the JSON they were stored as
/// rather than re-serialised from a CLR shape: an image written by a component that has since
/// changed must come back exactly as it was written, or the audit trail edits its own history on
/// read.
/// </remarks>
public sealed record AuditEventResponse(
    Guid EventId,
    Guid? ActorId,
    string? ActorRole,
    string Action,
    Guid? SubjectId,
    string? SubjectType,
    [property: JsonPropertyName("before")] System.Text.Json.Nodes.JsonNode? Before,
    [property: JsonPropertyName("after")] System.Text.Json.Nodes.JsonNode? After,
    System.Text.Json.Nodes.JsonNode? Detail,
    string? Ip,
    DateTimeOffset OccurredAt);

/// <summary>`GET /v1/admin/session` — Δ C062; the post-sign-in bootstrap (URD §2.2).</summary>
public sealed record AdminSessionResponse(
    Guid UserId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<AdminPermissionResponse> Permissions,
    IReadOnlyList<AdminMenuGroupResponse> Menu,
    bool MfaRequired);

/// <summary>One URD §2.3 row as this caller holds it.</summary>
public sealed record AdminPermissionResponse(
    string FeatureArea, string Label, string Symbol, IReadOnlyList<string> Grants, string? Qualifier, bool OwnScope);

/// <summary>One nav group of the role-scoped menu manifest.</summary>
public sealed record AdminMenuGroupResponse(
    string Key, string LabelKey, IReadOnlyList<AdminMenuItemResponse> Items);

/// <inheritdoc cref="AdminMenuGroupResponse"/>
public sealed record AdminMenuItemResponse(string Key, string LabelKey, string Path, string OwnedBy);

/// <summary>SCR-AP-003 tab 4 — a driver awaiting a bank &amp; payout decision (AL-58, AL-59).</summary>
/// <param name="Status">
/// The driver's <b>identity</b> verdict, as on the other tabs. Every row here is by construction
/// awaiting a payout decision, so repeating that would say nothing.
/// </param>
public sealed record DriverPayoutQueueRowResponse(
    Guid DriverId,
    string Name,
    string Bank,
    string AccountNo,
    DateTimeOffset SubmittedAt,
    bool HasProof,
    bool HasLankaQr,
    string Status);

/// <summary>SCR-AP-003c's sibling for a driver — what the officer decides on (AL-58, AL-59).</summary>
public sealed record DriverPayoutVerificationResponse(
    Guid DriverId,
    string Name,
    string Bank,
    string Branch,
    string AccountNo,
    string AccountHolderName,
    string Status,
    string? RejectionReason,
    DateTimeOffset? VerifiedAt,
    IReadOnlyList<DocumentRefResponse> Documents,
    bool Approvable);

/// <summary>The verdict, echoed back with the version it landed on.</summary>
public sealed record DriverPayoutDecisionResponse(
    Guid DriverId, string Status, string? Reason, DateTimeOffset? VerifiedAt);
