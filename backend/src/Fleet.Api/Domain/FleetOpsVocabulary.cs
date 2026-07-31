using System.Collections.Frozen;

namespace MageRide.Fleet.Domain;

/// <summary>
/// The <c>registry.vehicles.status</c> lifecycle, as the Fleet Portal sees it (migration 0303).
/// </summary>
/// <remarks>
/// A fleet vehicle enters <see cref="Pending"/> and leaves it only through a Verification
/// Officer's decision. <b>There is no auto-approval here</b>, and that is the difference from the
/// Driver App: AL-30's four-step wizard auto-approves a Mode C vehicle once every step verifies,
/// and registry-svc refuses Mode A/B on that route outright ("in-app vehicle onboarding is Mode C
/// only"). A bus's route permit is a legal document a person reads.
/// </remarks>
public static class FleetVehicleStatuses
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Deactivated = "DEACTIVATED";
}

/// <summary>
/// <c>fleet.yaml</c>'s <c>FleetVehicle.docsStatus</c> — whether AL-50's required slots are complete.
/// </summary>
/// <remarks>
/// <b>Derived, never stored.</b> `fleet.yaml` describes <c>docs_pending</c> as "the state a
/// bulk-CSV row starts in", which makes it sound like a column; it is not one, and a column would
/// be a second opinion about the same documents. Every answer is computed from the vehicle's
/// current <c>registry.documents</c> rows by <see cref="VehicleDocumentSlots"/>, so a slot
/// verified by an officer moves the vehicle to <see cref="Complete"/> with nothing having been
/// written to the vehicle at all.
/// </remarks>
public static class VehicleDocsStatuses
{
    public const string Pending = "docs_pending";
    public const string Complete = "docs_complete";
}

/// <summary>
/// AL-50 / SCR-FP-004's four named document slots, in both spellings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two vocabularies, and the difference is not cosmetic.</b> The wire names are the slot labels
/// SCR-FP-004 prints — an operator uploads a "registration copy (CR book)" and a "route permit" —
/// and the stored names are the <c>registry.documents.kind</c> CHECK values, which predate the
/// Fleet Portal and are shared with the Driver App's Mode C wizard and with ocr-svc's extraction
/// prompts. `fleet.yaml` states the mapping in <c>uploadVehicleDocument</c>'s description:
/// <c>registration_copy → registration</c> and <c>route_permit → permit</c>.
/// </para>
/// <para>
/// <b>The required set is a function of the mode, and only of the mode.</b> Registration, insurance
/// and revenue licence for every vehicle; the route permit for Mode A, because Sri Lankan
/// passenger transport legally requires one (US-27.3). A Mode B school van needs no permit and
/// demanding one would make an office shuttle unapprovable.
/// </para>
/// </remarks>
public static class VehicleDocumentKinds
{
    // Stored — registry.documents.kind (migration 0305).
    public const string Registration = "registration";
    public const string Insurance = "insurance";
    public const string RevenueLicense = "revenue_license";
    public const string Permit = "permit";

    // On the wire — SCR-FP-004's slot labels, per fleet.yaml's `kind` enum.
    public const string RegistrationCopyWire = "registration_copy";
    public const string InsuranceWire = "insurance";
    public const string RevenueLicenseWire = "revenue_license";
    public const string RoutePermitWire = "route_permit";

    /// <summary>The three slots every fleet vehicle needs, whatever its mode (US-27.3).</summary>
    public static readonly string[] RequiredForEveryMode = [Registration, Insurance, RevenueLicense];

    /// <summary>Every slot the portal renders, in SCR-FP-004's order.</summary>
    public static readonly string[] All = [Registration, Insurance, RevenueLicense, Permit];

    private static readonly FrozenDictionary<string, string> WireToStored =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RegistrationCopyWire] = Registration,
            [InsuranceWire] = Insurance,
            [RevenueLicenseWire] = RevenueLicense,
            [RoutePermitWire] = Permit,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> StoredKinds = All.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The stored kind for a slot label, or <see langword="null"/> for anything else.</summary>
    /// <remarks>
    /// A stored name is <em>not</em> accepted in its place, except where the two coincide
    /// (<c>insurance</c>, <c>revenue_license</c>). Accepting both spellings would leave two ways to
    /// name one slot on a contract that declares an enum, and a client sending
    /// <c>registration</c> is a client written against the wrong half of the mapping.
    /// </remarks>
    public static string? ToStoredKind(string? wireKind) =>
        wireKind is not null && WireToStored.TryGetValue(wireKind, out var stored) ? stored : null;

    /// <summary>Whether a stored kind is one of AL-50's four.</summary>
    public static bool IsFleetSlot(string? storedKind) =>
        storedKind is not null && StoredKinds.Contains(storedKind);

    /// <summary>
    /// The slots this vehicle must have verified before it can be approved (AL-50, extends AL-10).
    /// </summary>
    public static IReadOnlyList<string> RequiredFor(string mode) =>
        string.Equals(mode, FleetModes.PublicTransport, StringComparison.Ordinal)
            ? [Registration, Insurance, RevenueLicense, Permit]
            : RequiredForEveryMode;
}

/// <summary>
/// The per-slot chip SCR-FP-004 renders (<c>fleet.yaml</c> <c>VehicleDocumentSlot.status</c>).
/// </summary>
public static class VehicleDocumentSlotStatuses
{
    /// <summary>Uploaded, read, and nothing on it is waiting for a Verification Officer.</summary>
    public const string Verified = "verified";

    /// <summary>Uploaded, and something on it is not settled — an unread field, or a lapsed expiry.</summary>
    public const string Pending = "pending";

    /// <summary>Nothing has been uploaded into this slot.</summary>
    public const string Missing = "missing";
}

/// <summary><c>registry.documents.status</c> (migration 0305), spelled as this service reads it.</summary>
/// <remarks>
/// A copy of registry-svc's <c>DocumentStatuses</c> rather than a shared type — the same judgement
/// ocr-svc's <c>DocumentKinds</c> records. The two services agree on these strings through a
/// column, and a shared constant would make a rename in one bounded context look free.
/// </remarks>
public static class VehicleDocumentStatuses
{
    public const string Valid = "VALID";
    public const string Expiring = "EXPIRING";
    public const string Expired = "EXPIRED";
    public const string Rejected = "REJECTED";
}

/// <summary><c>registry.document_fields.verify_status</c> (AL-29).</summary>
public static class DocumentFieldVerifyStatuses
{
    public const string AutoVerified = "auto_verified";
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";

    /// <summary>Whether a field in this state holds its slot out of <c>verified</c>.</summary>
    public static bool BlocksApproval(string? verifyStatus) =>
        string.Equals(verifyStatus, Pending, StringComparison.Ordinal);
}

/// <summary><c>registry.document_fields.source</c> (AL-29). This service only ever writes <c>ai</c>.</summary>
public static class DocumentFieldSources
{
    public const string Ai = "ai";
    public const string Manual = "manual";
}

/// <summary>
/// The field keys AL-50's four slots carry, mirroring ocr-svc's <c>DocumentFieldKeys</c>.
/// </summary>
/// <remarks>
/// Reproduced rather than referenced, for the reason ocr-svc's own copy records: the two services
/// agree on these strings over HTTP, and a shared constant would make a wire contract look like a
/// compile-time one. <see cref="ExpiryFieldFor"/> is the seam between AL-29's extraction and
/// E-03's sweep — whatever comes back under that key becomes
/// <c>registry.documents.expires_at</c>.
/// </remarks>
public static class VehicleDocumentFieldKeys
{
    public const string InsuranceExpiry = "insurance_expiry";
    public const string RevenueNo = "revenue_no";
    public const string RevenueExpiry = "revenue_expiry";
    public const string PermitExpiry = "permit_expiry";
    public const string PermitNo = "permit_no";
    public const string PermitRoute = "permit_route";
    public const string RegNoMatch = "reg_no_match";
    public const string PlateText = "plate_text";

    /// <summary>The expiry field of a slot, or <see langword="null"/> where the document has none.</summary>
    /// <remarks>
    /// A CR book does not expire — it is a title document, not a certificate — which is why
    /// <c>registration</c> is absent rather than mapped to a key nothing returns.
    /// </remarks>
    public static string? ExpiryFieldFor(string storedKind) => storedKind switch
    {
        VehicleDocumentKinds.Insurance => InsuranceExpiry,
        VehicleDocumentKinds.RevenueLicense => RevenueExpiry,
        VehicleDocumentKinds.Permit => PermitExpiry,
        _ => null,
    };
}

/// <summary><c>registry.fleet_schedules.status</c> (migration 0314).</summary>
public static class FleetScheduleStatuses
{
    public const string Scheduled = "SCHEDULED";

    /// <summary>A <c>trips.sessions</c> row opened on the vehicle. The alarm will not fire.</summary>
    public const string Started = "STARTED";

    /// <summary>The alarm offset passed with no session. Terminal for the alarm, not for the journey.</summary>
    public const string Missed = "MISSED";

    public const string Cancelled = "CANCELLED";
}

/// <summary><c>registry.fleet_bulk_jobs.status</c> (migration 0314), matching <c>fleet.yaml</c>.</summary>
public static class BulkJobStatuses
{
    public const string Processing = "PROCESSING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
}

/// <summary><c>registry.fleet_bulk_job_rows.status</c> (migration 0314).</summary>
public static class BulkRowStatuses
{
    public const string Imported = "IMPORTED";
    public const string Failed = "FAILED";
}

/// <summary>
/// AL-09's canonical vehicle types, as the <c>registry.vehicles.vehicle_type</c> CHECK spells them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identical to registry-svc's <c>VehicleTypes</c>, and <c>car</c> is refused for its reason:</b>
/// AL-09 maps <c>car → sedan</c> as a one-time data migration, not an input alias, and rewriting it
/// silently would hide an un-updated client until a fare tariff or a map marker disagreed. Unlike
/// registry-svc, <c>bus</c> is an ordinary value here — the Fleet Portal is the surface Mode A
/// belongs on, and refusing it would leave a bus company unable to onboard a bus.
/// </para>
/// <para>
/// <b><see cref="Train"/> is the one type this surface refuses.</b> US-2.17/2.18 make trains
/// admin-only and D3' gives them their own route family (<c>POST /v1/admin/trains</c>): a railway
/// is not an operator with a Fleet Portal login, and a private company registering one would put a
/// train on the passenger map that no admin decided to run. Refused as <c>403 mode-not-allowed</c>
/// rather than <c>400</c>, the same distinction registry-svc draws for <c>bus</c> — the value is
/// real, the surface is wrong.
/// </para>
/// </remarks>
public static class FleetVehicleTypes
{
    /// <summary>Admin-only (US-2.17/2.18). Listed so the refusal names a type rather than a typo.</summary>
    public const string Train = "train";

    public static readonly string[] All =
    [
        "motorbike", "three_wheeler", "flex", "sedan", "mini_van", "van", "truck", "mini_truck",
        "bus", Train,
    ];

    private static readonly FrozenSet<string> Known = All.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsKnown(string? vehicleType) => vehicleType is not null && Known.Contains(vehicleType);

    /// <summary>Whether this type may be onboarded through the Fleet Portal at all.</summary>
    public static bool IsFleetOnboardable(string vehicleType) =>
        !string.Equals(vehicleType, Train, StringComparison.Ordinal);
}
