using MageRide.Shared.Primitives;

namespace MageRide.Fleet.Domain;

// =================================================================================================
// The rows C059's surface reads and writes. Everything here is a projection of a table another
// migration declares; the column comments there are the authority for what each field means.
// =================================================================================================

/// <summary>A row of <c>registry.documents</c> owned by a fleet (migration 0305, AL-50).</summary>
/// <remarks>
/// <c>ck_documents_owner</c> is an XOR, so a fleet-uploaded vehicle document sets <c>fleet_id</c>
/// and must <b>not</b> also set <c>driver_id</c> — the C003 note the C058 handoff restated for this
/// component. There is therefore no <c>DriverId</c> on this record: a row this service can read is
/// a row with no driver on it.
/// </remarks>
public sealed record VehicleDocument(
    Guid Id,
    Guid FleetId,
    Guid VehicleId,
    string Kind,
    string FileUrl,
    DateTimeOffset? ExpiresAt,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>A row of <c>registry.document_fields</c> (migration 0305, AL-29).</summary>
public sealed record VehicleDocumentField(
    Guid Id,
    Guid DocumentId,
    string FieldKey,
    string? FieldValue,
    decimal? Confidence,
    string Source,
    string VerifyStatus);

/// <summary>
/// One of SCR-FP-004's named slots, resolved to the chip the portal draws.
/// </summary>
/// <param name="Status">
/// <c>verified</c> | <c>pending</c> | <c>missing</c>. Computed, never stored — see
/// <see cref="VehicleDocumentSlots"/> for the rule and why each half of it is there.
/// </param>
/// <param name="IsRequired">
/// Whether AL-50 makes this slot mandatory for <em>this</em> vehicle. The route permit is required
/// for Mode A and optional for Mode B, so the same slot answers differently on two vehicles and the
/// portal cannot derive it from the kind alone.
/// </param>
public sealed record VehicleDocumentSlot(
    string Kind,
    string Status,
    bool IsRequired,
    Guid? DocumentId,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<VehicleDocumentField> Fields);

/// <summary>
/// The rule that turns a vehicle's documents into SCR-FP-004's four chips (AL-50, US-27.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>One place, because three things read it</b> — the portal's document screen, the
/// <c>docsStatus</c> on every vehicle response, and the approval gate on the internal plane. Three
/// derivations of "is this vehicle's paperwork complete" would eventually disagree, and the one
/// that matters is the gate.
/// </para>
/// <para>
/// <b>The current document of a kind is the newest non-REJECTED one</b>, which is registry-svc's
/// convention (<c>ListCurrentForVehicleAsync</c>) and is what makes a renewal supersede rather than
/// merely accompany its predecessor: an operator who re-uploads a blurred insurance certificate
/// must not be held down by the blurred one.
/// </para>
/// <para>
/// <b>An expired document is <c>pending</c>, not <c>verified</c>.</b> US-27.3 keeps expiry and
/// approval separate — expiry "auto-suspends dispatch" under E-03, which is a different
/// consequence — but a certificate that has already lapsed cannot be the evidence a vehicle is
/// approved on. Reading it as verified would let an operator upload last year's cover and be
/// approved on it.
/// </para>
/// </remarks>
public static class VehicleDocumentSlots
{
    /// <summary>Every slot for a vehicle of this mode, in SCR-FP-004's order.</summary>
    public static IReadOnlyList<VehicleDocumentSlot> For(
        string mode,
        IReadOnlyCollection<VehicleDocument> documents,
        IReadOnlyCollection<VehicleDocumentField> fields)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(fields);

        var required = VehicleDocumentKinds.RequiredFor(mode);

        // The whole of AL-50's slot set is rendered, required or not: SCR-FP-004 draws four boxes
        // and a Mode B vehicle's permit box is an empty optional one, not an absent one.
        return
        [
            .. VehicleDocumentKinds.All.Select(kind =>
            {
                var current = Current(documents, kind);

                if (current is null)
                {
                    return new VehicleDocumentSlot(
                        kind, VehicleDocumentSlotStatuses.Missing, required.Contains(kind), null, null, []);
                }

                var documentFields = fields.Where(field => field.DocumentId == current.Id).ToArray();

                return new VehicleDocumentSlot(
                    kind,
                    StatusOf(current, documentFields),
                    required.Contains(kind),
                    current.Id,
                    current.ExpiresAt,
                    documentFields);
            }),
        ];
    }

    /// <summary>
    /// Whether every slot AL-50 requires for this mode is <c>verified</c> — the approval gate.
    /// </summary>
    public static bool AreRequiredSlotsVerified(IReadOnlyCollection<VehicleDocumentSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        return slots
            .Where(slot => slot.IsRequired)
            .All(slot => string.Equals(slot.Status, VehicleDocumentSlotStatuses.Verified, StringComparison.Ordinal));
    }

    /// <summary>The required slots that are not yet verified, named for the refusal message.</summary>
    public static IReadOnlyList<string> UnverifiedRequiredSlots(IReadOnlyCollection<VehicleDocumentSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        return
        [
            .. slots
                .Where(slot => slot.IsRequired
                    && !string.Equals(slot.Status, VehicleDocumentSlotStatuses.Verified, StringComparison.Ordinal))
                .Select(slot => $"{slot.Kind} ({slot.Status})"),
        ];
    }

    /// <summary><c>fleet.yaml</c>'s <c>docsStatus</c>, derived from the same slots.</summary>
    public static string DocsStatus(IReadOnlyCollection<VehicleDocumentSlot> slots) =>
        AreRequiredSlotsVerified(slots) ? VehicleDocsStatuses.Complete : VehicleDocsStatuses.Pending;

    private static VehicleDocument? Current(IEnumerable<VehicleDocument> documents, string kind) =>
        documents
            .Where(document => string.Equals(document.Kind, kind, StringComparison.Ordinal)
                && !string.Equals(document.Status, VehicleDocumentStatuses.Rejected, StringComparison.Ordinal))
            .OrderByDescending(document => document.CreatedAt)
            .ThenByDescending(document => document.Id)
            .FirstOrDefault();

    private static string StatusOf(VehicleDocument document, IReadOnlyCollection<VehicleDocumentField> fields)
    {
        // A field nobody has settled is the AL-29 officer queue's whole subject, and it is the
        // usual reason a slot is not verified: ocr-svc read the expiry with low confidence, or
        // could not read it at all and returned the key with a null value.
        if (fields.Any(field => DocumentFieldVerifyStatuses.BlocksApproval(field.VerifyStatus)))
        {
            return VehicleDocumentSlotStatuses.Pending;
        }

        if (string.Equals(document.Status, VehicleDocumentStatuses.Expired, StringComparison.Ordinal))
        {
            return VehicleDocumentSlotStatuses.Pending;
        }

        // A slot whose document yielded no fields at all has not been read by anything. That
        // happens when ocr-svc was unreachable, and it must not read as verified — the fields are
        // written pending in that case, so this is a belt-and-braces clause for a document
        // inserted by some other path.
        return fields.Count == 0
            ? VehicleDocumentSlotStatuses.Pending
            : VehicleDocumentSlotStatuses.Verified;
    }
}

/// <summary>A row of <c>registry.fleet_assignments_fleet</c> (migrations 0306, 0314, 1807).</summary>
/// <param name="IsActive">
/// The validity window evaluated by the database at read time. US-13.9's auto-expiry is this
/// column going false with nothing having been written.
/// </param>
public sealed record FleetAssignment(
    Guid Id,
    Guid FleetId,
    Guid VehicleId,
    Guid DriverId,
    DateTimeOffset AssignedAt,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? DriverName,
    string? DriverPhone,
    string RegistrationNumber,
    bool IsActive);

/// <summary>A row of <c>registry.fleet_schedules</c> (migration 0314, US-13.11).</summary>
public sealed record FleetSchedule(
    Guid Id,
    Guid FleetId,
    Guid VehicleId,
    Guid? RouteId,
    DateTimeOffset DepartAt,
    short NotStartedAlarmMinutes,
    string Status,
    DateTimeOffset? AlarmRaisedAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// A schedule whose alarm is due, with the people the alarm has to reach (US-13.11/13.11b).
/// </summary>
/// <param name="DriverIds">
/// Every driver whose assignment covers this vehicle at the departure instant. Usually one; two
/// when a shift changes over the departure, and the alarm goes to both rather than to a guess.
/// </param>
public sealed record DueScheduleAlarm(
    Guid Id,
    Guid FleetId,
    Guid VehicleId,
    string RegistrationNumber,
    DateTimeOffset DepartAt,
    short NotStartedAlarmMinutes,
    IReadOnlyList<Guid> DriverIds,
    IReadOnlyList<Guid> MemberIds);

/// <summary>One vehicle's latest position, from <c>telemetry.positions_fleet</c> (US-13.3).</summary>
public sealed record FleetVehiclePosition(
    Guid VehicleId,
    string? RegistrationNumber,
    double Lat,
    double Lng,
    short? HeadingDeg,
    float? SpeedMps,
    DateTimeOffset SampleTs);

/// <summary>
/// One vehicle's line in the analytics table (US-13.4).
/// </summary>
/// <param name="DistanceKm">
/// Great-circle distance along consecutive telemetry samples of the period, in kilometres. Not
/// road distance — nothing in this build map-matches a completed journey — and the difference is
/// argued at the query.
/// </param>
/// <param name="EarningsMinor">
/// <see langword="null"/>, always, and deliberately: see <c>FleetInsightsRepository</c>. A fleet's
/// Mode A/B vehicles earn no fares on this platform.
/// </param>
public sealed record VehicleAnalytics(
    Guid VehicleId,
    string RegistrationNumber,
    int TripCount,
    double DistanceKm,
    double ActiveHours,
    double UtilisationPct,
    long? EarningsMinor);

/// <summary>A row of <c>registry.fleet_bulk_jobs</c> (migration 0314, US-13.1).</summary>
public sealed record BulkVehicleJob(
    Guid Id,
    Guid FleetId,
    Guid RequestedBy,
    string Status,
    int TotalRows,
    int SucceededRows,
    int FailedRows,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>A row of <c>registry.fleet_bulk_job_rows</c> — one CSV line and what became of it.</summary>
public sealed record BulkVehicleRow(
    int RowNumber,
    string RegistrationNumber,
    string? VehicleType,
    string? Mode,
    string? ModeBBilling,
    int? DefaultMonthlyFareMinor,
    string Status,
    Guid? VehicleId,
    string? ErrorCode,
    string? ErrorDetail);

/// <summary>One of the org's operational polygons (<c>spatial.geofences</c>, migrations 1401/1408).</summary>
/// <remarks>
/// The ring is <c>MageRide.Shared.Primitives.GeoPoint</c>, the platform's coordinate — which
/// validates its own bounds on construction, so a polygon that exists is one whose every vertex is
/// on the earth. The endpoint checks the bounds first all the same, because an
/// <c>ArgumentOutOfRangeException</c> is a 500 and a malformed request is a 400.
/// </remarks>
public sealed record FleetGeofence(Guid Id, Guid FleetId, string? Name, IReadOnlyList<GeoPoint> Polygon);
