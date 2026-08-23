using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MageRide.Shared.Errors;

/// <summary>
/// The stable kebab error-code registry (D3' §0 "Errors").
/// <para>
/// Every error response the platform emits carries a <c>type</c> of
/// <c>https://mageride.lk/errors/{code}</c> where <c>{code}</c> is one of these keys. Codes are
/// globally unique across services so a client can branch on the code alone.
/// </para>
/// <para>
/// Codes named by a spec are declared here. A service that needs a code of its own registers it
/// at start-up with <see cref="Register"/> — the registry rejects duplicates, which is what keeps
/// the key space collision-free.
/// </para>
/// </summary>
public static partial class MageRideErrors
{
    /// <summary>Base of the RFC 7807 <c>type</c> URI (D3' §0).</summary>
    public const string TypeUriBase = "https://mageride.lk/errors/";

    // ---------------------------------------------------------------------------------------
    // Cross-cutting — owned by the kernel. Not individually named by a spec; see the C002
    // handoff note in build/progress.md.
    // ---------------------------------------------------------------------------------------

    /// <summary>Request body or query failed validation. Carries an <c>errors</c> extension.</summary>
    public static readonly ErrorCode ValidationFailed = new("validation-failed", 400, "Validation failed");

    /// <summary>Malformed request that is not a field-level validation failure.</summary>
    public static readonly ErrorCode BadRequest = new("bad-request", 400, "Bad request");

    /// <summary>No credential, or a credential that failed validation.</summary>
    public static readonly ErrorCode Unauthorized = new("unauthorized", 401, "Authentication required");

    /// <summary>Authenticated, but the effective role set does not permit this (deny-by-default, AL-06).</summary>
    public static readonly ErrorCode Forbidden = new("forbidden", 403, "Forbidden");

    /// <summary>
    /// This session was displaced by a sign-in on another device (AL-08, Δ MCS-30).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both apps have handled this code since C014 and nothing has ever sent it.</b>
    /// `AuthSessionManager` matches on it, both shells clear their whole back stack for it and
    /// `SessionRevocationTest` covers it — a complete displacement path with no producer, so a
    /// driver whose account was taken over by a new handset saw a generic 401 at their next refresh
    /// and no explanation at all.
    /// </para>
    /// <para>
    /// Distinct from <see cref="Unauthorized"/> on purpose: an expired token is answered by
    /// refreshing, and a displaced one must never be. The apps wipe the local database on this and
    /// route to Login (`mobile_db_schema.md` §0.4), which is exactly what a handset that no longer
    /// belongs to the account should do with an offline copy of a driving licence.
    /// </para>
    /// </remarks>
    public static readonly ErrorCode DeviceRevoked = new("device-revoked", 403, "Signed in on another device");

    public static readonly ErrorCode NotFound = new("not-found", 404, "Resource not found");
    public static readonly ErrorCode MethodNotAllowed = new("method-not-allowed", 405, "Method not allowed");
    public static readonly ErrorCode Conflict = new("conflict", 409, "Conflict");
    public static readonly ErrorCode PayloadTooLarge = new("payload-too-large", 413, "Payload too large");
    public static readonly ErrorCode UnsupportedMediaType = new("unsupported-media-type", 415, "Unsupported media type");
    public static readonly ErrorCode InternalError = new("internal-error", 500, "Internal server error");

    /// <summary>A downstream dependency is unavailable or its circuit is open (D6' §8.3).</summary>
    public static readonly ErrorCode DependencyUnavailable = new("dependency-unavailable", 503, "Dependency unavailable");

    public static readonly ErrorCode ServiceUnavailable = new("service-unavailable", 503, "Service unavailable");

    /// <summary>A downstream call exceeded its timeout budget (D6' §8.3).</summary>
    public static readonly ErrorCode UpstreamTimeout = new("upstream-timeout", 504, "Upstream timeout");

    // ---------------------------------------------------------------------------------------
    // Idempotency (R-14, R-18; D3' §0 "Idempotency"). Kernel-owned.
    // ---------------------------------------------------------------------------------------

    /// <summary><c>Idempotency-Key</c> is mandatory on POST mutations (D3' §0).</summary>
    public static readonly ErrorCode IdempotencyKeyRequired = new("idempotency-key-required", 400, "Idempotency-Key header required");

    /// <summary>Key present but not a ULID/UUID-shaped token of at most 128 characters (D3' §0).</summary>
    public static readonly ErrorCode IdempotencyKeyInvalid = new("idempotency-key-invalid", 400, "Idempotency-Key is malformed");

    /// <summary>Key already used for a <em>different</em> request payload — replay is not possible.</summary>
    public static readonly ErrorCode IdempotencyKeyReuse = new("idempotency-key-reuse", 409, "Idempotency-Key reused with a different request");

    /// <summary>The first request under this key has not finished yet; retry after it completes.</summary>
    public static readonly ErrorCode IdempotencyInProgress = new("idempotency-in-progress", 409, "A request with this Idempotency-Key is still in progress");

    // ---------------------------------------------------------------------------------------
    // Gateway edge (D-30, D-31). Enforced by the YARP gateway (C008); declared here so the
    // codes stay in one registry.
    // ---------------------------------------------------------------------------------------

    /// <summary>Play Integrity / App Attest header missing or invalid (D3' §0, D-30).</summary>
    public static readonly ErrorCode AttestationFailed = new("attestation-failed", 401, "App attestation failed");

    /// <summary>Client below the per-platform minimum version (D3' §0, D-31). Body carries
    /// <c>updateUrl</c>, <c>latestVersion</c>, <c>isMandatory</c>.</summary>
    public static readonly ErrorCode UpgradeRequired = new("upgrade-required", 426, "App upgrade required");

    /// <summary>Generic edge/token-bucket rejection (D3' §0, D3' public-bff family).</summary>
    public static readonly ErrorCode RateLimited = new("rate-limited", 429, "Rate limit exceeded");

    // ---------------------------------------------------------------------------------------
    // iam-svc (D3' §iam-svc)
    // ---------------------------------------------------------------------------------------

    public static readonly ErrorCode InvalidPhone = new("invalid-phone", 400, "Phone number is not a valid +94 number");
    public static readonly ErrorCode OtpExpired = new("otp-expired", 400, "OTP has expired");
    public static readonly ErrorCode InvalidOtp = new("invalid-otp", 401, "OTP is incorrect");
    public static readonly ErrorCode UserBlocked = new("user-blocked", 403, "User is blocked");
    public static readonly ErrorCode AuthNotFound = new("auth-not-found", 404, "Auth attempt not found");
    public static readonly ErrorCode DeviceMismatch = new("device-mismatch", 409, "Device does not match the bound device");

    /// <summary>OTP attempt window locked (D3' §0 "423 locked (OTP attempts)"). Kernel-named.</summary>
    public static readonly ErrorCode OtpLocked = new("otp-locked", 423, "Too many OTP attempts; locked");

    /// <summary>&gt;5 per hour or a resend inside the 60 s cooldown (D-32).</summary>
    public static readonly ErrorCode OtpRateLimited = new("otp-rate-limited", 429, "OTP rate limit exceeded");

    // ---------------------------------------------------------------------------------------
    // registry-svc / provisioning-svc (D3')
    // ---------------------------------------------------------------------------------------

    public static readonly ErrorCode InvalidVehicleType = new("invalid-vehicle-type", 400, "Unknown vehicle type");
    public static readonly ErrorCode CsvInvalid = new("csv-invalid", 400, "CSV could not be parsed");
    public static readonly ErrorCode ModeNotAllowed = new("mode-not-allowed", 403, "Operating mode not allowed on this surface");
    public static readonly ErrorCode NotOwner = new("not-owner", 403, "Caller does not own this resource");
    public static readonly ErrorCode VehicleNotApproved = new("vehicle-not-approved", 403, "Vehicle is not approved");
    public static readonly ErrorCode VehicleNotFound = new("vehicle-not-found", 404, "Vehicle not found");
    public static readonly ErrorCode RegistrationExists = new("registration-exists", 409, "An active registration already exists");
    public static readonly ErrorCode ImeiDuplicate = new("imei-duplicate", 409, "IMEI is already bound; both records quarantined");
    public static readonly ErrorCode TooManyRows = new("too-many-rows", 413, "Too many rows in upload");
    public static readonly ErrorCode BulkInProgress = new("bulk-in-progress", 429, "A bulk job is already running");

    // ---------------------------------------------------------------------------------------
    // trip-state-svc / ride-svc / dispatch-svc (D3')
    // ---------------------------------------------------------------------------------------

    public static readonly ErrorCode InvalidFareToken = new("invalid-fare-token", 400, "Fare token is invalid or expired");
    public static readonly ErrorCode IllegalTransition = new("illegal-transition", 400, "Illegal state transition");
    public static readonly ErrorCode PaymentMethodInvalid = new("payment-method-invalid", 402, "Payment method is not usable for this ride");
    public static readonly ErrorCode InsufficientWallet = new("insufficient-wallet", 402, "Wallet balance is insufficient");
    public static readonly ErrorCode BookingDisabled = new("booking-disabled", 403, "Booking is disabled for this account");
    public static readonly ErrorCode NotOnline = new("not-online", 403, "Driver is not online");
    public static readonly ErrorCode NotRideParticipant = new("not-ride-participant", 403, "Caller is not a participant in this ride");
    public static readonly ErrorCode ActiveRideExists = new("active-ride-exists", 409, "A non-terminal ride already exists");
    public static readonly ErrorCode DriverAlreadyLive = new("driver-already-live", 409, "Driver already has a live session");
    public static readonly ErrorCode OfferAlreadyAccepted = new("offer-already-accepted", 409, "Offer was already accepted by another driver");
    public static readonly ErrorCode RideTerminal = new("ride-terminal", 409, "Ride is in a terminal state");
    public static readonly ErrorCode VersionConflict = new("version-conflict", 409, "Optimistic concurrency conflict");
    public static readonly ErrorCode DirectionalLimitReached = new("directional-limit-reached", 409, "Directional Travel daily use limit reached");
    public static readonly ErrorCode OfferExpired = new("offer-expired", 410, "Offer has expired");

    /// <summary>
    /// The US-5.10 grace window for restarting an auto-ended session has closed (C031).
    /// </summary>
    /// <remarks>
    /// 410 rather than 409: the request was well formed and would have succeeded a minute ago,
    /// which is exactly what Gone means. <c>trip-state.yaml</c> declared the 410 response before
    /// any code existed to carry it.
    /// </remarks>
    public static readonly ErrorCode SessionRestartExpired =
        new("session-restart-expired", 410, "The session restart window has closed");
    public static readonly ErrorCode LocRequestRateLimited = new("loc-request-rate-limited", 429, "Location-request rate limit exceeded");

    // ---------------------------------------------------------------------------------------
    // fare-svc / wallet-svc / fleet-svc (D3')
    // ---------------------------------------------------------------------------------------

    public static readonly ErrorCode InvalidAmount = new("invalid-amount", 400, "Amount is invalid");
    public static readonly ErrorCode UnserviceableArea = new("unserviceable-area", 400, "Area is not serviceable");
    public static readonly ErrorCode GatewayError = new("gateway-error", 402, "Payment gateway returned an error");
    public static readonly ErrorCode MerchantNotOnboarded = new("merchant-not-onboarded", 402, "Driver has no OnePay merchant account");
    public static readonly ErrorCode PaymentAlreadySettled = new("payment-already-settled", 409, "Payment is already settled");
    public static readonly ErrorCode PayoutProfileNotVerified = new("payout-profile-not-verified", 409, "Fleet payout profile is not verified");
    public static readonly ErrorCode RouteUnavailable = new("route-unavailable", 422, "No route could be computed");

    // ---------------------------------------------------------------------------------------
    // fleet-svc — the organisation, its sub-roles and its payout profile (AL-03, AL-49).
    // Coined by C058, which is the first component with code that can raise them; `fleet.yaml`
    // and `_shared.yaml`'s ErrorCode enum carry the same seven.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The organisation has not been approved by a Verification Officer (US-13.A7).
    /// </summary>
    /// <remarks>
    /// A code of its own rather than a bare <see cref="Forbidden"/>, because the Fleet Portal
    /// renders a different screen for it: "we are reviewing your application" is not "you may not
    /// do this", and SCR-FP-002's pending state is the one an operator sees for days.
    /// </remarks>
    public static readonly ErrorCode FleetNotApproved = new("fleet-not-approved", 403, "Fleet organisation is not approved");

    /// <summary>
    /// The caller holds no membership of the organisation named in the path (AL-03).
    /// </summary>
    /// <remarks>
    /// The token's <c>fleet_id</c> claim carries <b>one</b> membership — iam-svc picks the most
    /// privileged when a person belongs to several (C027) — so the claim can never be the
    /// authority on a path-addressed org. This is what a caller gets when the two disagree and
    /// there is no membership row to back the path.
    /// </remarks>
    public static readonly ErrorCode NotFleetMember = new("not-fleet-member", 403, "Caller is not a member of this fleet organisation");

    /// <summary>The caller's sub-role is below what the route requires (US-13.A5).</summary>
    public static readonly ErrorCode FleetRoleInsufficient = new("fleet-role-insufficient", 403, "Fleet sub-role does not permit this");

    public static readonly ErrorCode FleetNotFound = new("fleet-not-found", 404, "Fleet organisation not found");

    /// <summary>The organisation has never submitted a bank and payout profile (AL-49).</summary>
    public static readonly ErrorCode PayoutProfileNotFound = new("payout-profile-not-found", 404, "Fleet payout profile not found");

    /// <summary>
    /// The Colombo business date has already been swept (AL-58).
    /// </summary>
    /// <remarks>
    /// A code of its own rather than a bare <see cref="Conflict"/>, because Finance running the
    /// sweep out of band needs to tell "already done today" apart from anything else that could go
    /// wrong with it — the payout run pays a driver's WHOLE balance, so a second sweep of one day
    /// would raise an empty instruction for every driver it had just emptied.
    /// </remarks>
    public static readonly ErrorCode PayoutBatchExists = new("payout-batch-exists", 409, "This date has already been swept");

    /// <summary>Another live organisation already claims this business registration.</summary>
    public static readonly ErrorCode BusinessRegistrationExists = new("business-registration-exists", 409, "A live organisation already uses this business registration");

    /// <summary>The invited person already holds a sub-role in this organisation (US-13.A5).</summary>
    public static readonly ErrorCode FleetMemberExists = new("fleet-member-exists", 409, "This person is already a member of the organisation");

    /// <summary>
    /// The assignment names somebody who is not a driver on this platform (US-13.2).
    /// </summary>
    /// <remarks>
    /// <b>Δ C059.</b> A code of its own rather than a bare <see cref="NotFound"/>, because US-13.2
    /// has the operator assign "by User ID / phone" and the two failures need different words on
    /// the screen: "no such person" sends them back to the number they typed, while "that person
    /// has never opened the Driver App" is something the driver has to fix before the operator can
    /// do anything. The vehicle's own 404 stays <c>vehicle-not-found</c>, so a portal can tell which
    /// half of the request was wrong.
    /// </remarks>
    public static readonly ErrorCode DriverNotFound = new("driver-not-found", 404, "No such driver");

    /// <summary>
    /// A required AL-50 document slot is missing or unverified, so the vehicle cannot be approved.
    /// </summary>
    /// <remarks>
    /// <b>Δ C059.</b> US-27.3: registration, insurance and revenue licence for every vehicle, plus
    /// a route permit for Mode A. 409 rather than 403 — the officer is entitled to approve
    /// vehicles, and this one becomes approvable the moment the paperwork settles, which is a
    /// conflict with a state rather than a refusal of a right.
    /// </remarks>
    public static readonly ErrorCode DocumentsIncomplete = new("documents-incomplete", 409, "A required document is missing or unverified");

    /// <summary>
    /// The consolidated fleet invoice carries nothing to pay (US-13.10).
    /// </summary>
    /// <remarks>
    /// <b>Δ C060.</b> Raised when <c>POST …/billing/{invoiceId}/pay</c> names an invoice that is
    /// <c>FREE</c> — every vehicle in its first month, or a Mode-A-only fleet, so the total is zero
    /// and no journal entry could balance — or one that has already been settled. A code of its own
    /// rather than a bare <see cref="Conflict"/> because SCR-FP-010 draws a different thing for
    /// each: "already paid" is a receipt to open, "nothing to pay" is a month that cost nothing.
    /// 409 rather than 400 — the request is well formed and would have worked in a different state.
    /// </remarks>
    public static readonly ErrorCode InvoiceNotPayable = new("invoice-not-payable", 409, "This invoice has nothing to pay");

    // ---------------------------------------------------------------------------------------
    // safety-svc / public-bff (D3')
    // ---------------------------------------------------------------------------------------

    public static readonly ErrorCode NoEmergencyContact = new("no-emergency-contact", 400, "No emergency contact on file");
    public static readonly ErrorCode TokenUnknown = new("token-unknown", 404, "Share token is unknown");
    public static readonly ErrorCode TokenExpiredOrRevoked = new("token-expired-or-revoked", 410, "Share token has expired or was revoked");

    /// <summary>
    /// SCR-WT-005 was opened before the journey it summarises finished (US-25.6).
    /// </summary>
    /// <remarks>
    /// <b>Δ C066, and it replaces a code that could not carry the status the contract declares.</b>
    /// <c>public-bff.yaml</c> answers <c>409</c> on this route and listed
    /// <see cref="IllegalTransition"/> beside it — but that entry is <c>400</c> here and is
    /// ride-svc's, where 400 is what D3' prints. One of the two documents had to move, and moving
    /// the shared code's status would turn every one of ride-svc's illegal transitions into a 409.
    /// A plain <see cref="Conflict"/> was the other option and says nothing: the page needs to
    /// distinguish "come back when the trip ends" from any other conflict, because that is the whole
    /// difference between SCR-WT-002 and SCR-WT-005. Micro-change-set raised.
    /// </remarks>
    public static readonly ErrorCode ReceiptNotReady = new("receipt-not-ready", 409, "The journey has not finished yet");

    // ---------------------------------------------------------------------------------------
    // transit-svc — the GTFS Dataset Manager (AL-54, SCR-AP-016). Coined by C007 in
    // `contracts/_shared.yaml`'s ErrorCode enum and declared here by C057, which is the first
    // component with code that can raise them.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A byte-identical feed has already been uploaded (BR-32.1's sha256 refusal).
    /// </summary>
    /// <remarks>
    /// Carries a <c>feedVersionId</c> extension naming the existing version, because SCR-AP-016's
    /// inline error is "This exact file is already uploaded (version N)" — a bare 409 leaves the
    /// operator with no way to go and look at the version they already have.
    /// </remarks>
    public static readonly ErrorCode FeedDuplicate = new("feed-duplicate", 409, "This GTFS file has already been uploaded");

    /// <summary>Activation was asked for on a version that has not passed validation (BR-32.2).</summary>
    public static readonly ErrorCode FeedNotValidated = new("feed-not-validated", 409, "Feed version has not been validated");

    /// <summary>Activation was asked for on the feed that is already live (BR-32.2).</summary>
    public static readonly ErrorCode FeedAlreadyActive = new("feed-already-active", 409, "Feed version is already active");

    // ---------------------------------------------------------------------------------------
    // Registry plumbing
    // ---------------------------------------------------------------------------------------

    private static readonly ConcurrentDictionary<string, ErrorCode> Registry = BuildDeclaredRegistry();

    /// <summary>Every registered code, ordered by key.</summary>
    public static IReadOnlyCollection<ErrorCode> All =>
        Registry.Values.OrderBy(static e => e.Code, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Registers a service-owned code. Idempotent for an identical entry; throws if the same key
    /// is already registered with a different status or title.
    /// </summary>
    public static ErrorCode Register(ErrorCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        Validate(code);

        var existing = Registry.GetOrAdd(code.Code, code);
        if (existing != code)
        {
            throw new InvalidOperationException(
                $"Error code '{code.Code}' is already registered as {existing.Status} \"{existing.Title}\" " +
                $"and cannot be redefined as {code.Status} \"{code.Title}\". Error codes are a public contract.");
        }

        return existing;
    }

    /// <inheritdoc cref="Register(ErrorCode)"/>
    public static ErrorCode Register(string code, int status, string title) =>
        Register(new ErrorCode(code, status, title));

    public static bool TryGet(string code, [NotNullWhen(true)] out ErrorCode? error) =>
        Registry.TryGetValue(code, out error);

    /// <summary>Maps a bare HTTP status to the kernel code services fall back to.</summary>
    public static ErrorCode ForStatus(int status) => status switch
    {
        400 => BadRequest,
        401 => Unauthorized,
        403 => Forbidden,
        404 => NotFound,
        405 => MethodNotAllowed,
        409 => Conflict,
        413 => PayloadTooLarge,
        415 => UnsupportedMediaType,
        426 => UpgradeRequired,
        429 => RateLimited,
        503 => ServiceUnavailable,
        504 => UpstreamTimeout,
        _ => status >= 500 ? InternalError : BadRequest,
    };

    private static ConcurrentDictionary<string, ErrorCode> BuildDeclaredRegistry()
    {
        var registry = new ConcurrentDictionary<string, ErrorCode>(StringComparer.Ordinal);

        foreach (var field in typeof(MageRideErrors).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(ErrorCode) || field.GetValue(null) is not ErrorCode code)
            {
                continue;
            }

            Validate(code);
            if (!registry.TryAdd(code.Code, code))
            {
                throw new InvalidOperationException($"Duplicate error code '{code.Code}' declared in {nameof(MageRideErrors)}.");
            }
        }

        return registry;
    }

    private static void Validate(ErrorCode code)
    {
        if (!KebabCase().IsMatch(code.Code))
        {
            throw new ArgumentException(
                $"Error code '{code.Code}' is not a stable kebab key (lower-case a-z0-9 separated by single hyphens).",
                nameof(code));
        }

        if (code.Status is < 400 or > 599)
        {
            throw new ArgumentException($"Error code '{code.Code}' maps to status {code.Status}; only 4xx/5xx are error codes.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(code.Title))
        {
            throw new ArgumentException($"Error code '{code.Code}' has no title.", nameof(code));
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KebabCase();
}
